using System.Numerics;
using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Engine;

// Make/unmake rules engine over a Position. Not thread-safe: one instance per worker.
//
// Card instances: 0-4 Blue's hand slots, 5-9 Red's, 10-18 cards already on the board at the
// root (instance = 10 + cell). Ownership is one 9-bit mask (bit set = Blue owns the cell).
//
// Capture order for one placement (TriadBuddy's documented spec, shared by Saucy and ATTG; to be
// confirmed against the client - docs/RULES.md):
//   1. Ascension/Descension: every typed card is worth +-1 per card of its type already on the
//      board, the placed card excluded; the shift is applied before comparing and clamped 1..A.
//   2. Same / Plus on the placed card. Every occupied neighbour takes part, friendly ones
//      included; the enemy ones among the matches flip and seed the combo.
//   3. Plain captures from the placed card, with Reverse and Fallen Ace applied.
//   4. Combo: each card flipped by step 2 (and then by this step) captures weaker enemy
//      neighbours with a plain comparison - no Reverse, no Fallen Ace, no new Same/Plus.
//   5. The placed card's type count goes up, which shifts later placements.
public sealed class Engine
{
    public const int Instances = 19;
    public const byte Empty = 255;
    private const int MaxValue = 10;

    private static readonly int[] Neighbors = BuildNeighbors();

    private readonly byte[] sides = new byte[Instances * 4];
    private readonly CardType[] types = new CardType[Instances];
    private readonly byte[] cells = new byte[Position.Cells];
    private readonly int[] typeCount = new int[5];
    private readonly ulong[,] cardZobrist = new ulong[Position.Cells, Instances];
    private readonly ulong[] ownerZobrist = new ulong[1 << Position.Cells];
    private readonly ulong blueToMoveZobrist;
    private readonly int[] combo = new int[Position.Cells];
    private ulong cardKey;

    public Engine(Position position)
    {
        this.Rules = position.Rules;
        this.Same = position.Rules.Has(TriadRule.Same);
        this.Plus = position.Rules.Has(TriadRule.Plus);
        this.Reverse = position.Rules.Has(TriadRule.Reverse);
        this.FallenAce = position.Rules.Has(TriadRule.FallenAce);
        this.TypeStep = position.Rules.TypeStep;
        this.BlueSelection = position.BlueSelection;
        this.RedSelection = position.RedSelection;

        var rng = new Random(0x7A1A1);
        for (var c = 0; c < Position.Cells; c++)
            for (var i = 0; i < Instances; i++)
                this.cardZobrist[c, i] = (ulong)rng.NextInt64();
        for (var m = 0; m < this.ownerZobrist.Length; m++)
            this.ownerZobrist[m] = (ulong)rng.NextInt64();
        this.blueToMoveZobrist = (ulong)rng.NextInt64();

        Array.Fill(this.cells, Empty);
        for (var slot = 0; slot < Position.HandSize; slot++)
        {
            if (position.BlueHand[slot] is { } b)
            {
                this.SetCard(slot, b);
                this.BlueHand |= 1 << slot;
            }

            if (position.RedHand[slot] is { } r)
            {
                this.SetCard(5 + slot, r);
                this.RedHand |= 1 << slot;
            }
        }

        for (var cell = 0; cell < Position.Cells; cell++)
        {
            if (position.Board[cell] is not { } placed)
                continue;
            var inst = 10 + cell;
            this.SetCard(inst, placed.Card);
            this.cells[cell] = (byte)inst;
            this.cardKey ^= this.cardZobrist[cell, inst];
            if (placed.Owner == Side.Blue)
                this.OwnerMask |= 1 << cell;
            if (placed.Card.Type != CardType.None)
                this.typeCount[(int)placed.Card.Type]++;
            this.Placed++;
        }

        this.BlueToMove = position.ToMove == Side.Blue;
    }

    public RuleSet Rules { get; }

    public bool Same { get; }

    public bool Plus { get; }

    public bool Reverse { get; }

    public bool FallenAce { get; }

    public int TypeStep { get; }

    public CardSelection BlueSelection { get; }

    public CardSelection RedSelection { get; }

    public int OwnerMask { get; private set; }

