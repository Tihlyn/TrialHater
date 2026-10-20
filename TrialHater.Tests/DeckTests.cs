using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class DeckTests
{
    // Memeroon: fixed 37, 14, 1; variable 21, 2, 7, 12 (2 of 4 picked).
    private const uint Memeroon = 2293762;

    // The player's Deck 1 on 2026-10-20: 5468, 7528, 8744 (3*), 7479 (4*), 7A91 (5*).
    private static readonly ushort[] DeckOne = [461, 384, 463, 444, 252];

    private static List<CardInfo> Cards(params ushort[] ids) => ids.Select(id => Catalog.Get(id)!).ToList();

    [Fact]
    public void TheDeckLimitsAreOneFiveStarAndTwoOfFourOrMore()
    {
        Assert.Null(DeckRules.Problem(Cards(DeckOne)));
        var byStars = Catalog.All.OrderBy(c => c.Id).GroupBy(c => c.Stars).ToDictionary(g => g.Key, g => g.ToList());
        var two5 = byStars[5].Take(2).Concat(byStars[1].Take(3)).ToList();
        Assert.Equal("more than one 5-star card", DeckRules.Problem(two5));
        var three4 = byStars[4].Take(3).Concat(byStars[1].Take(2)).ToList();
        Assert.Equal("more than two cards of 4 stars or more", DeckRules.Problem(three4));
        Assert.Equal("a card appears twice", DeckRules.Problem([.. byStars[1].Take(4), byStars[1][0]]));
    }

    [Fact]
    public void EveryNpcHandHasTheFixedCardsAndOneVariablePick()
    {
        var npc = Npcs.Get(Memeroon)!;
        var hands = DeckRules.NpcHands(npc, Catalog);
        Assert.Equal(6, hands.Count);   // C(4, 2)
        Assert.All(hands, h => Assert.Equal(5, h.Length));
        Assert.Equal(6, hands.Select(h => string.Join(" ", h.Select(c => c.ToString()).Order(StringComparer.Ordinal))).Distinct().Count());
    }

    [Fact]
    public void EveryCandidateDeckIsLegal()
    {
        var byStars = Catalog.All.OrderBy(c => c.Id).GroupBy(c => Math.Min(c.Stars, (byte)5)).ToDictionary(g => g.Key, g => g.ToList());
        var candidates = DeckOptimizer.Candidates(byStars[5].Take(2).ToList(), byStars[4].Take(3).ToList(), byStars[3].Take(6).ToList()).ToList();
        Assert.All(candidates, d => Assert.Null(DeckRules.Problem(d)));
        // No 5*: C(6,5) + 3*C(6,4) + 3*C(6,3); one 5*: 2 * (C(6,4) + 3*C(6,3)).
        Assert.Equal(6 + 45 + 60 + (2 * (15 + 60)), candidates.Count);
    }

    [Fact]
    public void ExpectedRulesKeepWhatEveryRecentMatchHad()
    {
        // Tsuzura: Roulette + Chaos on the sheet, Reverse from the region; the roll varies.
        var npc = Npcs.Get(2293828)!;
        var (none, why) = DeckRules.ExpectedRules(npc, []);
        Assert.Equal(RuleSet.Of(npc.Rules), none);
        Assert.Contains("regional rules unknown", why);
        var history = new[]
        {
            RuleSet.Of(TriadRule.Same, TriadRule.Chaos, TriadRule.Reverse),
            RuleSet.Of(TriadRule.Chaos, TriadRule.Reverse, TriadRule.Swap),
            RuleSet.Of(TriadRule.ThreeOpen, TriadRule.Chaos, TriadRule.Reverse),
        };
        Assert.Equal(RuleSet.Of(TriadRule.Chaos, TriadRule.Reverse), DeckRules.ExpectedRules(npc, history).Rules);
    }

    [Fact]
    public void UnderReverseTheOptimiserBeatsAHighNumberDeck()
    {
        // Small search for test speed. Under Reverse low sides capture, so the high-number deck that
        // lost to Gyoei/Tsuzura should rank below what the optimiser builds.
        var npc = Npcs.Get(Memeroon)!;
        var options = new DeckSearchOptions
        {
            TopFive = 1,
            TopFour = 2,
            TopLow = 7,
            Finalists = 2,
            Results = 1,
            Evaluation = new DeckEvaluationOptions { Playouts = 2, QuickPlayouts = 1 },
        };
        var result = DeckOptimizer.Find(npc, RuleSet.Of(TriadRule.Reverse), Catalog.All.ToList(), [Cards(DeckOne)], Catalog, options);
        var best = Assert.Single(result.Best);
        var saved = Assert.Single(result.Saved);
        Assert.Null(DeckRules.Problem(best.Cards));
        Assert.True(best.Expected > saved.Expected, $"best {best.Expected:0.00} vs Deck 1 {saved.Expected:0.00}");
        Assert.True(best.Cards.Average(c => c.Stats.SideSum) < saved.Cards.Average(c => c.Stats.SideSum));
    }

    [Fact]
    public void RandomMakesTheDeckIrrelevant()
    {
        var npc = Npcs.Get(Memeroon)!;
        var result = DeckOptimizer.Find(npc, RuleSet.Of(TriadRule.Random), Catalog.All.ToList(), [], Catalog, new DeckSearchOptions());
        Assert.Empty(result.Best);
        Assert.Contains("does not matter", result.Status);
    }
}
