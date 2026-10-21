using TrialHater.Core.Engine;
using TrialHater.Core.Search;

namespace Arena;

// How the simulated NPC picks its move. It sees both hands (the position is the true state).
internal interface IOpponent
{
    string Name { get; }

    Move Choose(Engine engine, Position state, Random random);
}

internal static class Opponents
{
    // "greedy:0.1", "uniform", "softmax:2", "minimax", "minimax:0.05" (a random move that often).
    public static IOpponent Parse(string spec)
    {
        var parts = spec.Split(':');
        var arg = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
        return parts[0] switch
        {
            "greedy" => new ModelOpponent(spec, new GreedyOpponent(arg ?? 0.1)),
            "uniform" => new ModelOpponent(spec, new UniformOpponent()),
            "softmax" => new ModelOpponent(spec, new SoftmaxGain(arg ?? 2)),
            "minimax" => new MinimaxOpponent(spec, arg ?? 0),
            _ => throw new ArgumentException($"unknown opponent '{spec}'"),
        };
    }
}

// Samples a move from one of the plugin's opponent models.
internal sealed class ModelOpponent(string name, IOpponentModel model) : IOpponent
{
    public string Name => name;

    public Move Choose(Engine engine, Position state, Random random)
    {
        var moves = state.LegalMoves().ToArray();
        var weights = new double[moves.Length];
        model.Weigh(engine, moves, weights);
        var roll = random.NextDouble();
        for (var i = 0; i < moves.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
                return moves[i];
        }

        return moves[^1];
    }
}

// The worst case for us: the NPC plays a minimax-best move with both hands known (ties at random),
// or with probability epsilon any legal move.
internal sealed class MinimaxOpponent(string name, double epsilon) : IOpponent
{
    public string Name => name;

    public Move Choose(Engine engine, Position state, Random random)
    {
        var moves = state.LegalMoves().ToArray();
        if (random.NextDouble() < epsilon)
            return moves[random.Next(moves.Length)];
        var values = new Solver(state, 18).ScoreMoves(moves);
        var best = values.Max(v => v.Value);
        var top = values.Where(v => v.Value >= best - 1e-9).ToList();
        return top[random.Next(top.Count)].Move;
    }
}

// Probability rising with the cards the NPC would own right after the move: exp(beta * gain).
// Fits the logged NPC moves better than greedy(0.1) (research/validation/ModelFit, 2026-10-21).
internal sealed class SoftmaxGain(double beta) : IOpponentModel
{
    public string Name => $"softmax-gain(b={beta})";

    public void Weigh(Engine engine, ReadOnlySpan<Move> moves, Span<double> weights)
    {
        var gains = new int[moves.Length];
        for (var i = 0; i < moves.Length; i++)
        {
            var undo = engine.Play(moves[i].HandSlot, moves[i].Cell);
            gains[i] = engine.Placed - engine.MoverBoardCount;
            engine.Undo(moves[i].HandSlot, moves[i].Cell, undo);
        }

        var max = gains.Max();
        var sum = 0.0;
        for (var i = 0; i < moves.Length; i++)
            sum += weights[i] = Math.Exp(beta * (gains[i] - max));
        for (var i = 0; i < moves.Length; i++)
            weights[i] /= sum;
    }
}
