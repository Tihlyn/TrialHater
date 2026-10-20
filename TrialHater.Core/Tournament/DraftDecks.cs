using TrialHater.Core.Cards;

namespace TrialHater.Core.Tournament;

// Drafted decks seen in tournament results, as lines "tournament_utc,player,id;id;id;id;id"
// (research/data/draft-decks.csv, and the plugin's tournament_decks.csv). They are the
// DraftAdvisor's picture of what the field drafts.
public static class DraftDecks
{
    public const string Header = "tournament_utc,player,card_ids";

    // Decks of five known cards; lines that do not parse are skipped.
    public static List<CardStats[]> Parse(IEnumerable<string> lines, CardCatalog catalog)
    {
        var decks = new List<CardStats[]>();
        foreach (var line in lines)
        {
            var fields = line.Split(',');
            if (fields.Length < 3 || fields[0] == "tournament_utc")
                continue;
            var cards = fields[^1].Split(';').Select(id => ushort.TryParse(id, out var v) ? catalog.Get(v) : null).ToList();
            if (cards.Count == 5 && cards.All(c => c is not null))
                decks.Add(cards.Select(c => c!.Stats).ToArray());
        }

        return decks;
    }

    public static string Line(DateTimeOffset tournament, string player, IEnumerable<ushort> ids)
        => $"{tournament.UtcDateTime:yyyy-MM-ddTHH:mmZ},{player.Replace(',', ' ')},{string.Join(";", ids)}";
}
