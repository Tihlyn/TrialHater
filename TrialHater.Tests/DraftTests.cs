using TrialHater.Core.Cards;
using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class DraftTests
{
    // The 15 cards offered in the 2026-10-19 23:00 UTC tournament (three per rarity).
    private static readonly ushort[] Offer = [18, 36, 78, 98, 127, 210, 248, 259, 294, 319, 359, 368, 372, 433, 457];

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
    public void TheAdviceIsOneOfferedCardPerRarityAndRanksEveryDeck()
    {
        var offer = Offer.Select(id => Catalog.Get(id)!).ToList();
        var advice = DraftAdvisor.Advise(offer, RuleSet.Of(TriadRule.Draft), Observed(), Catalog);
        Assert.Equal(243, advice.Ranked.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, advice.Picks.Select(c => c.Stars).Order());
        Assert.All(advice.Picks, c => Assert.Contains(c.Id, Offer));
        Assert.Equal(15, advice.CardValue.Count);
        Assert.Equal(advice.Ranked[0].Expected, advice.Picks.Max(c => advice.CardValue[c.Id]), 9);
    }

    [Fact]
    public void UnderReverseTheAdvicePicksLowerCards()
    {
        var offer = Offer.Select(id => Catalog.Get(id)!).ToList();
        var plain = DraftAdvisor.Advise(offer, RuleSet.Of(TriadRule.Draft), Observed(), Catalog);
        var reverse = DraftAdvisor.Advise(offer, RuleSet.Of(TriadRule.Draft, TriadRule.Reverse), Observed(), Catalog);
        Assert.True(reverse.Picks.Sum(c => c.Stats.SideSum) < plain.Picks.Sum(c => c.Stats.SideSum),
            $"reverse {string.Join(",", reverse.Picks.Select(c => c.Stats))} vs plain {string.Join(",", plain.Picks.Select(c => c.Stats))}");
    }
}
