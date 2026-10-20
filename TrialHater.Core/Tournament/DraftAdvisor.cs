using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Tournament;

// The draft's best deck: Picks (one card per rarity, 1 to 5 stars), every legal deck ranked, and
// for each offered card the best expected result of a deck containing it (to judge alternatives).
public sealed record DraftAdvice(IReadOnlyList<CardInfo> Picks, IReadOnlyList<DeckScore> Ranked, IReadOnlyDictionary<ushort, double> CardValue, string Status);

// Open Tournament draft (docs/MODEL_MAPPING.md, session 6): 15 cards are offered, three per
// rarity, and the deck takes one of each, so 3^5 = 243 decks. Each is scored with simulated games
// (DeckEvaluator, quick: we take the most cards now, the opponent follows the greedy model) under
// the tournament's rules against an opponent pool: drafted decks seen in past tournaments, topped
// up with random one-per-rarity decks. The best dozen are scored again with more games. Opponents'
// drafts are unknown in advance (each player gets its own offer), so this ranks decks against the
// field, not against a particular player.
public static class DraftAdvisor
{
    public const int Opponents = 96;
    public const int Refined = 12;

    public static DraftAdvice Advise(IReadOnlyList<CardInfo> offer, RuleSet rules, IReadOnlyList<CardStats[]> observed, CardCatalog catalog,
        CancellationToken cancel = default, int seed = 1)
    {
        var tiers = Enumerable.Range(1, 5).Select(s => offer.Where(c => c.Stars == s).ToList()).ToList();
        if (tiers.Any(t => t.Count == 0))
            return new DraftAdvice([], [], new Dictionary<ushort, double>(), "the offer does not have a card of every rarity");

        var decks = new List<IReadOnlyList<CardInfo>>();
        foreach (var a in tiers[0])
        foreach (var b in tiers[1])
        foreach (var c in tiers[2])
        foreach (var d in tiers[3])
        foreach (var e in tiers[4])
            decks.Add([a, b, c, d, e]);

        var opponents = OpponentPool(observed, catalog, seed);
        var quick = new DeckScore[decks.Count];
        var options = new DeckEvaluationOptions { QuickPlayouts = 2 };
        Parallel.For(0, decks.Count, new ParallelOptions { CancellationToken = cancel },
            i => quick[i] = DeckEvaluator.Evaluate(decks[i], opponents, rules, options, exact: false, cancel));

        var top = quick.OrderByDescending(s => s.Expected).Take(Refined).ToList();
        var refined = new DeckScore[top.Count];
        var more = options with { QuickPlayouts = 8 };
        Parallel.For(0, top.Count, new ParallelOptions { CancellationToken = cancel },
            i => refined[i] = DeckEvaluator.Evaluate(top[i].Cards, opponents, rules, more, exact: false, cancel));

        var refinedKeys = refined.Select(Key).ToHashSet();
        var ranked = refined.OrderByDescending(s => s.Expected)
            .Concat(quick.Where(s => !refinedKeys.Contains(Key(s))).OrderByDescending(s => s.Expected))
            .ToList();
        var value = new Dictionary<ushort, double>();
        foreach (var score in ranked)
        {
            foreach (var card in score.Cards)
                value.TryAdd(card.Id, score.Expected);
        }

        var status = $"{decks.Count} decks vs {opponents.Count} opponents ({Math.Min(observed.Count, Opponents)} seen in past tournaments) under [{rules}]";
        return new DraftAdvice(ranked[0].Cards, ranked, value, status);
    }

    // Past drafted decks first (they show what the field actually drafts), then random decks of one
    // card per rarity up to Opponents.
    internal static List<CardStats[]> OpponentPool(IReadOnlyList<CardStats[]> observed, CardCatalog catalog, int seed)
    {
        var pool = observed.Take(Opponents).ToList();
        var byStars = catalog.All.OrderBy(c => c.Id).GroupBy(c => c.Stars).ToDictionary(g => g.Key, g => g.ToList());
        var rng = new Random(seed);
        while (pool.Count < Opponents)
        {
            pool.Add(Enumerable.Range(1, 5).Select(s =>
            {
                var tier = byStars[(byte)s];
                return tier[rng.Next(tier.Count)].Stats;
            }).ToArray());
        }

        return pool;
    }

    private static string Key(DeckScore score) => string.Join(",", score.Cards.Select(c => c.Id).Order());
}
