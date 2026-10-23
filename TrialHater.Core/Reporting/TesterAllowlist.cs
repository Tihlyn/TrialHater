using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrialHater.Core.Reporting;

public sealed class TesterEntry
{
    public string Name { get; set; } = string.Empty;

    // Set by hand to require a world, or pinned with the key.
    public string? World { get; set; }

    // SHA-256 of the install key (ReportProtocol.KeyHash), pinned on first use. Remove it (and
    // World) to let the character pin a new install.
    public string? KeyHash { get; set; }

    public DateTimeOffset? Pinned { get; set; }
}

public enum Admission
{
    Unknown,
    Admitted,
    Pinned,
    OtherKey,
    OtherWorld,
}

// The relay's tester list (allowlist.json): the characters that may file reports. A listed
// character is pinned to the first install key and world it reports with; after that only that
// key gets in, so knowing a tester's name is not enough. Not thread-safe; the relay locks.
public sealed class TesterAllowlist
{
    private static readonly JsonSerializerOptions Options = new(ReportProtocol.Json) { WriteIndented = true };

    public List<TesterEntry> Testers { get; set; } = [];

    public static TesterAllowlist Parse(string json)
        => JsonSerializer.Deserialize<TesterAllowlist>(json, Options) ?? new TesterAllowlist();

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    // Pinned changes the list: the caller saves it.
    public (Admission Result, TesterEntry? Entry) Admit(string name, string world, string keyHash, DateTimeOffset now)
    {
        var entry = this.Testers.FirstOrDefault(t => string.Equals(t.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return (Admission.Unknown, null);
        if (entry.World is { Length: > 0 } pinnedWorld && !string.Equals(pinnedWorld, world.Trim(), StringComparison.OrdinalIgnoreCase))
            return (Admission.OtherWorld, entry);
        if (entry.KeyHash is not { Length: > 0 } pinnedKey)
        {
            entry.KeyHash = keyHash;
            entry.World = world.Trim();
            entry.Pinned = now;
            return (Admission.Pinned, entry);
        }

        var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pinnedKey), Encoding.ASCII.GetBytes(keyHash));
        return (same ? Admission.Admitted : Admission.OtherKey, entry);
    }
}
