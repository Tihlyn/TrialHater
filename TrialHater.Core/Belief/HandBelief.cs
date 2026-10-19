using TrialHater.Core.Cards;
using TrialHater.Core.Match;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Belief;

// One way the NPC's hidden cards could be filled in, with its prior weight (weights sum to 1).
public sealed record Hypothesis(CardStats?[] RedHand, double Weight, string Description);

public sealed record BeliefResult(IReadOnlyList<Hypothesis> Hypotheses, string Status)
{
    public bool Usable => this.Hypotheses.Count > 0;
}

// Enumerates the NPC hands consistent with what has been seen. An NPC deck is every fixed card
// plus (5 - fixed) cards from the variable pool, so there are at most C(5,2) = 10 hands per NPC
// (median 6 over the current sheet) before any card is seen; every revealed or played card
// prunes that further. Small enough to solve each hand exactly rather than sample.
//
// Cards are matched by stats, not identity: the board does not show card ids, and cards with the
// same stats are interchangeable for play.
public static class HandBelief
{
    public static BeliefResult Build(MatchView view, NpcDeck? npc, CardCatalog catalog)
    {
        var redHand = view.Red.Select(s => s.State == SlotState.Visible ? s.Card : (CardStats?)null).ToArray();
        var hiddenSlots = Enumerable.Range(0, view.Red.Length).Where(i => view.Red[i].State == SlotState.Hidden).ToList();
        if (hiddenSlots.Count == 0)
            return new BeliefResult([new Hypothesis(redHand, 1, "all red cards visible")], "red hand fully visible");
        if (npc is null)
            return new BeliefResult([], $"{hiddenSlots.Count} hidden red card(s) and no NPC deck data");

        var fixedCards = Stats(npc.Fixed, catalog, out var missingFixed);
        var variable = Stats(npc.Variable, catalog, out var missingVariable);
        if (missingFixed + missingVariable > 0)
            return new BeliefResult([], $"NPC {npc.Name}: {missingFixed + missingVariable} card id(s) not in the catalogue");

        // Everything known to have left or still be in the NPC's deck this round.
        var visible = view.Red.Where(s => s.State == SlotState.Visible).Select(s => s.Card).ToList();
        var seen = new List<CardStats>(visible);
        seen.AddRange(view.RedPlayed);
        var hiddenForeign = new List<CardStats>();
        foreach (var foreign in view.RedForeign)
        {
            // A card of ours the NPC got by Swap: if we can see it (in hand or played) it is accounted
            // for; otherwise it is sitting in one of the hidden slots.
            if (!Remove(seen, foreign))
                hiddenForeign.Add(foreign);
        }

        seen.AddRange(view.RedLost);

        var dealtFromDeck = view.Round == 0 && !view.Rules.Has(TriadRule.Random);
        if (dealtFromDeck)
        {
            var exact = FromPool(fixedCards, variable, npc.VariablePicks, seen, hiddenForeign, hiddenSlots.Count);
            return exact is null
                ? new BeliefResult([], $"NPC {npc.Name}: what has been seen does not fit its fixed + variable deck (wrong NPC, or a mis-read card)")
                : Assemble(redHand, hiddenSlots, exact, $"{npc.Name}: fixed {fixedCards.Count} + {npc.VariablePicks} of {variable.Count}");
        }

        // A Sudden Death round deals hands from the board, and Random replaces the deck: the
        // fixed/variable split no longer holds. Fall back to any hidden cards from the NPC's whole
        // pool that are not already accounted for.
        var pool = new List<CardStats>(fixedCards);
        pool.AddRange(variable);
        foreach (var card in seen)
        {
            if (!Remove(pool, card))
                return new BeliefResult([], $"NPC {npc.Name}: a seen card ({card}) is not in its card pool (wrong NPC, or a mis-read card)");
        }

        if (pool.Count + hiddenForeign.Count < hiddenSlots.Count)
            return new BeliefResult([], $"NPC {npc.Name}: not enough unseen pool cards for {hiddenSlots.Count} hidden slot(s)");
        var loose = Combinations(pool, hiddenSlots.Count - hiddenForeign.Count)
            .Select(c => (IReadOnlyList<CardStats>)[.. hiddenForeign, .. c])
            .ToList();
        return Assemble(redHand, hiddenSlots, loose, $"{npc.Name}: any {hiddenSlots.Count} unseen pool card(s) (round {view.Round})");
    }

