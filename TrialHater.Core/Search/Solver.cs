using TrialHater.Core.Engine;
using GameEngine = TrialHater.Core.Engine.Engine;

namespace TrialHater.Core.Search;

public readonly record struct MoveValue(Move Move, double Value);

// Exact game-theoretic value of a full-information position: negamax with alpha-beta and a
// transposition table over W/D/L (+1/0/-1 from the side to move). Random card selection
// (Chaos, or an opponent's Order in an unknown order) is a chance node: the value is the average
// over the cards that could come up, each followed by the mover's best cell for that card.
//
// Benchmarked on real decks (docs/RESEARCH.md): the hardest decision of a match, the opening,
// solves in ~0.35 s on one core; every later move is faster. Not thread-safe: one per worker.
public sealed class Solver
{
    private const byte Exact = 1;
    private const byte Lower = 2;
    private const byte Upper = 3;
    private const byte NoMove = 255;
    private const int CancelCheckInterval = 4096;

    private readonly Entry[] table;
    private readonly int tableMask;
    private readonly CancellationToken cancel;

    public Solver(Position position, int tableBits = 19, CancellationToken cancel = default)
    {
        this.Engine = new GameEngine(position);
        this.Position = position;
        this.table = new Entry[1 << tableBits];
        this.tableMask = (1 << tableBits) - 1;
        this.cancel = cancel;
    }

    public GameEngine Engine { get; }

    public Position Position { get; }

    public long Nodes { get; private set; }

    // Value of the root for the side to move.
    public double RootValue()
    {
        var moves = this.ScoreRoot(fullWindow: false);
        return moves.Count == 0 ? this.Terminal() : moves.Max(m => m.Value);
    }

    // Exact value of every legal root move, for the side to move. A forced slot (Chaos draw,
    // Order) restricts the root to that card; it is not a chance node because it is known.
    public IReadOnlyList<MoveValue> ScoreRootMoves() => this.ScoreRoot(fullWindow: true);

    // Exact values of a subset of root moves (the policy splits the root across workers; moves
    // scored by one solver share its table).
    public IReadOnlyList<MoveValue> ScoreMoves(IEnumerable<Move> moves)
    {
        var list = new List<MoveValue>();
        foreach (var move in moves)
        {
            var undo = this.Engine.Play(move.HandSlot, move.Cell);
            var value = -this.Negamax(-1, 1);
            this.Engine.Undo(move.HandSlot, move.Cell, undo);
            list.Add(new MoveValue(move, value));
        }

        return list;
    }

    // Value of the current engine state for the side to move; used by evaluators that walk the
    // tree themselves and share this solver's table.
    public double Evaluate() => this.Negamax(-1, 1);

    private List<MoveValue> ScoreRoot(bool fullWindow)
    {
        var list = new List<MoveValue>(45);
        var engine = this.Engine;
        var hand = engine.SelectableHand(this.Position.ForcedSlot);
        var empty = engine.EmptyMask;
        var alpha = -1.0;
        for (var slot = 0; slot < Position.HandSize; slot++)
        {
            if ((hand & (1 << slot)) == 0)
                continue;
            for (var cell = 0; cell < Position.Cells; cell++)
            {
                if ((empty & (1 << cell)) == 0)
                    continue;
                // A won root cannot improve; only the full-window scoring needs the rest.
                if (!fullWindow && alpha >= 1)
                    return list;
                var undo = engine.Play(slot, cell);
                var value = -this.Negamax(-1, fullWindow ? 1 : -alpha);
                engine.Undo(slot, cell, undo);
                list.Add(new MoveValue(new Move(slot, cell), value));
                alpha = Math.Max(alpha, value);
            }
        }

        return list;
    }

    private double Terminal() => this.Engine.BlueToMove ? this.Engine.BlueOutcome : -this.Engine.BlueOutcome;

