using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Engine;

// Blue is always the player, matching the game's own colouring.
public enum Side : byte
{
    None = 0,
    Blue = 1,
    Red = 2,
}

// How the side to move picks its card. Ordered and Random replace the free choice: Order makes the
// leftmost remaining card mandatory; Chaos (and an opponent's Order with an unknown card order)
// makes the card a chance outcome, so the search averages over it instead of choosing it.
public enum CardSelection : byte
{
    Free,
    Ordered,
    Random,
}

public readonly record struct BoardCard(CardStats Card, Side Owner);

public readonly record struct Move(int HandSlot, int Cell)
{
    public override string ToString() => $"slot {this.HandSlot} -> cell {this.Cell}";
}

// A full-information match position: the engine's input. The player's view of an NPC match has
// hidden red cards; the belief layer turns that view into one Position per hypothesis.
public sealed record Position
{
    public const int Cells = 9;
    public const int HandSize = 5;

    public Position(RuleSet rules, BoardCard?[] board, CardStats?[] blueHand, CardStats?[] redHand, Side toMove)
    {
        if (board.Length != Cells || blueHand.Length != HandSize || redHand.Length != HandSize)
            throw new ArgumentException("A position has 9 cells and two 5-slot hands.");
        if (toMove is not (Side.Blue or Side.Red))
            throw new ArgumentException("Someone has to be on the move.", nameof(toMove));
        this.Rules = rules;
        this.Board = board;
        this.BlueHand = blueHand;
        this.RedHand = redHand;
        this.ToMove = toMove;
        this.BlueSelection = rules.Has(TriadRule.Chaos) ? CardSelection.Random : rules.Has(TriadRule.Order) ? CardSelection.Ordered : CardSelection.Free;
        this.RedSelection = this.BlueSelection;
    }

    public RuleSet Rules { get; }

    public BoardCard?[] Board { get; }

    // Slot order is the game's hand order (deck order), which Order depends on; null = played.
    public CardStats?[] BlueHand { get; }

    public CardStats?[] RedHand { get; }

    public Side ToMove { get; }

    // The slot the side to move is forced to play this turn, as the game shows it (Chaos draws it,
    // Order is the leftmost). -1 when the choice is free or not yet known.
    public int ForcedSlot { get; init; } = -1;

    public CardSelection BlueSelection { get; init; }

    // An NPC's Order hand is in an order we cannot see, so callers usually downgrade it to Random.
    public CardSelection RedSelection { get; init; }

    public int Placed => this.Board.Count(c => c is not null);

    public bool IsFinished => this.Placed == Cells;

    public IEnumerable<Move> LegalMoves()
    {
        var hand = this.ToMove == Side.Blue ? this.BlueHand : this.RedHand;
        var selection = this.ToMove == Side.Blue ? this.BlueSelection : this.RedSelection;
        IEnumerable<int> slots;
        if (this.ForcedSlot >= 0)
            slots = hand[this.ForcedSlot] is null ? [] : [this.ForcedSlot];
        else if (selection == CardSelection.Ordered)
            slots = Enumerable.Range(0, HandSize).Where(s => hand[s] is not null).Take(1);
        else
            slots = Enumerable.Range(0, HandSize).Where(s => hand[s] is not null);

        foreach (var slot in slots)
            for (var cell = 0; cell < Cells; cell++)
                if (this.Board[cell] is null)
                    yield return new Move(slot, cell);
    }

    // A deterministic text form for logs, fixtures and freshness checks.
    public string Describe()
    {
        var cells = string.Join(" ", this.Board.Select(c => c is { } b ? $"{(b.Owner == Side.Blue ? 'B' : 'R')}{b.Card}" : "."));
        var blue = string.Join(" ", this.BlueHand.Select(c => c?.ToString() ?? "-"));
        var red = string.Join(" ", this.RedHand.Select(c => c?.ToString() ?? "-"));
        return $"[{this.Rules}] board: {cells} | blue: {blue} | red: {red} | {this.ToMove} to move{(this.ForcedSlot >= 0 ? $" (forced slot {this.ForcedSlot})" : string.Empty)}";
    }
}
