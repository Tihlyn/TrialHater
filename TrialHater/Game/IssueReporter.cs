using System.Security.Cryptography;
using TrialHater.Core.Reporting;

namespace TrialHater.Game;

// "Report an issue". The window asks; on the next framework tick the plugin copies its state
// (ReportSnapshot) and hands it to Send. A worker adds the plugin's lines from dalamud.log and the
// recent records, keeps a copy under reports/ and sends it to the relay on the compute box
// (tools/ReportRelay, docs/REPORTING.md), which files an issue in the private reports repo. The
// relay knows the tester by character name and this install's key (reporter.key, made on first
// use).
public sealed class IssueReporter : IDisposable
{
    private const string Endpoint = "https://146.59.253.111:8443/v1/reports";

    // `ReportRelay pin relay.pfx`; changes only with a new relay certificate.
    private const string RelayPin = "RFi15dgTEvAh8pI47ggnWXt1CqqMBDopaw1xzKpW9nI=";

    private const int KeepReports = 20;
    private const int LogLines = 3000;
    private const long LogBytes = 24 * 1024 * 1024;

    private readonly string configFolder;
    private readonly string dalamudLog;
    private readonly Action<string> log;
    private readonly ReportClient client = new(new Uri(Endpoint), RelayPin, TimeSpan.FromSeconds(60));
    private readonly CancellationTokenSource disposing = new();
    private (ReportCategory Category, string Description)? requested;
    private byte[]? key;
    private volatile bool sending;
    private volatile bool filed;
    private volatile string status = string.Empty;

    public IssueReporter(string configFolder, Action<string> log)
    {
        this.configFolder = configFolder;
        this.log = log;
        // pluginConfigs/TrialHater sits two levels under the folder dalamud.log is in.
        var root = Directory.GetParent(configFolder)?.Parent?.FullName ?? configFolder;
        this.dalamudLog = Path.Combine(root, "dalamud.log");
    }

    public bool Busy => this.sending || this.requested is not null;

    public string Status => this.status;

    // The last report reached the reports repo.
    public bool Filed => this.filed;

    public string Fingerprint => this.Key() is { } k ? ReportProtocol.Fingerprint(k) : "no key";

    public void Request(ReportCategory category, string description)
    {
        if (this.Busy)
            return;
        this.requested = (category, description.Trim());
        this.filed = false;
        this.status = "gathering the report...";
    }

    // Framework thread: the report the window asked for, once.
    public (ReportCategory Category, string Description)? TakeRequest()
    {
        var request = this.requested;
        this.requested = null;
        return request;
    }

    // Framework thread. `files` were copied from the game this tick; the rest is read on a worker.
    public void Send(ReportCategory category, string description, string headline, string name, string world, List<ReportFile> files, bool probeOn)
    {
        if (name.Length == 0 || world.Length == 0)
        {
            this.status = "not sent: log in to a character first (the relay knows testers by character)";
            return;
        }

        if (this.Key() is not { } key)
        {
            this.status = $"not sent: {Path.Combine(this.configFolder, "reporter.key")} is unreadable; delete it and ask for your character to be reset on the relay";
            return;
        }

        this.sending = true;
        this.status = "sending...";
        var cancel = this.disposing.Token;
        Task.Run(async () =>
        {
            var folder = "-";
            try
            {
                files.Add(new ReportFile("log.txt", string.Join("\n", LogExcerpt.Select(LogExcerpt.Tail(this.dalamudLog, int.MaxValue, LogBytes), "TrialHater", LogLines))));
                files.Add(new ReportFile("matches.csv", this.Records("matches.csv", 60, keepHeader: true)));
                files.Add(new ReportFile("npc_moves.jsonl", this.Records("npc_moves.jsonl", 30)));
                var probe = Path.Combine(this.configFolder, "model_probe.jsonl");
                if (probeOn && File.Exists(probe) && DateTime.UtcNow - File.GetLastWriteTimeUtc(probe) < TimeSpan.FromHours(2))
                    files.Add(new ReportFile("model_probe.jsonl", this.Records("model_probe.jsonl", 30)));
                var report = new IssueReport(category, headline.Length <= IssueReport.MaxHeadline ? headline : headline[..IssueReport.MaxHeadline],
                    description.Length <= IssueReport.MaxDescription ? description : description[..IssueReport.MaxDescription],
                    Plugin.VersionText, DateTimeOffset.Now, files);
                folder = this.Keep(report);
                var result = await this.client.SendAsync(report, new Reporter(name, world, key), cancel);
                File.WriteAllText(Path.Combine(folder, "result.txt"), result.Filed ? $"filed as #{result.Number}: {result.Message}" : result.Message);
                this.filed = result.Filed;
                this.status = result.Filed ? $"filed as report #{result.Number}. Thank you!" : $"not sent: {result.Message}. A copy is in {folder}";
                this.log(result.Filed ? $"[Report] filed #{result.Number} ({category})" : $"[Report] not sent: {result.Message} (copy in {folder})");
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                this.status = $"not sent: {ex.GetType().Name}: {ex.Message}. A copy may be in {folder}";
                this.log($"[Report] failed: {ex}");
            }
            finally
            {
                this.sending = false;
            }
        }, cancel);
    }

    public void Dispose()
    {
        this.disposing.Cancel();
        this.client.Dispose();
    }

    private byte[]? Key()
    {
        if (this.key is not null)
            return this.key;
        var path = Path.Combine(this.configFolder, "reporter.key");
        try
        {
            if (File.Exists(path))
            {
                // A damaged key is not replaced: the relay has the old one pinned.
                var existing = new byte[ReportProtocol.KeyBytes];
                return Convert.TryFromBase64String(File.ReadAllText(path).Trim(), existing, out var length) && length == existing.Length
                    ? this.key = existing
                    : null;
            }

            var made = RandomNumberGenerator.GetBytes(ReportProtocol.KeyBytes);
            Directory.CreateDirectory(this.configFolder);
            File.WriteAllText(path, Convert.ToBase64String(made));
            return this.key = made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.log($"[Report] could not read or write {path}: {ex.Message}");
            return null;
        }
    }

    // The last lines of one of our records (header kept for the csv).
    private string Records(string file, int lines, bool keepHeader = false)
    {
        var path = Path.Combine(this.configFolder, file);
        var tail = LogExcerpt.Tail(path, lines, 8 * 1024 * 1024);
        if (keepHeader && tail.Count > 0 && File.ReadLines(path).FirstOrDefault() is { } header && tail[0] != header)
            tail.Insert(0, header);
        return string.Join("\n", tail);
    }

    // reports/<time>-<category>/ with the files as sent; only the last KeepReports are kept.
    private string Keep(IssueReport report)
    {
        var root = Path.Combine(this.configFolder, "reports");
        var folder = Path.Combine(root, $"{report.Created:yyyyMMdd-HHmmss}-{report.Category.Label()}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "report.txt"), $"{report.Category.Title()}\n{report.Headline}\n{report.PluginVersion} {report.Created:O}\n\n{report.Description}\n");
        foreach (var file in report.Files)
            File.WriteAllText(Path.Combine(folder, file.Name), file.Content);
        foreach (var old in Directory.GetDirectories(root).Order(StringComparer.Ordinal).SkipLast(KeepReports))
        {
            try
            {
                Directory.Delete(old, true);
            }
            catch (IOException)
            {
                // Open in an editor: it goes next time.
            }
        }

        return folder;
    }
}
