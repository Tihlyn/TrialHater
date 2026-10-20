using TrialHater.Core.Cards;
using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class DraftTests
{
    // The 2026-10-20 17:18 UTC offer in window order: pairs (0,1)..(4,5) of 3* + 2*, pairs (6,7)..
    // (10,11) of 4* + 1*, then three 5*. The player took Magitek + Rhoswen, Themis + Kobold, Bahamut.
    private static readonly ushort[] Offer = [92, 256, 48, 187, 176, 144, 209, 348, 433, 13, 385, 17, 74, 61, 419];

    private static List<CardInfo> Cards(ushort[] ids) => ids.Select(id => Catalog.Get(id)!).ToList();

    private static List<CardStats[]> Observed() => DraftDecks.Parse(File.ReadLines(DataPath("draft-decks.csv")), Catalog);

    [Fact]
    public void TheRecordedTournamentDecksAreOneCardPerRarity()
    {
        var decks = Observed();
        Assert.Equal(24, decks.Count);
        foreach (var deck in decks)
        {
            var stars = deck.Select(s => Catalog.WithStats(s).Select(c => c.Stars).ToHashSet()).ToList();
            foreach (var star in new byte[] { 1, 2, 3, 4, 5 })
                Assert.Contains(stars, s => s.Contains(star));
        }
    }

    [Fact]
    public void TheAdviceIsOneOptionPerPickAndRanksTheTwentySevenDecks()
    {
        var advice = DraftAdvisor.Advise(Cards(Offer), RuleSet.Of(TriadRule.Draft, TriadRule.Ascension, TriadRule.FallenAce), Observed(), Catalog);
        Assert.Equal(27, advice.Ranked.Count);
        Assert.Equal(9, advice.Options.Count);
        Assert.Equal([0, 1, 2], advice.Best.Select(o => o.Pick));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, advice.Picks.Select(c => c.Stars).Order());

        // The picks are whole options: a pair is taken together, never split.
        Assert.Equal(advice.Picks, advice.Best.SelectMany(o => o.Cards));
        Assert.All(advice.Best, o => Assert.Equal(advice.Ranked[0].Expected, o.Value, 9));
    }

    [Fact]
    public void ThePairsComeFromTheWindowLayout()
    {
        var advice = DraftAdvisor.Advise(Cards(Offer), RuleSet.Of(TriadRule.Draft), Observed(), Catalog);
        var first = advice.Options.Where(o => o.Pick == 0).Select(o => string.Join("+", o.Cards.Select(c => c.Id))).ToList();
        Assert.Equal(["92+256", "48+187", "176+144"], first);
        var third = advice.Options.Where(o => o.Pick == 2).Select(o => o.Cards.Single().Id).ToList();
        Assert.Equal([74, 61, 419], third);
    }

    [Fact]
    public void AnOfferThatDoesNotMakeOneCardPerRarityIsRefused()
    {
        var shuffled = Offer.Reverse().ToArray();
        var advice = DraftAdvisor.Advise(Cards(shuffled), RuleSet.Of(TriadRule.Draft), Observed(), Catalog);
        Assert.Empty(advice.Picks);
        Assert.Contains("layout", advice.Status);
    }
}
