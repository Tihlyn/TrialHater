using System.IO.Compression;
using System.Text.Json;
using TrialHater.Core.Reporting;

namespace TrialHater.ReportRelay;

// One report request: who is asking (headers, checked against the tester list before the body is
// read), the brakes, the body, then GitHub. Every refusal says why in the reply and in relay.log;
// a report GitHub would not take is kept under failed/ so it can be filed by hand.
internal sealed class Relay
{
    private readonly string allowlistPath;
    private readonly string failedFolder;
    private readonly GitHubReports github;
    private readonly RelayLog log;
    private readonly ReportRateLimit limits = new();
    private readonly object allowlistGate = new();
    private TesterAllowlist? allowlist;
    private DateTime allowlistStamp;

    public Relay(string allowlistPath, string failedFolder, GitHubReports github, RelayLog log)
    {
        this.allowlistPath = allowlistPath;
        this.failedFolder = failedFolder;
        this.github = github;
        this.log = log;
    }

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (this.limits.Blocked(address, now))
            return Reply(StatusCodes.Status429TooManyRequests, "too many refused requests from this address; try again in an hour");

        var headers = context.Request.Headers;
        var name = Unescape(headers[ReportProtocol.NameHeader].ToString());
        var world = Unescape(headers[ReportProtocol.WorldHeader].ToString());
        var key = Key(headers[ReportProtocol.KeyHeader].ToString());
        if (name is not { Length: > 0 and <= 40 } || world is not { Length: > 0 and <= 40 } || key is null)
            return this.Refuse(address, now, StatusCodes.Status400BadRequest, "missing or malformed reporter headers");

        var keyHash = ReportProtocol.KeyHash(key);
        var fingerprint = ReportProtocol.Fingerprint(key);
        var who = $"{name}@{world} ({fingerprint}, {address})";
        var (admission, _) = this.Admit(name, world, keyHash, now);
        switch (admission)
        {
            case Admission.Unknown:
                return this.Refuse(address, now, StatusCodes.Status403Forbidden, "this character is not on the tester list", who);
            case Admission.OtherKey:
                return this.Refuse(address, now, StatusCodes.Status403Forbidden, "this character is registered to another install of the plugin", who);
            case Admission.OtherWorld:
                return this.Refuse(address, now, StatusCodes.Status403Forbidden, "this character is registered on another world", who);
            case Admission.Pinned:
                this.log.Write($"pinned {who}");
                break;
        }

        if (this.limits.TryTake(keyHash, now) is { } wait)
        {
            this.log.Write($"rate limited {who}: {wait}");
            return Reply(StatusCodes.Status429TooManyRequests, wait);
        }

        byte[] body;
        IssueReport? report;
        try
        {
            body = await ReadBody(context.Request, context.RequestAborted);
            report = JsonSerializer.Deserialize<IssueReport>(Inflate(body, context.Request.Headers.ContentEncoding.ToString()), ReportProtocol.Json);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Reply(StatusCodes.Status413PayloadTooLarge, $"the report is over {ReportProtocol.MaxBodyBytes / (1024 * 1024)} MiB compressed");
        }
        catch (ReportTooLargeException)
        {
            return Reply(StatusCodes.Status413PayloadTooLarge, $"the report is over {ReportProtocol.MaxReportBytes / (1024 * 1024)} MiB");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            this.log.Write($"unreadable report from {who}: {ex.Message}");
            return Reply(StatusCodes.Status400BadRequest, "the report could not be read");
        }

        if ((report is null ? "empty report" : report.Problem()) is { } why)
        {
            this.log.Write($"rejected report from {who}: {why}");
            return Reply(StatusCodes.Status400BadRequest, why);
        }

        var accepted = report!;
        try
        {
            var filed = await this.github.FileAsync(accepted, name, world, fingerprint, now, CancellationToken.None);
            this.log.Write($"filed #{filed.Number} ({accepted.Category}) from {who}: {filed.Folder}");
            return Results.Json(new ReportReply(filed.Number, filed.Url, null), ReportProtocol.Json, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            var kept = this.Keep(body, name, now);
            this.log.Write($"GitHub did not take the report from {who}: {ex.Message}; kept as {kept}");
            return Reply(StatusCodes.Status502BadGateway, "GitHub did not take the report; the relay kept it for filing by hand");
        }
    }

    private (Admission Result, TesterEntry? Entry) Admit(string name, string world, string keyHash, DateTimeOffset now)
    {
        lock (this.allowlistGate)
        {
            try
            {
                // Read again whenever the file changes, so editing it needs no restart.
                var stamp = File.GetLastWriteTimeUtc(this.allowlistPath);
                if (this.allowlist is null || stamp != this.allowlistStamp)
                {
                    this.allowlist = TesterAllowlist.Parse(File.ReadAllText(this.allowlistPath));
                    this.allowlistStamp = stamp;
                }
            }
            catch (Exception ex)
            {
                this.log.Write($"could not read {this.allowlistPath}: {ex.Message}; refusing everyone");
                this.allowlist = null;
                return (Admission.Unknown, null);
            }

            var result = this.allowlist.Admit(name, world, keyHash, now);
            if (result.Result == Admission.Pinned)
            {
                var temp = this.allowlistPath + ".tmp";
                File.WriteAllText(temp, this.allowlist.ToJson());
                File.Move(temp, this.allowlistPath, overwrite: true);
                this.allowlistStamp = File.GetLastWriteTimeUtc(this.allowlistPath);
            }

            return result;
        }
    }

    private IResult Refuse(string address, DateTimeOffset now, int status, string why, string? who = null)
    {
        this.limits.Failed(address, now);
        this.log.Write($"refused {who ?? address}: {why}");
        return Reply(status, why);
    }

    private string Keep(byte[] body, string name, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(this.failedFolder);
            var path = Path.Combine(this.failedFolder, $"{now:yyyyMMdd-HHmmss}-{GitHubReports.Slug(name)}.json.gz");
            File.WriteAllBytes(path, body);
            return path;
        }
        catch (Exception ex)
        {
            return $"nothing ({ex.Message})";
        }
    }

    private static IResult Reply(int status, string error)
        => Results.Json(new ReportReply(0, null, error), ReportProtocol.Json, statusCode: status);

    private static string? Unescape(string value)
    {
        try
        {
            return value.Length == 0 ? null : Uri.UnescapeDataString(value).Trim();
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static byte[]? Key(string value)
    {
        var key = new byte[ReportProtocol.KeyBytes];
        return Convert.TryFromBase64String(value, key, out var length) && length == ReportProtocol.KeyBytes ? key : null;
    }

    // Kestrel stops a body over MaxRequestBodySize.
    private static async Task<byte[]> ReadBody(HttpRequest request, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }

    private static byte[] Inflate(byte[] body, string encoding)
    {
        if (encoding.Length == 0)
            return body;
        if (!encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"unsupported content encoding '{encoding}'");
        using var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = gzip.Read(chunk)) > 0)
        {
            if (output.Length + read > ReportProtocol.MaxReportBytes)
                throw new ReportTooLargeException();
            output.Write(chunk, 0, read);
        }

        return output.ToArray();
    }

    private sealed class ReportTooLargeException : Exception;
}