    public int BlueHand { get; private set; }

    public int RedHand { get; private set; }

    public int Placed { get; private set; }

    public bool BlueToMove { get; private set; }

    public bool IsFinished => this.Placed == Position.Cells;

    public int MoverHand => this.BlueToMove ? this.BlueHand : this.RedHand;

    public CardSelection MoverSelection => this.BlueToMove ? this.BlueSelection : this.RedSelection;

    public int EmptyMask
    {
        get
        {
            var mask = 0;
            for (var c = 0; c < Position.Cells; c++)
                if (this.cells[c] == Empty)
                    mask |= 1 << c;
            return mask;
        }
    }

    public ulong Key => this.cardKey ^ this.ownerZobrist[this.OwnerMask] ^ (this.BlueToMove ? this.blueToMoveZobrist : 0);

    public byte CellInstance(int cell) => this.cells[cell];

    public bool IsBlue(int cell) => (this.OwnerMask & (1 << cell)) != 0;

    // Blue's total: owned board cards plus any card still in Blue's hand (the game counts it).
    public int BlueScore => BitOperations.PopCount((uint)this.OwnerMask) + BitOperations.PopCount((uint)this.BlueHand);

    // +1 / 0 / -1 for Blue at the end of the board.
    public int BlueOutcome
    {
        get
        {
            var score = this.BlueScore;
            return score > 5 ? 1 : score == 5 ? 0 : -1;
        }
    }

    // Cards the mover may choose from right now (Ordered: the leftmost). Under Random the caller
    // treats this mask as the chance outcomes rather than a choice.
    public int SelectableHand(int forcedSlot = -1)
    {
        var hand = this.MoverHand;
        if (forcedSlot >= 0)
            return hand & (1 << forcedSlot);
        if (this.MoverSelection == CardSelection.Ordered && hand != 0)
            return hand & -hand;
        return hand;
    }

    // Places the mover's hand-slot card. Returns the undo token (the ownership before the move).
    public int Play(int slot, int cell)
    {
        var undo = this.OwnerMask;
        var blue = this.BlueToMove;
        var inst = blue ? slot : 5 + slot;
        if (blue)
            this.BlueHand &= ~(1 << slot);
        else
            this.RedHand &= ~(1 << slot);
        this.cells[cell] = (byte)inst;
        this.cardKey ^= this.cardZobrist[cell, inst];
        this.SetOwner(cell, blue);
        this.Placed++;

        var comboCount = 0;
        if (this.Same || this.Plus)
            comboCount = this.SpecialCaptures(cell, inst, blue);

        this.PlainCaptures(cell, inst, blue);

        for (var head = 0; head < comboCount; head++)
            comboCount = this.ComboCaptures(this.combo[head], blue, comboCount);

        var type = this.types[inst];
        if (type != CardType.None)
            this.typeCount[(int)type]++;
        this.BlueToMove = !blue;
        return undo;
    }

    public void Undo(int slot, int cell, int undo)
    {
        this.BlueToMove = !this.BlueToMove;
        var blue = this.BlueToMove;
        var inst = this.cells[cell];
        var type = this.types[inst];
        if (type != CardType.None)
            this.typeCount[(int)type]--;
        this.cardKey ^= this.cardZobrist[cell, inst];
        this.cells[cell] = Empty;
        this.OwnerMask = undo;
        this.Placed--;
        if (blue)
            this.BlueHand |= 1 << slot;
        else
            this.RedHand |= 1 << slot;
    }

    // Cards the mover owns on the board after a hypothetical move, for one-ply opponent models.
    public int MoverBoardCount => this.BlueToMove
        ? BitOperations.PopCount((uint)this.OwnerMask)
        : this.Placed - BitOperations.PopCount((uint)this.OwnerMask);

    public CardStats CardOf(int inst) => new(
        this.sides[inst * 4],
        this.sides[inst * 4 + 1],
        this.sides[inst * 4 + 2],
        this.sides[inst * 4 + 3],
        this.types[inst]);

