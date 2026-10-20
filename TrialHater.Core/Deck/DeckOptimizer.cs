using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Deck;

public sealed record DeckSearchResult(IReadOnlyList<DeckScore> Best, IReadOnlyList<DeckScore> Saved, string Status);

public sealed record DeckSearchOptions
{
    // Cards kept per rarity tier for the candidate decks: 5 stars, 4 stars, 3 stars or less.
    public int TopFive { get; init; } = 3;

    public int TopFour { get; init; } = 5;

    public int TopLow { get; init; } = 10;

    // Candidates passed from the quick evaluation to the exact one, and results returned.
    public int Finalists { get; init; } = 12;

    public int Results { get; init; } = 5;

    public DeckEvaluationOptions Evaluation { get; init; } = new();
}

// Finds the best decks among the owned cards for one NPC (docs/RESEARCH.md, "Deck optimiser"):
// 1. rank each owned card by how its sides fare against the NPC's card pool under the rules,
// 2. build every legal deck from the top cards of each rarity tier and score it quickly
//    (simulated games, we take the most cards now, the NPC follows its model),
// 3. evaluate the finalists and the player's saved decks exactly (DeckEvaluator) and rank them by
//    expected result per game.
// Under Order the deck's order is the play order: the finalists' best order is searched too.
public static class DeckOptimizer
{
    public static DeckSearchResult Find(NpcDeck npc, RuleSet rules, IReadOnlyCollection<CardInfo> owned,
        IReadOnlyList<IReadOnlyList<CardInfo>> saved, CardCatalog catalog, DeckSearchOptions options,
        Action<string>? progress = null, CancellationToken cancel = default)
    {
        if (rules.Has(TriadRule.Random))
            return new DeckSearchResult([], [], "Random rule: the game deals from the whole collection, the deck does not matter");
        var hands = DeckRules.NpcHands(npc, catalog);
        if (hands.Count == 0)
            return new DeckSearchResult([], [], $"{npc.Name}: no deck data");

        var evalRules = DeckRules.ForEvaluation(rules);
        var pool = hands.SelectMany(h => h).Distinct().ToList();
        var ranked = owned.Where(c => c.Stars > 0)
            .Select(c => (Card: c, Score: CardScore(c.Stats, pool, evalRules)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Card.Id)
            .Select(x => x.Card).ToList();
        var five = ranked.Where(c => c.Stars >= 5).Take(options.TopFive).ToList();
        var four = ranked.Where(c => c.Stars == 4).Take(options.TopFour).ToList();
        var low = ranked.Where(c => c.Stars <= 3).Take(options.TopLow).ToList();
        var candidates = Candidates(five, four, low).ToList();
        if (candidates.Count == 0)
            return new DeckSearchResult([], [], "not enough owned cards for a legal deck");

        progress?.Invoke($"quick evaluation of {candidates.Count} decks");
        var quick = new DeckScore[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { CancellationToken = cancel },
            i => quick[i] = DeckEvaluator.Evaluate(candidates[i], hands, rules, options.Evaluation, exact: false, cancel));
        var finalists = quick.OrderByDescending(s => s.Expected).ThenByDescending(s => s.Draw)
            .Take(options.Finalists).Select(s => s.Cards).ToList();

        if (evalRules.Has(TriadRule.Order))
        {
            progress?.Invoke("searching card orders (Order)");
            finalists = finalists.Select(deck => BestOrder(deck, hands, rules, options.Evaluation, cancel)).ToList();
        }

        var exact = !evalRules.Has(TriadRule.Chaos) && !evalRules.Has(TriadRule.Order);
        // Decks run side by side (each also splits its deals), so the cores stay busy.
        var all = finalists.Concat(saved.Where(d => d.Count == DeckRules.Size)).ToList();
        var results = new DeckScore[all.Count];
        var done = 0;
        progress?.Invoke($"{(exact ? "exact" : "simulated")} evaluation of {all.Count} decks");
        Parallel.For(0, all.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            results[i] = Final(all[i], hands, rules, options.Evaluation, exact, cancel);
            progress?.Invoke($"{(exact ? "exact" : "simulated")} evaluation {Interlocked.Increment(ref done)}/{all.Count}");
        });
        var scored = results.Take(finalists.Count).ToList();
        var savedScores = results.Skip(finalists.Count).ToList();

        var best = scored.OrderByDescending(s => s.Expected).ThenByDescending(s => s.PerfectWin - s.PerfectLoss)
            .Take(options.Results).ToList();
        var how = exact ? "exact solve per deal + safe play" : "simulated games (Chaos/Order: no exact search)";
        return new DeckSearchResult(best, savedScores, $"{npc.Name} [{rules}]: {hands.Count} NPC hand(s), {candidates.Count} decks tried, {how}");
    }

