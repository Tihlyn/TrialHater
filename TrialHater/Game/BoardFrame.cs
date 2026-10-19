namespace TrialHater.Game;

// One card slot of AddonTripleTriad as copied out of game memory. Sides are what the slot shows;
// whether that includes an Ascension/Descension shift is still to be confirmed in game.
public sealed record SlotRaw(
    bool HasCard,
    byte Up,
    byte Right,
    byte Down,
    byte Left,
    byte Type,
    byte Rarity,
    byte Owner,
    byte MinTint)
{
    // A face-down card: present but with no numbers (both inspected plugins rely on this).
    public bool Hidden => this.HasCard && this.Up == 0;

    // MinTint is the lowest MultiplyRed over the card's images. Saucy and ATTG treat < 100 as
    // "Chaos did not draw this card", but it stayed 255 through a whole Chaos match on 2026-10-19;
    // the forced slot comes from the board command instead. Kept in probe output only.
}

// Everything read from the board addon in one frame. Plain data: the tracker and the window never
// touch game memory, and recorded frames can be replayed.
//
// Command / Arg1 / Arg2 are the agent's last instruction to the board (AtkValues[23..25]), as
// observed 2026-10-19 (docs/MODEL_MAPPING.md):
//   7 (side: 0 player, 1 NPC)   turn starts        1 (0, -1)              player may act
//   6 (0, slot)                 player may act, but must play `slot` (Chaos; 2026-10-19 capture)
//   25 (side, slot)             card picked         23 (cell, slot)       card placed
//   27 (cell, slot)             placement resolved  3 (?)                 captures shown
//   14 (11, 121622)             rule banner         17 (2, cell)          type value update
//   10 (1, 0)                   board full          12, 16, 9, 11         match start sequence
public sealed record BoardFrame(
    long Frame,
    bool Visible,
    bool Ready,
    byte TurnState,
    int? Command,
    int? Arg1,
    int? Arg2,
    SlotRaw[] Blue,
    SlotRaw[] Red,
    SlotRaw[] Board,
    IReadOnlyList<string> Texts,
    IReadOnlyList<int?> LeadingValues)
{
    public const byte TurnWaiting = 0;
    public const byte TurnNormal = 1;
    public const byte TurnMasked = 2;   // Order / Chaos: the card is chosen for us
    public const int CommandPlayerActs = 1;
    public const int CommandPlayerActsForced = 6;

    // The opponent's name as the board shows it (AtkValues[4], or [1] when we sit red).
    public string? OpponentName { get; init; }

    // We are the board's red player (a tournament against a player); the reader has already
    // mirrored hands and owners so that Blue is us.
    public bool SeatedRed { get; init; }

    public int BlueInHand => this.Blue.Count(s => s.HasCard);

    public int RedInHand => this.Red.Count(s => s.HasCard);

    public int OnBoard => this.Board.Count(s => s.HasCard);

    // Our move is expected now: the agent told the board the player may act, and the board is
    // taking input. In the 2026-10-19 captures this held at every player turn (command 1 with
    // TurnState 1; command 6 with TurnState 2 under Chaos) and never on the NPC's turn.
    public bool CanAct => this.Command is CommandPlayerActs or CommandPlayerActsForced && this.TurnState is TurnNormal or TurnMasked;

    // The hand slot the game makes us play (Chaos draws it). In the capture every play used it.
    public int ForcedSlot => this.Command == CommandPlayerActsForced && this.Arg2 is >= 0 and <= 4 ? this.Arg2.Value : -1;
}
