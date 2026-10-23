using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TrialHater.Core.Reporting;

namespace TrialHater.ReportRelay;

// Files a report in the reports repo: its files in one commit under reports/<month>/<id>/, then
// an issue that links them by commit and quotes the state. The token is a fine-grained one
// limited to that repo (Contents and Issues, read and write).
internal sealed class GitHubReports : IDisposable
{
    // GitHub's limit on an issue body is 65536 characters.
    private const int MaxBody = 60000;
    private const int MaxInlineState = 30000;

    private readonly HttpClient http;
    private readonly string repo;
    private readonly SemaphoreSlim commits = new(1, 1);
    private string? branch;

    public GitHubReports(string repo, string token)
    {
        this.repo = repo;
        this.http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
        this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("TrialHater-ReportRelay");
        this.http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        this.http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<(int Number, string Url, string Folder)> FileAsync(IssueReport report, string name, string world, string fingerprint,
        DateTimeOffset now, CancellationToken cancel)
    {
        var id = $"{now:yyyyMMdd-HHmmss}-{Slug(name)}-{Random.Shared.Next(0x10000):x4}";
        var folder = $"reports/{now:yyyy-MM}/{id}";
        var commit = await this.CommitAsync(folder, report.Files, $"Report {id}: {report.Category.Title()}", cancel);
        var issue = await this.SendAsync(HttpMethod.Post, $"repos/{this.repo}/issues", new JsonObject
        {
            ["title"] = Title(report, name),
            ["body"] = this.Body(report, name, world, fingerprint, now, folder, commit),
            ["labels"] = new JsonArray(report.Category.Label()),
        }, cancel);
        return (issue["number"]!.GetValue<int>(), issue["html_url"]!.GetValue<string>(), folder);
    }

    public void Dispose() => this.http.Dispose();

    // Lower-case letters and digits, words joined by '-'.
    public static string Slug(string text)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "unknown" : slug.Length > 40 ? slug[..40] : slug;
    }

    private static string Title(IssueReport report, string name)
    {
        var headline = string.Join(" ", report.Headline.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var title = $"[{report.Category.Title()}] {headline} ({name})";
        return title.Length <= 250 ? title : title[..250];
    }

    private string Body(IssueReport report, string name, string world, string fingerprint, DateTimeOffset now, string folder, string commit)
    {
        var body = new StringBuilder();
        body.AppendLine($"**{name}** ({world}) · TrialHater {report.PluginVersion} · {report.Created.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC on the reporter's clock, "
                        + $"{now:HH:mm:ss} at the relay · install `{fingerprint}`");
        body.AppendLine();
        if (report.Description.Trim().Length > 0)
        {
            body.AppendLine("### What happened");
            Fence(body, report.Description.Trim());
        }

        if (report.Files.FirstOrDefault(f => f.Name == "state.txt") is { } state)
        {
            body.AppendLine("### State");
            Fence(body, state.Content.Length <= MaxInlineState ? state.Content : state.Content[..MaxInlineState] + "\n[... cut, see state.txt]");
        }

        body.AppendLine("### Files");
        foreach (var file in report.Files)
            body.AppendLine($"- [{file.Name}](https://github.com/{this.repo}/blob/{commit}/{folder}/{file.Name}) ({Size(Encoding.UTF8.GetByteCount(file.Content))})");
        return body.Length <= MaxBody ? body.ToString() : body.ToString(0, MaxBody) + "\n\n[... cut]";
    }

    // A fence longer than any backtick run in the text, so the text cannot close it; mentions and
    // issue references inside a fence stay inert.
    private static void Fence(StringBuilder body, string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        body.AppendLine(fence + "text").AppendLine(text).AppendLine(fence);
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KiB" : $"{bytes / (1024.0 * 1024):0.0} MiB";

    private async Task<string> CommitAsync(string folder, IReadOnlyList<ReportFile> files, string message, CancellationToken cancel)
    {
        await this.commits.WaitAsync(cancel);
        try
        {
            this.branch ??= (await this.SendAsync(HttpMethod.Get, $"repos/{this.repo}", null, cancel))["default_branch"]!.GetValue<string>();
            var entries = new List<JsonObject>();
            foreach (var file in files)
            {
                var blob = await this.SendAsync(HttpMethod.Post, $"repos/{this.repo}/git/blobs", new JsonObject
                {
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content)),
                    ["encoding"] = "base64",
                }, cancel);
                entries.Add(new JsonObject { ["path"] = $"{folder}/{file.Name}", ["mode"] = "100644", ["type"] = "blob", ["sha"] = blob["sha"]!.GetValue<string>() });
            }

            // The relay is the only writer, but someone may push to the repo by hand meanwhile.
            for (var attempt = 1; ; attempt++)
            {
                var head = (await this.SendAsync(HttpMethod.Get, $"repos/{this.repo}/git/ref/heads/{this.branch}", null, cancel))["object"]!["sha"]!.GetValue<string>();
                var baseTree = (await this.SendAsync(HttpMethod.Get, $"repos/{this.repo}/git/commits/{head}", null, cancel))["tree"]!["sha"]!.GetValue<string>();
                var tree = await this.SendAsync(HttpMethod.Post, $"repos/{this.repo}/git/trees", new JsonObject
                {
                    ["base_tree"] = baseTree,
                    ["tree"] = new JsonArray(entries.Select(e => (JsonNode)e.DeepClone()).ToArray()),
                }, cancel);
                var commit = (await this.SendAsync(HttpMethod.Post, $"repos/{this.repo}/git/commits", new JsonObject
                {
                    ["message"] = message,
                    ["tree"] = tree["sha"]!.GetValue<string>(),
                    ["parents"] = new JsonArray(head),
                }, cancel))["sha"]!.GetValue<string>();
                try
                {
                    await this.SendAsync(HttpMethod.Patch, $"repos/{this.repo}/git/refs/heads/{this.branch}", new JsonObject { ["sha"] = commit, ["force"] = false }, cancel);
                    return commit;
                }
                catch (GitHubException ex) when (ex.Status == 422 && attempt < 3)
                {
                    // Not a fast-forward any more: build the commit again on the new head.
                }
            }
        }
        finally
        {
            this.commits.Release();
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? content, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, path);
        if (content is not null)
            request.Content = JsonContent.Create(content);
        using var response = await this.http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new GitHubException((int)response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {(text.Length > 300 ? text[..300] : text)}");
        return JsonNode.Parse(text)!;
    }

    private sealed class GitHubException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }
}
