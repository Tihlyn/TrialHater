namespace TrialHater.Core.Cards;

// The card list for one game-data version. The plugin fills it from the client's sheets; tests
// and offline tools fill it from research/data/cards.csv.
public sealed class CardCatalog
{
    private readonly Dictionary<ushort, CardInfo> byId;
    private readonly Dictionary<CardStats, List<CardInfo>> byStats;

    public CardCatalog(IEnumerable<CardInfo> cards)
    {
        this.byId = new Dictionary<ushort, CardInfo>();
        this.byStats = new Dictionary<CardStats, List<CardInfo>>();
        foreach (var card in cards)
        {
            this.byId[card.Id] = card;
            if (!this.byStats.TryGetValue(card.Stats, out var list))
                this.byStats[card.Stats] = list = [];
            list.Add(card);
        }
    }

    public int Count => this.byId.Count;

    public IEnumerable<CardInfo> All => this.byId.Values;

    public CardInfo? Get(ushort id) => this.byId.GetValueOrDefault(id);

    // Every card whose stats match what a board slot shows. More than one result is normal for
    // play (they are interchangeable) and only becomes a problem for collection accounting.
    public IReadOnlyList<CardInfo> WithStats(CardStats stats)
        => this.byStats.TryGetValue(stats, out var list) ? list : [];

    // Reads the research export (card_id,number,name,stars,type,top,right,bottom,left,...).
    public static CardCatalog FromCsv(string path)
    {
        var cards = new List<CardInfo>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = Csv.Split(line);
            if (f.Count < 9 || !ushort.TryParse(f[0], out var id))
                continue;
            var type = f[4] switch
            {
                "Primal" => CardType.Primal,
                "Scion" => CardType.Scion,
                "Society" => CardType.Beastman,
                "Garlean" => CardType.Garlean,
                _ => CardType.None,
            };
            var number = ushort.TryParse(f[1].Replace("No. ", string.Empty).Replace("Ex. ", string.Empty), out var n) ? n : (ushort)0;
            var stats = new CardStats(byte.Parse(f[5]), byte.Parse(f[6]), byte.Parse(f[7]), byte.Parse(f[8]), type);
            cards.Add(new CardInfo(id, f[2], byte.Parse(f[3]), stats, number));
        }

        return new CardCatalog(cards);
    }
}

internal static class Csv
{
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
