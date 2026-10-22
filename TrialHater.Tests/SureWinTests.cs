using TrialHater.Core.Belief;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;
using TrialHater.Core.Search;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

// Prideful Stag (TripleTriad 2293821, Plus), 2026-10-21 13:51, lost 3/10. Fixed Godbert 6656,
// Lost Lamb 7345, Blue Dragon 2277; variable 2 of Momodi 7553, Vanu Vanu 2647, Coblyn 3334,
// Chocobo 3721. It opened Vanu Vanu, so its last card was one of Momodi, Coblyn, Chocobo.
public class SureWinTests
{
    private const uint PridefulStag = 2293821;

    private static CardStats Card(ushort id) => Catalog.Get(id)!.Stats;

    // Our third decision: after Lost Lamb at 7, the NPC holds Godbert and one hidden card. Ten of
    // the twelve moves are a win in each hypothesis taken alone; 7A91 to the centre (played) loses
    // to Godbert bottom-left when the hidden card is Chocobo (Plus 8/8).
    private static MatchView AfterLostLamb()
    {
        var vanu = Card(82);
        var dragon = Card(26);
        var lamb = Card(158);
        var board = Board(null, new BoardCard(vanu, Side.Blue), B("5468"), null, null, new BoardCard(dragon, Side.Blue), null, new BoardCard(lamb, Side.Red), B("8744"));
        HandSlot[] blue = [HandSlot.Empty, HandSlot.Of(C("7528")), HandSlot.Empty, HandSlot.Of(C("7479")), HandSlot.Of(C("7A91"))];
        HandSlot[] red = [HandSlot.Hidden, HandSlot.Empty, HandSlot.Empty, HandSlot.Empty, HandSlot.Hidden];
        return new MatchView(RuleSet.Of(TriadRule.Plus), board, blue, red, Side.Blue, NpcTriadId: PridefulStag)
        {
            RedPlayed = [vanu, dragon, lamb],
        };
    }

    private static Position[] Positions(MatchView view, NpcDeck npc)
        => HandBelief.Build(view, npc, Catalog).Hypotheses.Select(h => view.ToPosition(h.RedHand)).ToArray();

    [Fact]
    public void FindsTheMovesThatWinWithoutSeeingTheHand()
    {
        var npc = Npcs.Get(PridefulStag)!;
        var positions = Positions(AfterLostLamb(), npc);
        Assert.Equal(3, positions.Length);
        Assert.True(SureWin.Applies(positions));

        var sure = SureWin.Moves(positions, positions[0].LegalMoves(), 4, CancellationToken.None);
        Move[] expected = [new(1, 0), new(1, 3), new(1, 4), new(1, 6), new(3, 4), new(3, 6), new(4, 0), new(4, 6)];
        Assert.Equal(expected.ToHashSet(), sure);
        Assert.DoesNotContain(new Move(4, 4), sure);
    }

    [Fact]
    public void PolicyPlaysASureWinOverAPerHandWin()
    {
        var npc = Npcs.Get(PridefulStag)!;
        var decision = MovePolicy.Decide(AfterLostLamb(), npc, Catalog, new PolicyOptions { ExploitBudget = TimeSpan.FromSeconds(20) });
        Assert.Equal(DecisionStatus.Ready, decision.Status);
        Assert.NotEqual(new Move(4, 4), decision.Move);
        Assert.Contains("sure win: 8 of 10", decision.Reason);
        Assert.Equal(8, decision.Moves.Count(m => m.Safe));
    }

    [Fact]
    public void WithoutASureWinThePolicyIsUnchanged()
    {
        // The next decision: Godbert at 6, one card hidden. Every move loses to one of the three.
        var npc = Npcs.Get(PridefulStag)!;
        var decision = MovePolicy.Decide(AfterGodbert(), npc, Catalog, new PolicyOptions());
        Assert.Equal(new Move(3, 0), decision.Move);
        Assert.DoesNotContain("sure win", decision.Reason);
    }

    private static MatchView AfterGodbert()
    {
        var vanu = Card(82);
        var dragon = Card(26);
        var lamb = Card(158);
        var godbert = Card(45);
        var board = Board(null, new BoardCard(vanu, Side.Blue), B("5468"), null, B("7A91"), new BoardCard(dragon, Side.Blue), new BoardCard(godbert, Side.Red), new BoardCard(lamb, Side.Red), B("8744"));
        HandSlot[] blue = [HandSlot.Empty, HandSlot.Of(C("7528")), HandSlot.Empty, HandSlot.Of(C("7479")), HandSlot.Empty];
        HandSlot[] red = [HandSlot.Empty, HandSlot.Empty, HandSlot.Empty, HandSlot.Empty, HandSlot.Hidden];
        return new MatchView(RuleSet.Of(TriadRule.Plus), board, blue, red, Side.Blue, NpcTriadId: PridefulStag)
        {
            RedPlayed = [vanu, dragon, lamb, godbert],
        };
    }

    // With one hand the search is the exact game: its win and no-loss sets are the solver's.
    [Fact]
    public void ThresholdsMatchTheSolverOnAKnownHand()
    {
        var npc = Npcs.Get(PridefulStag)!;
        foreach (var position in Positions(AfterLostLamb(), npc).Concat(Positions(AfterGodbert(), npc)))
        {
            var values = new Solver(position).ScoreRootMoves();
            var legal = position.LegalMoves().ToList();
            foreach (var atLeast in new[] { 1, 0 })
            {
                var exact = values.Where(v => v.Value >= atLeast - 1e-9).Select(v => v.Move).ToHashSet();
                Assert.Equal(exact, SureWin.Moves([position], legal, 2, CancellationToken.None, atLeast));
            }
        }
    }

    [Fact]
    public void SureWinsAreSureDrawsAreNonLossesInEveryHand()
    {
        var npc = Npcs.Get(PridefulStag)!;
        foreach (var view in new[] { AfterLostLamb(), AfterGodbert() })
        {
            var positions = Positions(view, npc);
            var legal = positions[0].LegalMoves().ToList();
            var wins = SureWin.Moves(positions, legal, 2, CancellationToken.None);
            var draws = SureWin.Moves(positions, legal, 2, CancellationToken.None, atLeast: 0);
            var perHand = legal.Where(m => positions.All(p => new Solver(p).ScoreMoves([m])[0].Value >= 0)).ToHashSet();
            Assert.Subset(draws, wins);
            Assert.Subset(perHand, draws);
        }
    }

    [Fact]
    public void OneHypothesisOrRandomDrawsAreLeftAlone()
    {
        var position = Pos(RuleSet.Of(TriadRule.Plus), EmptyBoard(), Hand("5468", "7528", "8744", "7479", "7A91"), Hand("6656", "7345", "2277", "7553", "3721"));
        Assert.False(SureWin.Applies([position]));
        var chaos = Pos(RuleSet.Of(TriadRule.Chaos), EmptyBoard(), Hand("5468", "7528", "8744", "7479", "7A91"), Hand("6656", "7345", "2277", "7553", "3721"));
        Assert.False(SureWin.Applies([chaos, chaos]));
    }
}
