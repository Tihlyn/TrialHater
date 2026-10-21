using System.Globalization;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Tournament;

// One Open Tournament we played: the deck we drafted, what the advisor expected of it (null when
// the advice did not cover it), whether it was the advised deck, and how we placed among the 8.
public sealed record DraftResult(
    DateTimeOffset Tournament,
    RuleSet Rules,
    IReadOnlyList<ushort> Deck,
    double? Expected,
    bool Advised,
    int Rank,
    int Points,
    int Captures)
{
    // Vetted: the deck placed in the top half; otherwise it is discarded.
    public bool Vetted => this.Rank is > 0 and <= DraftRecord.VettedRank;
}

// Our drafted decks and how each tournament went, one character's (the plugin keeps a file per
// character): picks are vetted or discarded by the results. The advisor's simulation stays the
// ranking; the record only decides between decks the simulation cannot tell apart
// (DraftAdvisor.TieMargin), and the draft panel shows it next to each option.
public sealed class DraftRecord
{
    public const int VettedRank = 4;
    public const string Header = "tournament_utc,rules_mask,deck,expected,advised,rank,points,captures";

    private readonly List<DraftResult> results = [];

    public IReadOnlyList<DraftResult> Results => this.results;

    // False when that tournament is already recorded (a results window still up after a reload).
    public bool Add(DraftResult result)
    {
        if (this.results.Any(r => r.Tournament == result.Tournament && r.Deck.SequenceEqual(result.Deck)))
            return false;
        this.results.Add(result);
        return true;
    }

    // For each card of the deck that we drafted before under the same rules: tournaments it was
    // vetted in minus tournaments it was discarded in.
    public int Track(RuleSet rules, IEnumerable<ushort> cards)
    {
        var score = 0;
        foreach (var card in cards)
        {
            foreach (var r in this.results)
            {
                if (r.Rules.Mask == rules.Mask && r.Deck.Contains(card))
                    score += r.Vetted ? 1 : -1;
            }
        }

        return score;
    }

    // (vetted, discarded) tournaments of one card under the rules.
    public (int Vetted, int Discarded) Card(RuleSet rules, ushort card)
    {
        var mine = this.results.Where(r => r.Rules.Mask == rules.Mask && r.Deck.Contains(card)).ToList();
        return (mine.Count(r => r.Vetted), mine.Count(r => !r.Vetted));
    }

    public static string Line(DraftResult r)
        => string.Join(",",
            r.Tournament.UtcDateTime.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture),
            r.Rules.Mask.ToString(CultureInfo.InvariantCulture),
            string.Join(";", r.Deck),
            r.Expected is { } e ? e.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
            r.Advised ? "1" : "0",
            r.Rank.ToString(CultureInfo.InvariantCulture),
            r.Points.ToString(CultureInfo.InvariantCulture),
            r.Captures.ToString(CultureInfo.InvariantCulture));

    // Lines that do not parse are skipped.
    public static DraftRecord Parse(IEnumerable<string> lines)
    {
        var record = new DraftRecord();
        foreach (var line in lines)
        {
            var f = line.Split(',');
            if (f.Length < 8 || f[0] == "tournament_utc"
                || !DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                || !ushort.TryParse(f[1], out var mask)
                || !int.TryParse(f[5], out var rank) || !int.TryParse(f[6], out var points) || !int.TryParse(f[7], out var captures))
                continue;
            var deck = f[2].Split(';').Select(id => ushort.TryParse(id, out var v) ? v : (ushort)0).ToList();
            if (deck.Count != 5 || deck.Contains((ushort)0))
                continue;
            double? expected = double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null;
            record.Add(new DraftResult(at, new RuleSet(mask), deck, expected, f[4] == "1", rank, points, captures));
        }

        return record;
    }
}
