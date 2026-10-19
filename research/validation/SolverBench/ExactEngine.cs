using System.Numerics;

namespace SolverBench;

// Independent full-information engine: plain captures, optional Same/Plus with Combo.
// Card instances 0-4 belong to Blue, 5-9 to Red. Values are W/D/L in {-1,0,1} from the side to move.
public sealed class ExactEngine
{
    public const int Empty = 255;
    private static readonly int[] Nb = BuildNeighbors(); // [cell*4+side], -1 if off board

    private readonly byte[,] sides = new byte[10, 4]; // Up, Right, Down, Left
    public readonly byte[] Cell = new byte[9];
    public int OwnerMask;   // bit = Blue owns the card in that cell
    public int BlueHand;    // bits 0-4
    public int RedHand;     // bits 0-4 (instance = 5 + bit)
    public int Placed;
    public bool BlueStarts;
    public readonly bool Same, Plus;

    private readonly ulong[,] cardZ = new ulong[9, 10];
    private readonly ulong[] ownerZ = new ulong[512];
    private ulong cardKey;

    private readonly ulong[] ttKey;
    private readonly sbyte[] ttVal;
    private readonly byte[] ttFlag; // 1 exact, 2 lower, 3 upper
    private readonly byte[] ttMove;
    private readonly int ttMaskBits;
    public long Nodes;

    public ExactEngine(byte[][] blue, byte[][] red, bool blueStarts, bool same, bool plus, int ttBits = 22)
    {
        for (var i = 0; i < 5; i++)
            for (var s = 0; s < 4; s++) { sides[i, s] = blue[i][s]; sides[5 + i, s] = red[i][s]; }
        Same = same; Plus = plus; BlueStarts = blueStarts;
        Array.Fill(Cell, (byte)Empty);
        BlueHand = RedHand = 0b11111;
        var rng = new Random(12345);
        for (var c = 0; c < 9; c++) for (var k = 0; k < 10; k++) cardZ[c, k] = (ulong)rng.NextInt64();
        for (var m = 0; m < 512; m++) ownerZ[m] = (ulong)rng.NextInt64();
        ttMaskBits = (1 << ttBits) - 1;
        ttKey = new ulong[1 << ttBits]; ttVal = new sbyte[1 << ttBits]; ttFlag = new byte[1 << ttBits]; ttMove = new byte[1 << ttBits];
    }

    public bool BlueToMove => (Placed % 2 == 0) == BlueStarts;

    private static int[] BuildNeighbors()
    {
        var t = new int[36];
        for (var p = 0; p < 9; p++)
        {
            int x = p % 3, y = p / 3;
            t[p * 4 + 0] = y > 0 ? p - 3 : -1;
            t[p * 4 + 1] = x < 2 ? p + 1 : -1;
            t[p * 4 + 2] = y < 2 ? p + 3 : -1;
            t[p * 4 + 3] = x > 0 ? p - 1 : -1;
        }
        return t;
    }

    private bool IsBlue(int cell) => (OwnerMask & (1 << cell)) != 0;

    // Returns the undo token (previous owner mask).
    public int Play(int handBit, int cell)
    {
        var undo = OwnerMask;
        var blue = BlueToMove;
        var inst = blue ? handBit : 5 + handBit;
        if (blue) BlueHand &= ~(1 << handBit); else RedHand &= ~(1 << handBit);
        Cell[cell] = (byte)inst;
        cardKey ^= cardZ[cell, inst];
        if (blue) OwnerMask |= 1 << cell; else OwnerMask &= ~(1 << cell);
        Placed++;

        Span<int> combo = stackalloc int[9];
        var comboCount = 0;
        if (Same || Plus)
        {
            Span<int> nbCell = stackalloc int[4];
            Span<int> mine = stackalloc int[4];
            Span<int> theirs = stackalloc int[4];
            var n = 0;
            for (var s = 0; s < 4; s++)
            {
                var nb = Nb[cell * 4 + s];
                if (nb < 0 || Cell[nb] == Empty) continue;
                nbCell[n] = nb; mine[n] = sides[inst, s]; theirs[n] = sides[Cell[nb], (s + 2) & 3]; n++;
            }
            var flip = 0;
            if (Same)
            {
                var cnt = 0;
                for (var i = 0; i < n; i++) if (mine[i] == theirs[i]) cnt++;
                if (cnt >= 2)
                    for (var i = 0; i < n; i++) if (mine[i] == theirs[i]) flip |= 1 << i;
            }
            if (Plus)
            {
                for (var i = 0; i < n; i++)
                    for (var j = 0; j < n; j++)
                        if (i != j && mine[i] + theirs[i] == mine[j] + theirs[j]) flip |= 1 << i;
            }
            for (var i = 0; i < n; i++)
            {
                if ((flip & (1 << i)) == 0 || IsBlue(nbCell[i]) == blue) continue;
                if (blue) OwnerMask |= 1 << nbCell[i]; else OwnerMask &= ~(1 << nbCell[i]);
                combo[comboCount++] = nbCell[i];
            }
        }

        // Plain captures from the placed card; they do not combo.
        CaptureFrom(cell, blue, combo, ref comboCount, addToCombo: false);

        // Combo: rule captures chain using plain comparisons.
        for (var head = 0; head < comboCount; head++)
            CaptureFrom(combo[head], blue, combo, ref comboCount, addToCombo: true);

        return undo;
    }

