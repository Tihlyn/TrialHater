using TrialHater.Core.Rules;

namespace TrialHater.Core.Tournament;

public sealed record ScheduledTournament(DateTimeOffset StartUtc, int Row, RuleSet Rules);

// The Open Tournament rotation (docs/MODEL_MAPPING.md, session 5). A tournament starts every two
// hours, on odd UTC hours; the `TripleTriadTournament` sheet holds the 10 rule sets and the rows
// rotate in order: row = ((start unix seconds / 7200) + 3) % 10 + 1, fitted to the in-game
// schedule window of 2026-10-20.
public static class OpenTournament
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    private const long Offset = 3600;

    // `rows[i]` = rules of sheet row i + 1.
    public static IEnumerable<ScheduledTournament> Upcoming(IReadOnlyList<RuleSet> rows, DateTimeOffset now, int count)
    {
        if (rows.Count == 0)
            yield break;
        // Slot k starts at k * 7200 + 3600 (odd UTC hours); the first one listed is the next start.
        var interval = (long)Interval.TotalSeconds;
        var slot = (now.ToUnixTimeSeconds() - Offset + interval - 1) / interval;
        for (var i = 0; i < count; i++, slot++)
        {
            var row = (int)((slot + 3) % rows.Count) + 1;
            yield return new ScheduledTournament(DateTimeOffset.FromUnixTimeSeconds((slot * interval) + Offset), row, rows[row - 1]);
        }
    }
}
