using TrialHater.Core.Engine;
using GameEngine = TrialHater.Core.Engine.Engine;

namespace TrialHater.Core.Search;

// How we expect the NPC to choose among the moves the game allows it. Used only to choose between
// moves that are equally safe; it never overrides the minimax guarantee.
public interface IOpponentModel
{
    string Name { get; }

    // Fills `weights` (same order as `moves`, summing to 1) for the side to move in `engine`.
    // The engine may be played/undone but must be left as found.
    void Weigh(GameEngine engine, ReadOnlySpan<Move> moves, Span<double> weights);
}

// Greedy with noise: most of the time the move that leaves the NPC owning the most cards right
// now (ties shared), otherwise any legal move. Epsilon 0 is pure greedy. A starting point until
// logged NPC moves can fit something better (docs/RESEARCH.md, "NPC model").
public sealed class GreedyOpponent(double epsilon = 0.1) : IOpponentModel
{
    public double Epsilon { get; } = Math.Clamp(epsilon, 0, 1);

    public string Name => this.Epsilon == 0 ? "greedy" : $"greedy(eps={this.Epsilon:0.##})";

    public void Weigh(GameEngine engine, ReadOnlySpan<Move> moves, Span<double> weights)
    {
        if (moves.Length == 0)
            return;
        Span<int> score = moves.Length <= 64 ? stackalloc int[moves.Length] : new int[moves.Length];
        var best = int.MinValue;
        for (var i = 0; i < moves.Length; i++)
        {
            var undo = engine.Play(moves[i].HandSlot, moves[i].Cell);
            // After Play the side to move has flipped; count for the side that just moved.
            score[i] = engine.Placed - engine.MoverBoardCount;
            engine.Undo(moves[i].HandSlot, moves[i].Cell, undo);
            best = Math.Max(best, score[i]);
        }

        var top = 0;
        for (var i = 0; i < moves.Length; i++)
            if (score[i] == best)
                top++;
        var uniform = this.Epsilon / moves.Length;
        for (var i = 0; i < moves.Length; i++)
            weights[i] = uniform + (score[i] == best ? (1 - this.Epsilon) / top : 0);
    }
}

// Every legal move equally likely: what the TriadBuddy lineage implicitly assumes.
public sealed class UniformOpponent : IOpponentModel
{
    public string Name => "uniform";

    public void Weigh(GameEngine engine, ReadOnlySpan<Move> moves, Span<double> weights)
    {
        for (var i = 0; i < moves.Length; i++)
            weights[i] = 1.0 / moves.Length;
    }
}