    // The value a side shows right now, type shift included (step 1 above).
    public int Value(int inst, int side)
    {
        var raw = this.sides[inst * 4 + side];
        var type = this.types[inst];
        if (this.TypeStep == 0 || type == CardType.None)
            return raw;
        return Math.Clamp(raw + this.TypeStep * this.typeCount[(int)type], 1, MaxValue);
    }

    private void SetCard(int inst, CardStats card)
    {
        this.sides[inst * 4] = card.Up;
        this.sides[inst * 4 + 1] = card.Right;
        this.sides[inst * 4 + 2] = card.Down;
        this.sides[inst * 4 + 3] = card.Left;
        this.types[inst] = card.Type;
    }

    private void SetOwner(int cell, bool blue)
    {
        if (blue)
            this.OwnerMask |= 1 << cell;
        else
            this.OwnerMask &= ~(1 << cell);
    }

    private int SpecialCaptures(int cell, int inst, bool blue)
    {
        Span<int> neighbor = stackalloc int[4];
        Span<int> mine = stackalloc int[4];
        Span<int> theirs = stackalloc int[4];
        var n = 0;
        for (var side = 0; side < 4; side++)
        {
            var nb = Neighbors[cell * 4 + side];
            if (nb < 0 || this.cells[nb] == Empty)
                continue;
            neighbor[n] = nb;
            mine[n] = this.Value(inst, side);
            theirs[n] = this.Value(this.cells[nb], (side + 2) & 3);
            n++;
        }

        var matched = 0;
        if (this.Same)
        {
            var equal = 0;
            for (var i = 0; i < n; i++)
                if (mine[i] == theirs[i])
                    equal++;
            if (equal >= 2)
                for (var i = 0; i < n; i++)
                    if (mine[i] == theirs[i])
                        matched |= 1 << i;
        }

        if (this.Plus)
        {
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    if (i != j && mine[i] + theirs[i] == mine[j] + theirs[j])
                        matched |= 1 << i;
        }

        var count = 0;
        for (var i = 0; i < n; i++)
        {
            if ((matched & (1 << i)) == 0 || this.IsBlue(neighbor[i]) == blue)
                continue;
            this.SetOwner(neighbor[i], blue);
            this.combo[count++] = neighbor[i];
        }

        return count;
    }

    private void PlainCaptures(int cell, int inst, bool blue)
    {
        for (var side = 0; side < 4; side++)
        {
            var nb = Neighbors[cell * 4 + side];
            if (nb < 0 || this.cells[nb] == Empty || this.IsBlue(nb) == blue)
                continue;
            if (this.Beats(this.Value(inst, side), this.Value(this.cells[nb], (side + 2) & 3)))
                this.SetOwner(nb, blue);
        }
    }

    private int ComboCaptures(int cell, bool blue, int comboCount)
    {
        var inst = this.cells[cell];
        for (var side = 0; side < 4; side++)
        {
            var nb = Neighbors[cell * 4 + side];
            if (nb < 0 || this.cells[nb] == Empty || this.IsBlue(nb) == blue)
                continue;
            if (this.Value(inst, side) <= this.Value(this.cells[nb], (side + 2) & 3))
                continue;
            this.SetOwner(nb, blue);
            this.combo[comboCount++] = nb;
        }

        return comboCount;
    }

    // Placement comparison (step 3). Fallen Ace: a 1 takes an A, and under Reverse an A takes a 1.
    private bool Beats(int mine, int theirs)
    {
        if (this.FallenAce)
        {
            if (!this.Reverse && mine == 1 && theirs == MaxValue)
                return true;
            if (this.Reverse && mine == MaxValue && theirs == 1)
                return true;
        }

        return this.Reverse ? mine < theirs : mine > theirs;
    }

    private static int[] BuildNeighbors()
    {
        var table = new int[Position.Cells * 4];
        for (var cell = 0; cell < Position.Cells; cell++)
        {
            int x = cell % 3, y = cell / 3;
            table[cell * 4 + 0] = y > 0 ? cell - 3 : -1;
            table[cell * 4 + 1] = x < 2 ? cell + 1 : -1;
            table[cell * 4 + 2] = y < 2 ? cell + 3 : -1;
            table[cell * 4 + 3] = x > 0 ? cell - 1 : -1;
        }

        return table;
    }
}
