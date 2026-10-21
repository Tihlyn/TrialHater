using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;
using TrialHater.Core.Search;
using GameEngine = TrialHater.Core.Engine.Engine;

namespace TrialHater.Core.Deck;

// How a deck fares against one NPC: simulated games over every hand the NPC can deal and both
// starting sides. Win/Draw/Loss are rates over all games; Perfect* (exact evaluation only) is the
// game-theoretic result of each deal with both hands known, an upper bound on what any play can
// guarantee since the NPC's hand is hidden in a real match.
public sealed record DeckScore(
    IReadOnlyList<CardInfo> Cards,
    int Games,
    double Win,
    double Draw,
    double Loss,
    bool Exact,
    double? PerfectWin = null,
    double? PerfectDraw = null,
    double? PerfectLoss = null)
{
    // Expected result per game, win +1 / draw 0 / loss -1: the ranking key.
    public double Expected => this.Win - this.Loss;
}

public sealed record DeckEvaluationOptions
{
    // Simulated games per (NPC hand, starting side) in the exact evaluation, and in the quick one.
    public int Playouts { get; init; } = 6;

    public int QuickPlayouts { get; init; } = 2;

    public IOpponentModel Model { get; init; } = new GreedyOpponent(0.1);

    public int TableBits { get; init; } = 18;

    public string Signature => $"{this.Playouts},{this.QuickPlayouts},{this.TableBits},{this.Model.Name}";
}

// Two evaluations of a deck:
// - Exact (no Chaos/Order): per deal, an exact solve, then games where we play like auto play's
//   safe half - only moves that keep the deal's minimax value, the one taking the most cards now
//   among them - and the NPC follows the opponent model. The solver's table makes these cheap.
// - Quick (prefilter, and Chaos/Order where exact search is too slow): we take the most cards
//   now, the NPC follows the model.
public static class DeckEvaluator
{
    public static DeckScore Evaluate(IReadOnlyList<CardInfo> deck, IReadOnlyList<CardStats[]> npcHands, RuleSet rules,
        DeckEvaluationOptions options, bool exact, CancellationToken cancel = default)
    {
        var evalRules = DeckRules.ForEvaluation(rules);
        exact &= !evalRules.Has(TriadRule.Chaos) && !evalRules.Has(TriadRule.Order);
        var stats = deck.Select(c => (CardStats?)c.Stats).ToArray();
        var deals = npcHands.SelectMany(hand => new[] { (Hand: hand, First: Side.Blue), (Hand: hand, First: Side.Red) }).ToList();
        var results = new (int Win, int Draw, int Loss, int Perfect)[deals.Count];
        var seed = DeckSeed(deck);

        void Run(int i)
        {
            var (hand, first) = deals[i];
            var position = new Position(evalRules, new BoardCard?[Position.Cells], (CardStats?[])stats.Clone(),
                hand.Select(c => (CardStats?)c).ToArray(), first)
            {
                // The NPC's deck order is not visible: its Order is a random draw, as in MatchView.
                RedSelection = evalRules.Has(TriadRule.Chaos) || evalRules.Has(TriadRule.Order) ? CardSelection.Random : CardSelection.Free,
            };
            var rng = new Random(seed ^ (i * 7919));
            results[i] = exact
                ? ExactDeal(position, options, rng, cancel)
                : QuickDeal(position, options, rng, cancel);
        }

        if (exact)
            Parallel.For(0, deals.Count, new ParallelOptions { CancellationToken = cancel }, Run);
        else
        {
            for (var i = 0; i < deals.Count; i++)
                Run(i);
        }

        var games = results.Sum(r => r.Win + r.Draw + r.Loss);
        if (games == 0)
            return new DeckScore(deck, 0, 0, 0, 0, exact);
        var score = new DeckScore(deck, games,
            results.Sum(r => r.Win) / (double)games,
            results.Sum(r => r.Draw) / (double)games,
            results.Sum(r => r.Loss) / (double)games,
            exact);
        if (!exact)
            return score;
        return score with
        {
            PerfectWin = results.Count(r => r.Perfect > 0) / (double)deals.Count,
            PerfectDraw = results.Count(r => r.Perfect == 0) / (double)deals.Count,
            PerfectLoss = results.Count(r => r.Perfect < 0) / (double)deals.Count,
        };
    }

    private static (int, int, int, int) ExactDeal(Position position, DeckEvaluationOptions options, Random rng, CancellationToken cancel)
    {
        var solver = new Solver(position, options.TableBits, cancel);
        var root = solver.Evaluate();
        var perfect = Math.Sign(position.ToMove == Side.Blue ? root : -root);
        int win = 0, draw = 0, loss = 0;
        for (var n = 0; n < options.Playouts; n++)
        {
            cancel.ThrowIfCancellationRequested();
            var outcome = Playout(solver.Engine, options.Model, rng, solver);
            if (outcome > 0)
                win++;
            else if (outcome == 0)
                draw++;
            else
                loss++;
        }

        return (win, draw, loss, perfect);
    }