    // The exact fixed/variable enumeration, or null when what was seen contradicts it.
    private static List<IReadOnlyList<CardStats>>? FromPool(
        List<CardStats> fixedCards, List<CardStats> variable, int picks, List<CardStats> seen, List<CardStats> hiddenForeign, int hidden)
    {
        var fixedLeft = new List<CardStats>(fixedCards);
        var seenVariable = new List<CardStats>();
        foreach (var card in seen)
        {
            if (Remove(fixedLeft, card))
                continue;
            seenVariable.Add(card);
        }

        var variableLeft = new List<CardStats>(variable);
        foreach (var card in seenVariable)
            if (!Remove(variableLeft, card))
                return null;
        var remainingPicks = picks - seenVariable.Count;
        if (remainingPicks < 0)
            return null;

        var hands = new List<IReadOnlyList<CardStats>>();
        foreach (var chosen in Combinations(variableLeft, remainingPicks))
        {
            var unknown = new List<CardStats>(fixedLeft);
            unknown.AddRange(chosen);
            unknown.AddRange(hiddenForeign);
            if (unknown.Count == hidden)
                hands.Add(unknown);
        }

        return hands.Count == 0 ? null : hands;
    }

    // Fills the hidden slots of each candidate and merges candidates that are the same hand.
    private static BeliefResult Assemble(CardStats?[] redHand, List<int> hiddenSlots, List<IReadOnlyList<CardStats>> unknowns, string status)
    {
        var merged = new Dictionary<string, (CardStats?[] Hand, double Weight)>();
        foreach (var unknown in unknowns)
        {
            var ordered = unknown.OrderBy(c => c.ToString(), StringComparer.Ordinal).ToList();
            var key = string.Join(" ", ordered);
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = (existing.Hand, existing.Weight + 1);
                continue;
            }

            var hand = (CardStats?[])redHand.Clone();
            for (var i = 0; i < hiddenSlots.Count; i++)
                hand[hiddenSlots[i]] = ordered[i];
            merged[key] = (hand, 1);
        }

        var total = merged.Values.Sum(v => v.Weight);
        var hypotheses = merged.Select(kv => new Hypothesis(kv.Value.Hand, kv.Value.Weight / total, kv.Key)).ToList();
        return new BeliefResult(hypotheses, $"{status}: {hypotheses.Count} hand(s)");
    }

    private static List<CardStats> Stats(IEnumerable<ushort> ids, CardCatalog catalog, out int missing)
    {
        var list = new List<CardStats>();
        missing = 0;
        foreach (var id in ids)
        {
            if (catalog.Get(id) is { } card)
                list.Add(card.Stats);
            else
                missing++;
        }

        return list;
    }

    private static bool Remove(List<CardStats> list, CardStats card)
    {
        var index = list.IndexOf(card);
        if (index < 0)
            return false;
        list.RemoveAt(index);
        return true;
    }

    internal static IEnumerable<List<T>> Combinations<T>(IReadOnlyList<T> items, int size)
    {
        if (size < 0 || size > items.Count)
            yield break;
        var index = new int[size];
        for (var i = 0; i < size; i++)
            index[i] = i;
        while (true)
        {
            yield return index.Select(i => items[i]).ToList();
            var k = size - 1;
            while (k >= 0 && index[k] == items.Count - size + k)
                k--;
            if (k < 0)
                yield break;
            index[k]++;
            for (var j = k + 1; j < size; j++)
                index[j] = index[j - 1] + 1;
        }
    }
}

