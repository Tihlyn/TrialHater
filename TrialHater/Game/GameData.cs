using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace TrialHater.Game;

// Card, NPC and rule data from the installed client's sheets, so the plugin always matches the
// game version it runs on. Built once on a worker thread at load (ENpcBase is large).
public sealed class GameData
{
    // ENpcData values in this range are TripleTriad sheet rows (event handler type 0x23).
    private const uint TriadHandlerType = 0x23;

    // Addon sheet row of the NPC menu entry "Triple Triad Challenge" (2026-10-20).
    private const uint TriadMenuTextRow = 9184;

    private GameData(CardCatalog cards, NpcCatalog npcs, Dictionary<uint, uint> npcBaseToTriad, Dictionary<TriadRule, string> ruleNames, List<RuleSet> tournamentRules, string triadMenuText)
    {
        this.TriadMenuText = triadMenuText;
        this.Cards = cards;
        this.Npcs = npcs;
        this.NpcBaseToTriad = npcBaseToTriad;
        this.RuleNames = ruleNames;
        this.TournamentRules = tournamentRules;
    }

    public CardCatalog Cards { get; }

    public NpcCatalog Npcs { get; }

    // ENpcBase row id (a world NPC's BaseId) -> TripleTriad row id.
    public IReadOnlyDictionary<uint, uint> NpcBaseToTriad { get; }

    // Rule names in the client language, as the match windows print them.
    public IReadOnlyDictionary<TriadRule, string> RuleNames { get; }

    // The NPC menu entry that starts a match, in the client language. Most menus also mark it with
    // icon 71101-71104, but some (Kaizan, Gyoei) show no icons at all.
    public string TriadMenuText { get; }

    // TripleTriadTournament rows 1..N (index 0 = row 1): the Open Tournament rotation.
    public IReadOnlyList<RuleSet> TournamentRules { get; }

    public NpcDeck? NpcForBase(uint baseId)
        => this.NpcBaseToTriad.TryGetValue(baseId, out var triad) ? this.Npcs.Get(triad) : null;

    // The deck of the only NPC with this name, as the board shows it (AtkValues[4]); null when no
    // NPC or several share the name.
    public NpcDeck? NpcByName(string name)
    {
        var matches = this.Npcs.All.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // Exact rule-name match, plus "Roulette (Same)" style text once Roulette has resolved.
    public IEnumerable<TriadRule> ParseRuleText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            yield break;
        foreach (var (rule, name) in this.RuleNames)
        {
            if (trimmed.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                yield return rule;
                yield break;
            }
        }

        var open = trimmed.IndexOf('(');
        var close = trimmed.LastIndexOf(')');
        if (open <= 0 || close <= open)
            yield break;
        var inner = trimmed[(open + 1)..close].Trim();
        foreach (var (rule, name) in this.RuleNames)
        {
            if (inner.Equals(name, StringComparison.OrdinalIgnoreCase) && rule != TriadRule.Roulette)
            {
                // The resolved rule replaces Roulette; Roulette itself is not reported.
                yield return rule;
                yield break;
            }
        }
    }

    public static GameData Load(IDataManager data)
    {
        var names = data.GetExcelSheet<TripleTriadCard>();
        var cards = new List<CardInfo>();
        foreach (var row in data.GetExcelSheet<TripleTriadCardResident>())
        {
            if (row.Top == 0 || names.GetRowOrDefault(row.RowId) is not { } named)
                continue;
            var name = named.Name.ExtractText();
            if (name.Length == 0)
                continue;
            var stars = row.TripleTriadCardRarity.ValueNullable?.Stars ?? 0;
            var type = (CardType)Math.Min(row.TripleTriadCardType.RowId, 4u);
            cards.Add(new CardInfo((ushort)row.RowId, name, stars, new CardStats(row.Top, row.Right, row.Bottom, row.Left, type), row.Order));
        }

        var baseToTriad = new Dictionary<uint, uint>();
        var triadNames = new Dictionary<uint, string>();
        var residents = data.GetExcelSheet<ENpcResident>();
        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            foreach (var entry in npc.ENpcData)
            {
                if (entry.RowId >> 16 != TriadHandlerType)
                    continue;
                baseToTriad[npc.RowId] = entry.RowId;
                if (!triadNames.ContainsKey(entry.RowId) && residents.GetRowOrDefault(npc.RowId) is { } resident)
                    triadNames[entry.RowId] = resident.Singular.ExtractText();
            }
        }

        // A reward item's AdditionalData is its card (all 136 rewarding NPCs, checked against
        // ffxivcollect 2026-10-22).
        var items = data.GetExcelSheet<Item>();
        var catalog = new CardCatalog(cards);
        var decks = new List<NpcDeck>();
        foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.TripleTriad>())
        {
            var fixedCards = row.TripleTriadCardFixed.Select(r => (ushort)r.RowId).Where(id => id != 0).ToList();
            var variable = row.TripleTriadCardVariable.Select(r => (ushort)r.RowId).Where(id => id != 0).ToList();
            if (fixedCards.Count + variable.Count == 0)
                continue;
            var rules = row.TripleTriadRule.Select(r => (TriadRule)r.RowId).Where(r => r != TriadRule.None).ToList();
            var rewards = row.ItemPossibleReward.Select(r => r.RowId).Where(id => id != 0)
                .Select(id => (Item: id, Card: items.GetRowOrDefault(id)?.AdditionalData.RowId ?? 0))
                .Where(r => r.Card is > 0 and <= ushort.MaxValue && catalog.Get((ushort)r.Card) is not null)
                .Select(r => new NpcReward((ushort)r.Card, r.Item)).ToList();
            decks.Add(new NpcDeck(row.RowId, triadNames.GetValueOrDefault(row.RowId, $"#{row.RowId}"),
                fixedCards, variable, rules, row.UsesRegionalRules, row.Fee, rewards));
        }

        var ruleNames = new Dictionary<TriadRule, string>();
        foreach (var rule in data.GetExcelSheet<TripleTriadRule>())
        {
            var name = rule.Name.ExtractText();
            if (rule.RowId is > 0 and <= (uint)TriadRule.Draft && name.Length > 0)
                ruleNames[(TriadRule)rule.RowId] = name;
        }

        // Unnamed columns: Unknown0..3 are TripleTriadRule ids (0 = none).
        var tournamentRules = new List<RuleSet>();
        foreach (var row in data.GetExcelSheet<TripleTriadTournament>())
        {
            if (row.RowId == 0)
                continue;
            var ids = new[] { Convert.ToInt32(row.Unknown0), Convert.ToInt32(row.Unknown1), Convert.ToInt32(row.Unknown2), Convert.ToInt32(row.Unknown3) };
            tournamentRules.Add(RuleSet.Of(ids.Where(id => id is > 0 and <= (int)TriadRule.Draft).Select(id => (TriadRule)id)));
        }

        var menuText = data.GetExcelSheet<Addon>().GetRowOrDefault(TriadMenuTextRow)?.Text.ExtractText() ?? string.Empty;
        return new GameData(catalog, new NpcCatalog(decks), baseToTriad, ruleNames, tournamentRules, menuText);
    }
}
