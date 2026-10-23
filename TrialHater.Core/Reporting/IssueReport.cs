using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TrialHater.Core.Reporting;

// What a tester says went wrong. Each is a label in the reports repo.
public enum ReportCategory
{
    Stalled,
    NoBelief,
    SolveStalled,
    Other,
}

public static class ReportCategories
{
    public static string Title(this ReportCategory category) => category switch
    {
        ReportCategory.Stalled => "Plugin stalled",
        ReportCategory.NoBelief => "NoBelief: no move played",
        ReportCategory.SolveStalled => "Solving stalled",
        _ => "Other",
    };

    public static string Label(this ReportCategory category) => category switch
    {
        ReportCategory.Stalled => "stalled",
        ReportCategory.NoBelief => "no-belief",
        ReportCategory.SolveStalled => "solve-stalled",
        _ => "other",
    };
}

public sealed record ReportFile(string Name, string Content);

// One report as the plugin sends it to the relay (tools/ReportRelay), which files it as an issue
// in the private reports repo with the files committed next to it. Who sends it travels in the
// request headers (ReportProtocol), so the relay can turn strangers away before reading a body.
public sealed record IssueReport(
    ReportCategory Category,
    string Headline,
    string Description,
    string PluginVersion,
    DateTimeOffset Created,
    IReadOnlyList<ReportFile> Files)
{
    public const int MaxHeadline = 200;
    public const int MaxDescription = 4000;
    public const int MaxFiles = 16;

    private static readonly Regex FileName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    // Why the relay will not file this report, or null.
    public string? Problem()
    {
        if (!Enum.IsDefined(this.Category))
            return "unknown category";
        if (this.Headline is null || this.Headline.Length > MaxHeadline)
            return "headline missing or too long";
        if (this.Description is null || this.Description.Length > MaxDescription)
            return "description missing or too long";
        if (this.PluginVersion is null || this.PluginVersion.Length > 40)
            return "plugin version missing or too long";
        if (this.Files is null || this.Files.Count > MaxFiles)
            return $"more than {MaxFiles} files";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in this.Files)
        {
            if (file?.Name is null || !FileName.IsMatch(file.Name) || file.Name.Contains("..", StringComparison.Ordinal))
                return $"bad file name '{file?.Name}'";
            if (!names.Add(file.Name))
                return $"file '{file.Name}' twice";
            if (file.Content is null)
                return $"file '{file.Name}' has no content";
        }

        return null;
    }
}

// The wire format between plugin and relay: POST Path with the gzipped JSON of an IssueReport.
// The headers name the character (URL-escaped) and carry the install's key: 32 random bytes the
// plugin keeps in reporter.key. The relay pins each allowed character to the first key it sees
// (TesterAllowlist), so a name alone gets nobody in.
public static class ReportProtocol
{
    public const string Path = "/v1/reports";
    public const string NameHeader = "X-TrialHater-Character";
    public const string WorldHeader = "X-TrialHater-World";
    public const string KeyHeader = "X-TrialHater-Key";
    public const int KeyBytes = 32;

    // Compressed request body, and the report once inflated.
    public const long MaxBodyBytes = 6 * 1024 * 1024;
    public const long MaxReportBytes = 32 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string KeyHash(byte[] key) => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

    // Short form of the key's hash, shown to the tester and in the issue.
    public static string Fingerprint(byte[] key) => KeyHash(key)[..12];
}
