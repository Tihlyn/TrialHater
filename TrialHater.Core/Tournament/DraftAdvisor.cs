using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Tournament;

// One option of a draft pick: the card(s) it adds, and the best expected result of a deck that
// takes it.
public sealed record DraftOption(int Pick, int Choice, IReadOnlyList<CardInfo> Cards, double Value);

// The draft's best deck (Picks, one card per rarity), the option to take at each of the three picks
// (Best), every option with its value, and every possible deck ranked.
public sealed record DraftAdvice(
    IReadOnlyList<CardInfo> Picks,
    IReadOnlyList<DraftOption> Best,
    IReadOnlyList<DraftOption> Options,
    IReadOnlyList<DeckScore> Ranked,
    string Status);

// Open Tournament draft (docs/MODEL_MAPPING.md, sessions 6 and 15). The window offers 15 cards in
// three picks: pick 0 is one of the pairs (0,1), (2,3), (4,5); pick 1 one of the pairs (6,7),
// (8,9), (10,11); pick 2 one of the cards 12, 13, 14 (positions in the window's AtkValues). A click
// sends [3, pick, choice] and the last click of each pick counts. Each pair joins two rarities and
// the pairs exclude each other, so the deck is one of 3 x 3 x 3 = 27, not any card per rarity.
//
// Each deck is scored with simulated games (DeckEvaluator, quick: we take the most cards now, the
// opponent follows the greedy model) under the tournament's rules against an opponent pool:
// drafted decks seen in past tournaments, topped up with random one-per-rarity decks.
// Opponents' drafts are unknown in advance (each player gets its own offer), so this ranks decks
// against the field, not against a particular player.
//
// With a DraftRecord, decks within TieMargin of the best - closer than the simulation can tell
// apart - are decided by how their cards did in our past tournaments under the same rules (vetted
// minus discarded); the chosen deck is ranked first.
public static class DraftAdvisor
{
    public const int Opponents = 96;
    public const int OfferSize = 15;

    // About two standard errors of a deck's simulated expected result (96 opponents x 2 sides x 8
    // games): decks closer than this are a tie.
    public const double TieMargin = 0.03;

    // Window positions of each pick's options.
    public static readonly int[][][] Layout =
    [
        [[0, 1], [2, 3], [4, 5]],
        [[6, 7], [8, 9], [10, 11]],
        [[12], [13], [14]],
    ];

    // `offer`: the 15 cards in window order.
    public static DraftAdvice Advise(IReadOnlyList<CardInfo> offer, RuleSet rules, IReadOnlyList<CardStats[]> observed, CardCatalog catalog,
        CancellationToken cancel = default, int seed = 1, DraftRecord? record = null)
    {
        if (offer.Count != OfferSize)
            return Empty($"the offer has {offer.Count} cards, not {OfferSize}");

        var options = Layout.Select((pick, p) => pick.Select((positions, c) => (Pick: p, Choice: c, Cards: positions.Select(i => offer[i]).ToList())).ToList()).ToList();
        var decks = new List<(int[] Choices, IReadOnlyList<CardInfo> Cards)>();
        foreach (var a in options[0])
        foreach (var b in options[1])
        foreach (var c in options[2])
            decks.Add(([a.Choice, b.Choice, c.Choice], [.. a.Cards, .. b.Cards, .. c.Cards]));
        if (decks.Any(d => d.Cards.Select(c => c.Stars).Distinct().Count() != 5))
            return Empty("the offer's picks do not make one card per rarity (window layout changed?)");

        var opponents = OpponentPool(observed, catalog, seed);
        var scores = new DeckScore[decks.Count];
        var evaluation = new DeckEvaluationOptions { QuickPlayouts = 8 };
        Parallel.For(0, decks.Count, new ParallelOptions { CancellationToken = cancel },
            i => scores[i] = DeckEvaluator.Evaluate(decks[i].Cards, opponents, rules, evaluation, exact: false, cancel));

        var order = Enumerable.Range(0, decks.Count).OrderByDescending(i => scores[i].Expected).ToList();
        var all = options.SelectMany(pick => pick.Select(o => new DraftOption(o.Pick, o.Choice, o.Cards,
            Enumerable.Range(0, decks.Count).Where(i => decks[i].Choices[o.Pick] == o.Choice).Max(i => scores[i].Expected)))).ToList();
        var status = $"{decks.Count} decks vs {opponents.Count} opponents ({Math.Min(observed.Count, Opponents)} seen in past tournaments) under [{rules}]";
        var bestDeck = order[0];
        if (record is not null)
        {
            var track = (int i) => record.Track(rules, decks[i].Cards.Select(c => c.Id));
            var chosen = order.TakeWhile(i => scores[i].Expected >= scores[order[0]].Expected - TieMargin)
                .OrderByDescending(track).ThenByDescending(i => scores[i].Expected).First();
            if (chosen != bestDeck)
            {
                status += $"; past tournaments: took a deck {scores[order[0]].Expected - scores[chosen].Expected:0.00} below the best (a tie) for its cards' record ({track(chosen):+0;-0} vs {track(bestDeck):+0;-0})";
                order.Remove(chosen);
                order.Insert(0, chosen);
                bestDeck = chosen;
            }
        }

        var best = Enumerable.Range(0, 3).Select(p => all.First(o => o.Pick == p && o.Choice == decks[bestDeck].Choices[p])).ToList();
        return new DraftAdvice(decks[bestDeck].Cards, best, all, order.Select(i => scores[i]).ToList(), status);
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

    private static DraftAdvice Empty(string why) => new([], [], [], [], why);
}
