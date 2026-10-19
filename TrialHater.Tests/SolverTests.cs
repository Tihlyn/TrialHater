using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;
using TrialHater.Core.Search;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class SolverTests
{
    [Fact]
    public void LastMoveValueIsTheOutcome()
    {
        // Three blue cards down, one card kept in hand. Into the last cell, "1119" (left 9) takes
        // cell 7 for 3 + 1 + 1 + 1 = 6 cards, a win; "1111" takes nothing, 5 cards, a draw.
        var board = Board(B("1111"), R("1111"), B("1111"), R("1111"), B("1111"), R("1111"), R("1111"), R("1111"), null);
        var p = Pos(RuleSet.Empty, board, Hand("1111", "1119"), Hand(), Side.Blue);
        var moves = new Solver(p).ScoreRootMoves();
        Assert.Equal(2, moves.Count);
        Assert.Equal(0, moves.Single(m => m.Move.HandSlot == 0).Value);
        Assert.Equal(1, moves.Single(m => m.Move.HandSlot == 1).Value);
    }

    // The solver against a naive full-width minimax/expectimax with no pruning or table, on random
    // positions with 4-6 cards already placed and random rule combinations.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchesNaiveSearchOnRandomPositions(int seed)
    {
        var rng = new Random(seed);
        var cards = Catalog.All.ToList();
        TriadRule[] optional = [TriadRule.Same, TriadRule.Plus, TriadRule.Reverse, TriadRule.FallenAce, TriadRule.Ascension, TriadRule.Chaos, TriadRule.Order];
        for (var round = 0; round < 25; round++)
        {
            var rules = RuleSet.Of(optional.Where(_ => rng.Next(3) == 0));
            if (rules.Has(TriadRule.Chaos) && rules.Has(TriadRule.Order))
                rules = rules.Without(TriadRule.Order);
            var p = RandomPosition(rng, cards, rules, placed: 4 + rng.Next(3));
            var solver = new Solver(p);
            var scored = solver.ScoreRootMoves();
            foreach (var mv in scored)
            {
                var engine = new Engine(p);
                var undo = engine.Play(mv.Move.HandSlot, mv.Move.Cell);
                var naive = -Naive(engine);
                engine.Undo(mv.Move.HandSlot, mv.Move.Cell, undo);
                Assert.True(Math.Abs(naive - mv.Value) < 1e-5, $"{p.Describe()} {mv.Move}: solver {mv.Value} naive {naive}");
            }

            var root = new Solver(p).RootValue();
            Assert.True(Math.Abs(scored.Max(m => m.Value) - root) < 1e-5, $"{p.Describe()}: best move {scored.Max(m => m.Value)} root {root}");
        }
    }

    // Chaos puts a Star1 chance node at every turn; a 16-entry table forces constant overwrites, so
    // any bound stored as exact (or an empty window turning into -infinity) shows up here.
    [Fact]
    public void ChaosMatchesNaiveUnderTableCollisions()
    {
        var rng = new Random(42);
        var cards = Catalog.All.ToList();
        for (var round = 0; round < 40; round++)
        {
            var rules = RuleSet.Of(TriadRule.Chaos).With(rng.Next(2) == 0 ? TriadRule.Plus : TriadRule.None).With(rng.Next(2) == 0 ? TriadRule.Same : TriadRule.None);
            var p = RandomPosition(rng, cards, rules, placed: 4);
            foreach (var bits in new[] { 4, 18 })
            {
                foreach (var mv in new Solver(p, bits).ScoreRootMoves())
                {
                    var engine = new Engine(p);
                    var undo = engine.Play(mv.Move.HandSlot, mv.Move.Cell);
                    var naive = -Naive(engine);
                    engine.Undo(mv.Move.HandSlot, mv.Move.Cell, undo);
                    Assert.True(Math.Abs(naive - mv.Value) < 1e-9, $"table 2^{bits} {p.Describe()} {mv.Move}: solver {mv.Value} naive {naive}");
                }
            }
        }
    }

    [Fact]
    public void CancellationStopsTheSearch()
    {
        var rng = new Random(7);
        var p = RandomPosition(rng, Catalog.All.ToList(), RuleSet.Of(TriadRule.Plus), placed: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new Solver(p, cancel: cts.Token).ScoreRootMoves());
    }

    [Fact]
    public void ExploitNeverChoosesAnUnsafeMove()
    {
        var rng = new Random(11);
        var cards = Catalog.All.ToList();
        for (var round = 0; round < 10; round++)
        {
            var p = RandomPosition(rng, cards, RuleSet.Of(TriadRule.Plus), placed: 3);
            var solver = new Solver(p);
            var scored = solver.ScoreRootMoves();
            var best = scored.Max(m => m.Value);
            var evaluator = new ExploitEvaluator(solver, new GreedyOpponent(0));
            foreach (var mv in scored.Where(m => m.Value >= best - 1e-9))
            {
                // Against any opponent the safe move is worth at least its minimax value; the
                // greedy expectation can only add to that.
                Assert.True(evaluator.AfterMove(mv.Move) >= mv.Value - 1e-9);
            }
        }
    }

    internal static Position RandomPosition(Random rng, List<CardInfo> cards, RuleSet rules, int placed)
    {
        var deck = cards.OrderBy(_ => rng.Next()).Take(10).Select(c => c.Stats).ToArray();
        var board = new BoardCard?[9];
        var blue = deck.Take(5).Select(c => (CardStats?)c).ToArray();
        var red = deck.Skip(5).Select(c => (CardStats?)c).ToArray();
        var blueFirst = rng.Next(2) == 0;
        var cellsFree = Enumerable.Range(0, 9).OrderBy(_ => rng.Next()).ToList();
        var engine = new Engine(new Position(RuleSet.Empty.With(rules.Has(TriadRule.Same) ? TriadRule.Same : TriadRule.None), board, blue, red, blueFirst ? Side.Blue : Side.Red));
        for (var i = 0; i < placed; i++)
        {
            var hand = engine.MoverHand;
            var slots = Enumerable.Range(0, 5).Where(s => (hand & (1 << s)) != 0).ToList();
            engine.Play(slots[rng.Next(slots.Count)], cellsFree[i]);
        }

        // Rebuild a Position from the engine state.
        for (var c = 0; c < 9; c++)
        {
            var inst = engine.CellInstance(c);
            if (inst != Engine.Empty)
                board[c] = new BoardCard(engine.CardOf(inst), engine.IsBlue(c) ? Side.Blue : Side.Red);
        }

        var blueHand = Enumerable.Range(0, 5).Select(s => (engine.BlueHand & (1 << s)) != 0 ? blue[s] : null).ToArray();
        var redHand = Enumerable.Range(0, 5).Select(s => (engine.RedHand & (1 << s)) != 0 ? red[s] : null).ToArray();
        return new Position(rules, board, blueHand, redHand, engine.BlueToMove ? Side.Blue : Side.Red);
    }

    // Plain recursive minimax (expectimax at random-selection nodes), no pruning, no table.
    private static double Naive(Engine engine)
    {
        if (engine.IsFinished)
            return engine.BlueToMove ? engine.BlueOutcome : -engine.BlueOutcome;
        var hand = engine.MoverHand;
        var empty = engine.EmptyMask;
        double BestFor(int cards)
        {
            var best = double.NegativeInfinity;
            for (var s = 0; s < 5; s++)
            {
                if ((cards & (1 << s)) == 0)
                    continue;
                for (var c = 0; c < 9; c++)
                {
                    if ((empty & (1 << c)) == 0)
                        continue;
                    var undo = engine.Play(s, c);
                    best = Math.Max(best, -Naive(engine));
                    engine.Undo(s, c, undo);
                }
            }

            return best;
        }

        if (engine.MoverSelection == CardSelection.Random && System.Numerics.BitOperations.PopCount((uint)hand) > 1)
        {
            var sum = 0.0;
            var n = 0;
            for (var s = 0; s < 5; s++)
            {
                if ((hand & (1 << s)) == 0)
                    continue;
                sum += BestFor(1 << s);
                n++;
            }

            return sum / n;
        }

        return BestFor(engine.SelectableHand());
    }
}
