using TrialHater.Core.Belief;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class BeliefTests
{
    // Memeroon: fixed 37, 14, 1; variable 21, 2, 7, 12 (2 of 4 picked).
    private const uint Memeroon = 2293762;

    private static MatchView View(NpcDeck npc, HandSlot[] red, IReadOnlyList<CardStats>? played = null, BoardCard?[]? board = null)
        => new(RuleSet.Of(npc.Rules), board ?? EmptyBoard(), BlueHand(), red, Side.Blue, NpcTriadId: npc.TriadId)
        {
            RedPlayed = played ?? [],
        };

    private static HandSlot[] BlueHand()
        => [HandSlot.Of(C("4234")), HandSlot.Of(C("3333")), HandSlot.Of(C("5555")), HandSlot.Of(C("2222")), HandSlot.Of(C("6161"))];

    private static HandSlot[] AllHidden() => [HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden];

    private static CardStats Card(ushort id) => Catalog.Get(id)!.Stats;

    [Fact]
    public void UnseenNpcHasOneHandPerVariablePick()
    {
        var npc = Npcs.Get(Memeroon)!;
        var belief = HandBelief.Build(View(npc, AllHidden()), npc, Catalog);
        Assert.Equal(6, belief.Hypotheses.Count);   // C(4, 2)
        Assert.Equal(1.0, belief.Hypotheses.Sum(h => h.Weight), 9);
        foreach (var h in belief.Hypotheses)
        {
            Assert.All(h.RedHand, c => Assert.NotNull(c));
            foreach (var fixedId in npc.Fixed)
                Assert.Contains(Card(fixedId), h.RedHand.Select(c => c!.Value));
        }
    }

    [Fact]
    public void ASeenVariableCardPrunesTheHands()
    {
        var npc = Npcs.Get(Memeroon)!;
        var shown = Card(npc.Variable[0]);
        var red = AllHidden();
        red[0] = HandSlot.Of(shown);
        var belief = HandBelief.Build(View(npc, red), npc, Catalog);
        Assert.Equal(3, belief.Hypotheses.Count);   // the other pick is one of the remaining 3
        Assert.All(belief.Hypotheses, h => Assert.Equal(shown, h.RedHand[0]));
    }

    [Fact]
    public void ASwappedCardOfOursInTheNpcsHandIsNotPartOfItsDeck()
    {
        // Yellow Moon, 2026-10-20 10:36: we gave 8744 and received 7137/Garlean; the NPC shows 4A59
        // and our 8744. Without the swap known, 8744 does not fit its deck (the stall).
        var npc = Npcs.Get(2293796)!;
        var red = new[] { HandSlot.Of(C("4A59/Garlean")), HandSlot.Hidden, HandSlot.Hidden, HandSlot.Of(C("8744")), HandSlot.Hidden };
        Assert.False(HandBelief.Build(View(npc, red), npc, Catalog).Usable);

        var view = View(npc, red) with { RedForeign = [C("8744")], RedLost = [C("7137/Garlean")] };
        var belief = HandBelief.Build(view, npc, Catalog);
        Assert.True(belief.Usable, belief.Status);
        Assert.All(belief.Hypotheses, h => Assert.DoesNotContain(C("7137/Garlean"), h.RedHand.Select(c => c!.Value)));
    }

    [Fact]
    public void PlayedCardsCountAsSeen()
    {
        var npc = Npcs.Get(Memeroon)!;
        var played = new List<CardStats> { Card(npc.Variable[1]), Card(npc.Fixed[0]) };
        var red = new[] { HandSlot.Empty, HandSlot.Empty, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden };
        var belief = HandBelief.Build(View(npc, red, played), npc, Catalog);
        Assert.Equal(3, belief.Hypotheses.Count);
        Assert.All(belief.Hypotheses, h => Assert.DoesNotContain(Card(npc.Fixed[0]), h.RedHand.Where(c => c is not null).Select(c => c!.Value)));
    }

    [Fact]
    public void ACardOutsideThePoolIsReportedNotGuessed()
    {
        var npc = Npcs.Get(Memeroon)!;
        var red = AllHidden();
        red[0] = HandSlot.Of(C("AAAA"));
        var belief = HandBelief.Build(View(npc, red), npc, Catalog);
        Assert.False(belief.Usable);
    }

    [Fact]
    public void AFullyVisibleHandNeedsNoNpcData()
    {
        var red = new[] { HandSlot.Of(C("1111")), HandSlot.Of(C("2222")), HandSlot.Of(C("3333")), HandSlot.Of(C("4444")), HandSlot.Of(C("5555")) };
        var view = new MatchView(RuleSet.Of(TriadRule.AllOpen), EmptyBoard(), BlueHand(), red, Side.Blue);
        var belief = HandBelief.Build(view, null, Catalog);
        Assert.Single(belief.Hypotheses);
    }

    [Fact]
    public void AnUnknownOpponentGetsLegalSampledHands()
    {
        var red = new[] { HandSlot.Of(C("AAAA")), HandSlot.Hidden, HandSlot.Hidden, HandSlot.Empty, HandSlot.Hidden };
        var view = new MatchView(RuleSet.Of(TriadRule.Plus), EmptyBoard(), BlueHand(), red, Side.Blue);
        var belief = HandBelief.Build(view, null, Catalog);
        Assert.InRange(belief.Hypotheses.Count, 1, HandBelief.UnknownOpponentSamples);
        Assert.Equal(1.0, belief.Hypotheses.Sum(h => h.Weight), 9);
        foreach (var h in belief.Hypotheses)
        {
            Assert.Equal(C("AAAA"), h.RedHand[0]);
            Assert.Null(h.RedHand[3]);
            Assert.All(new[] { 1, 2, 4 }, i => Assert.NotNull(h.RedHand[i]));
        }

        // Same view, same samples: decisions do not flicker between frames.
        Assert.Equal(belief.Hypotheses.Select(h => h.Description), HandBelief.Build(view, null, Catalog).Hypotheses.Select(h => h.Description));
    }

    [Fact]
    public void DraftFillsEachHiddenSlotWithACardOfItsRarity()
    {
        // Slot 1 (5 stars) already played, slot 2 (2 stars) shown.
        var red = new[] { HandSlot.Hidden, HandSlot.Empty, HandSlot.Of(C("5563/Beastman")), HandSlot.Hidden, HandSlot.Hidden };
        byte[] order = [3, 5, 2, 4, 1];
        var view = new MatchView(RuleSet.Of(TriadRule.Draft), EmptyBoard(), BlueHand(), red, Side.Blue) { SlotStars = order };
        var belief = HandBelief.Build(view, null, Catalog);
        Assert.InRange(belief.Hypotheses.Count, 2, HandBelief.DraftSamples);
        Assert.Equal(1.0, belief.Hypotheses.Sum(h => h.Weight), 9);
        foreach (var h in belief.Hypotheses)
        {
            Assert.Null(h.RedHand[1]);
            Assert.Equal(C("5563/Beastman"), h.RedHand[2]);
            foreach (var slot in new[] { 0, 3, 4 })
                Assert.Contains(order[slot], Catalog.WithStats(h.RedHand[slot]!.Value).Select(c => c.Stars));
        }
    }

    [Fact]
    public void DraftWithoutTheSlotOrderUsesTheMissingRarities()
    {
        // 1599 (4 stars) shown and 5522 (1 star) played: the three hidden cards are 2, 3 and 5 stars.
        var red = new[] { HandSlot.Hidden, HandSlot.Empty, HandSlot.Of(C("1599")), HandSlot.Hidden, HandSlot.Hidden };
        var view = new MatchView(RuleSet.Of(TriadRule.Draft), EmptyBoard(), BlueHand(), red, Side.Blue) { RedPlayed = [C("5522")] };
        var belief = HandBelief.Build(view, null, Catalog);
        Assert.True(belief.Usable, belief.Status);
        Assert.Contains("slot order unknown", belief.Status);
        foreach (var h in belief.Hypotheses)
        {
            var hidden = new[] { 0, 3, 4 }.Select(i => Catalog.WithStats(h.RedHand[i]!.Value).Select(c => c.Stars).ToList()).ToList();
            foreach (var star in new byte[] { 2, 3, 5 })
                Assert.Contains(hidden, stars => stars.Contains(star));
        }
    }

    [Fact]
    public void DraftIgnoresTheNpcsSheetDeck()
    {
        // Tournament match 1 (2026-10-19): the plugin took Prideful Stag's sheet deck, the drafted
        // card 5563 did not fit it, and the turn stalled on "no belief".
        var npc = Npcs.Get(2293864)!;
        var red = new[] { HandSlot.Hidden, HandSlot.Hidden, HandSlot.Empty, HandSlot.Hidden, HandSlot.Hidden };
        var view = View(npc, red, [C("5563/Beastman")]) with { Rules = RuleSet.Of(TriadRule.Draft) };
        var belief = HandBelief.Build(view, npc, Catalog);
        Assert.True(belief.Usable, belief.Status);
        Assert.StartsWith("Draft", belief.Status);
    }

    [Fact]
    public void EveryNpcInTheSheetHasAtMostTenHands()
    {
        // Special sheet rows (no NPC name, Random rule) deal from the whole pool and are excluded.
        foreach (var npc in Npcs.All.Where(n => !n.Name.StartsWith("UNMAPPED", StringComparison.Ordinal) && !n.Rules.Contains(TriadRule.Random)))
        {
            var belief = HandBelief.Build(View(npc, AllHidden()), npc, Catalog);
            Assert.True(belief.Hypotheses.Count is >= 1 and <= 10, $"{npc.Name}: {belief.Status}");
        }
    }

    [Fact]
    public void PolicyPicksASafeMoveAgainstARealNpc()
    {
        // The NPC opened with one of its fixed cards in the centre.
        var npc = Npcs.Get(Memeroon)!;
        var opener = Card(npc.Fixed[0]);
        var red = new[] { HandSlot.Empty, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden };
        var view = View(npc, red, [opener], Board(null, null, null, null, new BoardCard(opener, Side.Red)));
        var decision = MovePolicy.Decide(view, npc, Catalog, new PolicyOptions { ExploitBudget = TimeSpan.FromSeconds(20) });
        Assert.Equal(DecisionStatus.Ready, decision.Status);
        Assert.Equal(6, decision.Hypotheses);
        var chosen = decision.Chosen!;
        Assert.True(chosen.Safe);
        Assert.Equal(decision.Moves.Max(m => m.ExpectedValue), chosen.ExpectedValue, 9);
    }
}