    // Under Order with exact off the final score is simulated; give it the exact budget of games.
    private static DeckScore Final(IReadOnlyList<CardInfo> deck, List<CardStats[]> hands, RuleSet rules,
        DeckEvaluationOptions options, bool exact, CancellationToken cancel)
        => exact
            ? DeckEvaluator.Evaluate(deck, hands, rules, options, exact: true, cancel)
            : DeckEvaluator.Evaluate(deck, hands, rules, options with { QuickPlayouts = options.Playouts * 4 }, exact: false, cancel);

    // Every legal deck from the tiers: at most one 5-star card, at most two of 4 stars or more.
    internal static IEnumerable<IReadOnlyList<CardInfo>> Candidates(List<CardInfo> five, List<CardInfo> four, List<CardInfo> low)
    {
        var fives = new List<CardInfo?> { null };
        fives.AddRange(five);
        foreach (var f in fives)
        {
            var high = f is null ? 0 : 1;
            for (var k = 0; k <= Math.Min(2 - high, four.Count); k++)
            {
                var rest = DeckRules.Size - high - k;
                if (rest > low.Count || rest < 0)
                    continue;
                foreach (var fourPick in DeckRules.Combinations(four.Count, k))
                {
                    foreach (var lowPick in DeckRules.Combinations(low.Count, rest))
                    {
                        var deck = new List<CardInfo>(DeckRules.Size);
                        if (f is not null)
                            deck.Add(f);
                        deck.AddRange(fourPick.Select(i => four[i]));
                        deck.AddRange(lowPick.Select(i => low[i]));
                        yield return deck;
                    }
                }
            }
        }
    }

    // How well one card's sides fare against the NPC's pool: for each side and each pool card, +1
    // if ours would take theirs placed against it, +1 if theirs would not take ours. Type shifts
    // and Same/Plus are left to the simulations.
    internal static double CardScore(CardStats card, List<CardStats> pool, RuleSet rules)
    {
        var reverse = rules.Has(TriadRule.Reverse);
        var fallenAce = rules.Has(TriadRule.FallenAce);
        var score = 0;
        foreach (var theirs in pool)
        {
            for (var side = 0; side < CardStats.SideCount; side++)
            {
                var mine = card.Side(side);
                var facing = theirs.Side((side + 2) & 3);
                if (Beats(mine, facing, reverse, fallenAce))
                    score++;
                if (!Beats(facing, mine, reverse, fallenAce))
                    score++;
            }
        }

        return score / (double)Math.Max(1, pool.Count * CardStats.SideCount * 2);
    }

    private static bool Beats(int mine, int theirs, bool reverse, bool fallenAce)
    {
        if (fallenAce && !reverse && mine == 1 && theirs == 10)
            return true;
        if (fallenAce && reverse && mine == 10 && theirs == 1)
            return true;
        return reverse ? mine < theirs : mine > theirs;
    }

    // Order: the deck is played left to right, so try every order and keep the best (quick games).
    private static IReadOnlyList<CardInfo> BestOrder(IReadOnlyList<CardInfo> deck, List<CardStats[]> hands, RuleSet rules,
        DeckEvaluationOptions options, CancellationToken cancel)
    {
        IReadOnlyList<CardInfo> best = deck;
        var bestScore = double.NegativeInfinity;
        foreach (var order in Permutations(deck.Count))
        {
            var candidate = order.Select(i => deck[i]).ToList();
            var score = DeckEvaluator.Evaluate(candidate, hands, rules, options, exact: false, cancel).Expected;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        while (true)
        {
            yield return [.. a];
            var i = n - 2;
            while (i >= 0 && a[i] >= a[i + 1])
                i--;
            if (i < 0)
                yield break;
            var j = n - 1;
            while (a[j] <= a[i])
                j--;
            (a[i], a[j]) = (a[j], a[i]);
            Array.Reverse(a, i + 1, n - i - 1);
        }
    }
}
