using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Deck;

public static class DeckRules
{
    public const int Size = 5;

    // The game's deck limits, as every lineage implementation has them (TriadBuddy TriadDeck.cs:117,
    // Saucy, ATTG): at most one 5-star card, at most two cards of 4 stars or more, no card twice.
    public static string? Problem(IReadOnlyList<CardInfo> cards)
    {
        if (cards.Count != Size)
            return $"a deck holds {Size} cards";
        if (cards.Select(c => c.Id).Distinct().Count() != Size)
            return "a card appears twice";
        if (cards.Count(c => c.Stars >= 5) > 1)
            return "more than one 5-star card";
        if (cards.Count(c => c.Stars >= 4) > 2)
            return "more than two cards of 4 stars or more";
        return null;
    }

    // The rules a deck is evaluated under. Roulette's roll is unknown in advance and Swap, Sudden
    // Death and the Open rules do not change a full-information game, so they are dropped. Random
    // deals from the whole collection: no deck matters then (the caller says so).
    public static RuleSet ForEvaluation(RuleSet rules)
        => RuleSet.Of(rules.Rules.Where(r => r is TriadRule.Same or TriadRule.Plus or TriadRule.Reverse or TriadRule.FallenAce
            or TriadRule.Ascension or TriadRule.Descension or TriadRule.Order or TriadRule.Chaos));

    // The rules a match against `npc` will most likely have. The sheet lacks regional rules, so the
    // recent matches against it decide: a rule present in all of them stays (sheet + regional),
    // one that comes and goes (a Roulette roll) does not. `history` is newest last.
    public static (RuleSet Rules, string Source) ExpectedRules(NpcDeck npc, IReadOnlyList<RuleSet> history)
    {
        var sheet = RuleSet.Of(npc.Rules);
        if (history.Count == 0)
        {
            return (sheet, npc.UsesRegionalRules
                ? "sheet rules; regional rules unknown until a match against it is recorded"
                : "sheet rules (no regional rules)");
        }

        var recent = history.TakeLast(10).ToList();
        var common = recent.Aggregate(ushort.MaxValue, (mask, r) => (ushort)(mask & r.Mask));
        return (new RuleSet(common), $"rules seen in all of the last {recent.Count} match(es)");
    }

    // Every hand the NPC can be dealt: all its fixed cards plus each combination of variable picks.
    // Hand order is the sheet order (it only matters under Order, where the NPC's order is unseen
    // and searched as random anyway).
    public static List<CardStats[]> NpcHands(NpcDeck npc, CardCatalog catalog)
    {
        var fixedCards = npc.Fixed.Select(id => catalog.Get(id)?.Stats).OfType<CardStats>().ToList();
        var variable = npc.Variable.Select(id => catalog.Get(id)?.Stats).OfType<CardStats>().ToList();
        var picks = Math.Min(Size - fixedCards.Count, variable.Count);
        var hands = new List<CardStats[]>();
        if (picks <= 0)
        {
            if (fixedCards.Count >= Size)
                hands.Add([.. fixedCards.Take(Size)]);
            return hands;
        }

        foreach (var combo in Combinations(variable.Count, picks))
            hands.Add([.. fixedCards, .. combo.Select(i => variable[i])]);
        return hands;
    }

    internal static IEnumerable<int[]> Combinations(int n, int k)
    {
        var index = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return [.. index];
            var i = k - 1;
            while (i >= 0 && index[i] == n - k + i)
                i--;
            if (i < 0)
                yield break;
            index[i]++;
            for (var j = i + 1; j < k; j++)
                index[j] = index[j - 1] + 1;
        }
    }
}