    private void CaptureFrom(int cell, bool blue, Span<int> combo, ref int comboCount, bool addToCombo)
    {
        var inst = Cell[cell];
        for (var s = 0; s < 4; s++)
        {
            var nb = Nb[cell * 4 + s];
            if (nb < 0 || Cell[nb] == Empty || IsBlue(nb) == blue) continue;
            if (sides[inst, s] <= sides[Cell[nb], (s + 2) & 3]) continue;
            if (blue) OwnerMask |= 1 << nb; else OwnerMask &= ~(1 << nb);
            if (addToCombo) combo[comboCount++] = nb;
        }
    }

    public void Undo(int handBit, int cell, int undo)
    {
        Placed--;
        var blue = BlueToMove;
        var inst = Cell[cell];
        cardKey ^= cardZ[cell, inst];
        Cell[cell] = Empty;
        OwnerMask = undo;
        if (blue) BlueHand |= 1 << handBit; else RedHand |= 1 << handBit;
    }

    // Blue's card total including a card left in hand; 5 of 10 is a draw.
    public int BlueScore() => BitOperations.PopCount((uint)OwnerMask) + BitOperations.PopCount((uint)BlueHand);

    private int TerminalValueForMover()
    {
        var b = BlueScore();
        var blueVal = b > 5 ? 1 : b == 5 ? 0 : -1;
        return BlueToMove ? blueVal : -blueVal;
    }

    public void RebuildKey()
    {
        cardKey = 0;
        for (var c = 0; c < 9; c++) if (Cell[c] != Empty) cardKey ^= cardZ[c, Cell[c]];
    }

    public ulong Key() => cardKey ^ ownerZ[OwnerMask & OccupiedMask()];
    private int OccupiedMask() { var m = 0; for (var c = 0; c < 9; c++) if (Cell[c] != Empty) m |= 1 << c; return m; }

    public int Negamax(int alpha, int beta)
    {
        Nodes++;
        if (Placed == 9) return TerminalValueForMover();
        var key = Key();
        var slot = (int)(key & (ulong)ttMaskBits);
        var ttBest = 255;
        if (ttKey[slot] == key && ttFlag[slot] != 0)
        {
            int v = ttVal[slot];
            if (ttFlag[slot] == 1) return v;
            if (ttFlag[slot] == 2 && v >= beta) return v;
            if (ttFlag[slot] == 3 && v <= alpha) return v;
            ttBest = ttMove[slot];
        }
        var a0 = alpha;
        var best = -2;
        var bestMove = 255;
        var hand = BlueToMove ? BlueHand : RedHand;

        // Try the remembered best move first.
        if (ttBest != 255)
        {
            int hb = ttBest >> 4, c = ttBest & 15;
            if ((hand & (1 << hb)) != 0 && Cell[c] == Empty)
            {
                var u = Play(hb, c);
                var v = -Negamax(-beta, -alpha);
                Undo(hb, c, u);
                best = v; bestMove = ttBest;
                if (v > alpha) alpha = v;
            }
        }
        if (alpha < beta)
        {
            for (var hb = 0; hb < 5 && alpha < beta; hb++)
            {
                if ((hand & (1 << hb)) == 0) continue;
                for (var c = 0; c < 9; c++)
                {
                    if (Cell[c] != Empty) continue;
                    var mv = (hb << 4) | c;
                    if (mv == bestMove) continue;
                    var u = Play(hb, c);
                    var v = -Negamax(-beta, -alpha);
                    Undo(hb, c, u);
                    if (v > best) { best = v; bestMove = mv; }
                    if (v > alpha) alpha = v;
                    if (alpha >= beta) break;
                }
            }
        }
        ttKey[slot] = key; ttVal[slot] = (sbyte)best; ttMove[slot] = (byte)bestMove;
        ttFlag[slot] = (byte)(best <= a0 ? 3 : best >= beta ? 2 : 1);
        return best;
    }

    // Exact value of every legal move for the side to move.
    public List<(int hand, int cell, int value)> ScoreAllMoves()
    {
        var list = new List<(int, int, int)>();
        var hand = BlueToMove ? BlueHand : RedHand;
        for (var hb = 0; hb < 5; hb++)
        {
            if ((hand & (1 << hb)) == 0) continue;
            for (var c = 0; c < 9; c++)
            {
                if (Cell[c] != Empty) continue;
                var u = Play(hb, c);
                var v = -Negamax(-1, 1);
                Undo(hb, c, u);
                list.Add((hb, c, v));
            }
        }
        return list;
    }

    // Root value only, with the full window; cheaper than scoring every move.
    public (int hand, int cell, int value) BestMove()
    {
        var hand = BlueToMove ? BlueHand : RedHand;
        (int, int, int) best = (-1, -1, -2);
        var alpha = -1;
        for (var hb = 0; hb < 5; hb++)
        {
            if ((hand & (1 << hb)) == 0) continue;
            for (var c = 0; c < 9; c++)
            {
                if (Cell[c] != Empty) continue;
                var u = Play(hb, c);
                var v = -Negamax(-1, -alpha);
                Undo(hb, c, u);
                if (v > best.Item3) { best = (hb, c, v); if (v > alpha) alpha = v; }
                if (alpha >= 1) return best;
            }
        }
        return best;
    }
}