    private static (int, int, int, int) QuickDeal(Position position, DeckEvaluationOptions options, Random rng, CancellationToken cancel)
    {
        var engine = new GameEngine(position);
        int win = 0, draw = 0, loss = 0;
        for (var n = 0; n < options.QuickPlayouts; n++)
        {
            cancel.ThrowIfCancellationRequested();
            var outcome = Playout(engine, options.Model, rng, solver: null);
            if (outcome > 0)
                win++;
            else if (outcome == 0)
                draw++;
            else
                loss++;
        }

        return (win, draw, loss, 0);
    }

    // One game from the engine's current state; the engine is left as found. With a solver, Blue
    // only plays moves that keep the minimax value.
    private static int Playout(GameEngine engine, IOpponentModel model, Random rng, Solver? solver)
    {
        var played = new Stack<(Move Move, int Undo)>(Position.Cells);
        Span<double> weights = stackalloc double[45];
        while (!engine.IsFinished)
        {
            var hand = engine.SelectableHand();
            if (engine.MoverSelection == CardSelection.Random && hand != 0)
                hand = DrawSlot(hand, rng);
            var moves = Moves(engine, hand);
            Move move;
            if (engine.BlueToMove)
                move = solver is null ? Greedy(engine, moves, rng) : SafeGreedy(solver, moves, rng);
            else
            {
                var w = weights[..moves.Length];
                model.Weigh(engine, moves, w);
                move = moves[Sample(w, rng)];
            }

            played.Push((move, engine.Play(move.HandSlot, move.Cell)));
        }

        var outcome = engine.BlueOutcome;
        while (played.Count > 0)
        {
            var (move, undo) = played.Pop();
            engine.Undo(move.HandSlot, move.Cell, undo);
        }

        return outcome;
    }

    // The most cards for the mover right now; ties at random.
    private static Move Greedy(GameEngine engine, Move[] moves, Random rng)
    {
        var best = int.MinValue;
        var pick = moves[0];
        var ties = 0;
        foreach (var move in moves)
        {
            var score = Gain(engine, move);
            if (score > best)
            {
                best = score;
                pick = move;
                ties = 1;
            }
            else if (score == best && rng.Next(++ties) == 0)
                pick = move;
        }

        return pick;
    }

    // Greedy among the moves that keep the minimax value. Moves are tried from the greediest down
    // and the first safe one is taken. Without chance nodes values are -1/0/1, so the current
    // value and each move's safety are null-window tests rather than exact searches.
    private static Move SafeGreedy(Solver solver, Move[] moves, Random rng)
    {
        var engine = solver.Engine;
        var value = solver.AtLeast(1) ? 1 : solver.AtLeast(0) ? 0 : -1;
        var order = moves.Select(m => (Move: m, Gain: Gain(engine, m), Tie: rng.Next())).OrderByDescending(x => x.Gain).ThenBy(x => x.Tie);
        foreach (var (move, _, _) in order)
        {
            var undo = engine.Play(move.HandSlot, move.Cell);
            // Safe when the opponent cannot get more than -value from here.
            var safe = !solver.AtLeast(-value + 0.5);
            engine.Undo(move.HandSlot, move.Cell, undo);
            if (safe)
                return move;
        }

        return moves[0];
    }

    // Cards the mover owns on the board after the move (as GreedyOpponent counts them).
    private static int Gain(GameEngine engine, Move move)
    {
        var undo = engine.Play(move.HandSlot, move.Cell);
        var score = engine.Placed - engine.MoverBoardCount;
        engine.Undo(move.HandSlot, move.Cell, undo);
        return score;
    }

    private static int DrawSlot(int hand, Random rng)
    {
        var count = System.Numerics.BitOperations.PopCount((uint)hand);
        var pick = rng.Next(count);
        for (var slot = 0; slot < Position.HandSize; slot++)
        {
            if ((hand & (1 << slot)) == 0)
                continue;
            if (pick-- == 0)
                return 1 << slot;
        }

        return hand;
    }

    private static int Sample(ReadOnlySpan<double> weights, Random rng)
    {
        var roll = rng.NextDouble();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
                return i;
        }

        return weights.Length - 1;
    }

    private static Move[] Moves(GameEngine engine, int hand)
    {
        var empty = engine.EmptyMask;
        var list = new List<Move>(45);
        for (var slot = 0; slot < Position.HandSize; slot++)
        {
            if ((hand & (1 << slot)) == 0)
                continue;
            for (var cell = 0; cell < Position.Cells; cell++)
                if ((empty & (1 << cell)) != 0)
                    list.Add(new Move(slot, cell));
        }

        return [.. list];
    }

    private static int DeckSeed(IReadOnlyList<CardInfo> deck)
    {
        var seed = 17;
        foreach (var card in deck)
            seed = unchecked((seed * 31) + card.Id);
        return seed;
    }
}
