using TrialHater.Core.Rules;

namespace TrialHater.Core.Cards;

// One TripleTriad sheet row: the NPC always holds every fixed card and fills the rest of the
// five from the variable pool. Rules are the NPC's own; regional rules come on top unless
// UsesRegionalRules is false.
public sealed record NpcDeck(
    uint TriadId,
    string Name,
    IReadOnlyList<ushort> Fixed,
    IReadOnlyList<ushort> Variable,
    IReadOnlyList<TriadRule> Rules,
    bool UsesRegionalRules,
    ushort Fee,
    IReadOnlyList<NpcReward> Rewards)
{
    public int VariablePicks => Math.Max(0, 5 - this.Fixed.Count);
}

// A card the NPC can drop on a win, and the item that carries it (won cards arrive as items).
public sealed record NpcReward(ushort CardId, uint ItemId);

public sealed class NpcCatalog
{
    private readonly Dictionary<uint, NpcDeck> byId;

    public NpcCatalog(IEnumerable<NpcDeck> decks)
    {
        this.byId = decks.ToDictionary(d => d.TriadId);
    }

    public int Count => this.byId.Count;

    public IEnumerable<NpcDeck> All => this.byId.Values;

    public NpcDeck? Get(uint triadId) => this.byId.GetValueOrDefault(triadId);

    // Reads the research export (triple_triad_id,name,fee_mgp,fixed_card_ids,variable_pool_ids,...,rule_ids).
    public static NpcCatalog FromCsv(string path)
    {
        var decks = new List<NpcDeck>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = Csv.Split(line);
            if (f.Count < 8 || !uint.TryParse(f[0], out var id))
                continue;
            decks.Add(new NpcDeck(
                id,
                f[1],
                Ids(f[3]),
                Ids(f[4]),
                f[7].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(r => (TriadRule)byte.Parse(r)).ToList(),
                UsesRegionalRules: true,
                ushort.TryParse(f[2], out var fee) ? fee : (ushort)0,
                []));
        }

        return new NpcCatalog(decks);
    }

    private static List<ushort> Ids(string field)
        => field.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ushort.Parse).ToList();
}