    private double Negamax(double alpha, double beta)
    {
        if (++this.Nodes % CancelCheckInterval == 0)
            this.cancel.ThrowIfCancellationRequested();

        var engine = this.Engine;
        if (engine.IsFinished)
            return this.Terminal();

        // An empty window has nothing to prove; fail low (callers keep windows non-empty).
        if (alpha >= beta)
            return alpha;

        var key = engine.Key;
        ref var entry = ref this.table[(int)(key & (ulong)this.tableMask)];
        var remembered = NoMove;
        if (entry.Key == key && entry.Flag != 0)
        {
            var stored = entry.Value;
            if (entry.Flag == Exact
                || (entry.Flag == Lower && stored >= beta)
                || (entry.Flag == Upper && stored <= alpha))
                return stored;
            remembered = entry.Move;
        }

        double best;
        byte bestMove;
        byte flag;
        if (engine.MoverSelection == CardSelection.Random && BitCount(engine.MoverHand) > 1)
        {
            best = this.ChanceValue(alpha, beta);
            bestMove = NoMove;
            flag = best <= alpha ? Upper : best >= beta ? Lower : Exact;
        }
        else
        {
            (best, bestMove) = this.MaxValue(engine.SelectableHand(), alpha, beta, remembered);
            if (bestMove == NoMove)
                return this.Terminal();
            flag = best <= alpha ? Upper : best >= beta ? Lower : Exact;
        }

        // Re-fetch: recursion may have overwritten the slot.
        entry = ref this.table[(int)(key & (ulong)this.tableMask)];
        entry.Key = key;
        entry.Value = best;
        entry.Move = bestMove;
        entry.Flag = flag;
        return best;
    }

    // Average over the card that comes up, then the best cell for it. Star1 pruning (Ballard 1983):
    // every outcome is in [-1, 1], so after k of n cards the average is bounded; each remaining card
    // is searched only in the window that could still move the average across (alpha, beta), and
    // the node stops as soon as it cannot. A fail-low/high child implies the node fails the same
    // way, so a completed loop is exact. Without this a Chaos opening took over a minute.
    private double ChanceValue(double alpha, double beta)
    {
        var hand = this.Engine.MoverHand;
        var n = BitCount(hand);
        var sum = 0.0;
        var done = 0;
        for (var slot = 0; slot < Position.HandSize; slot++)
        {
            if ((hand & (1 << slot)) == 0)
                continue;
            var rest = n - done - 1;
            var low = Math.Max(-1.0, (n * alpha) - sum - rest);
            var high = Math.Min(1.0, (n * beta) - sum + rest);

            // In exact arithmetic the cutoffs below keep this window non-empty; rounding can miss a
            // cutoff by 1e-16 and leave it empty, and an empty max is -infinity. Return the bound
            // the cutoff would have (a live 2026-10-19 Chaos position scored a move -infinity).
            if (high <= -1)
                return (sum - rest - 1) / n;
            if (low >= 1)
                return (sum + rest + 1) / n;

            sum += this.MaxValue(1 << slot, low, high, NoMove).Value;
            done++;
            var left = n - done;
            if ((sum + left) / n <= alpha)
                return (sum + left) / n;
            if ((sum - left) / n >= beta)
                return (sum - left) / n;
        }

        return sum / n;
    }

    private (double Value, byte Move) MaxValue(int hand, double alpha, double beta, byte remembered)
    {
        var engine = this.Engine;
        var empty = engine.EmptyMask;
        var best = double.NegativeInfinity;
        var bestMove = NoMove;
        if (remembered != NoMove)
        {
            int slot = remembered >> 4, cell = remembered & 15;
            if ((hand & (1 << slot)) != 0 && (empty & (1 << cell)) != 0)
            {
                var undo = engine.Play(slot, cell);
                var value = -this.Negamax(-beta, -alpha);
                engine.Undo(slot, cell, undo);
                best = value;
                bestMove = remembered;
                alpha = Math.Max(alpha, value);
            }
        }

        for (var slot = 0; slot < Position.HandSize && alpha < beta; slot++)
        {
            if ((hand & (1 << slot)) == 0)
                continue;
            for (var cell = 0; cell < Position.Cells; cell++)
            {
                if ((empty & (1 << cell)) == 0)
                    continue;
                var move = (byte)((slot << 4) | cell);
                if (move == bestMove)
                    continue;
                var undo = engine.Play(slot, cell);
                var value = -this.Negamax(-beta, -alpha);
                engine.Undo(slot, cell, undo);
                if (value > best)
                {
                    best = value;
                    bestMove = move;
                }

                alpha = Math.Max(alpha, value);
                if (alpha >= beta)
                    break;
            }
        }

        return (best, bestMove);
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    private struct Entry
    {
        public ulong Key;
        public double Value;
        public byte Flag;
        public byte Move;
    }
}
