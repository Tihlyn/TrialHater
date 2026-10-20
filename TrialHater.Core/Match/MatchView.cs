using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;

namespace TrialHater.Core.Match;

public enum SlotState : byte
{
    Empty,
    Hidden,
    Visible,
}

public readonly record struct HandSlot(SlotState State, CardStats Card = default)
{
    public static readonly HandSlot Empty = new(SlotState.Empty);
    public static readonly HandSlot Hidden = new(SlotState.Hidden);

    public static HandSlot Of(CardStats card) => new(SlotState.Visible, card);

    public override string ToString() => this.State switch
    {
        SlotState.Visible => this.Card.ToString(),
        SlotState.Hidden => "??",
        _ => "-",
    };
}

// What the player can see of a match at one moment, plus the history the screen no longer shows
// (cards the NPC already played, cards exchanged by Swap). Plain data: built by the plugin's
// reader, replayed by tests. Blue is the player.
public sealed record MatchView(
    RuleSet Rules,
    BoardCard?[] Board,
    HandSlot[] Blue,
    HandSlot[] Red,
    Side ToMove,
    int ForcedSlot = -1,
    uint NpcTriadId = 0,
    int Round = 0)
{
    // Cards Red has placed this round, in order.
    public IReadOnlyList<CardStats> RedPlayed { get; init; } = [];

    // Cards Red holds or has played that came from our deck (Swap); not part of the NPC's pool.
    public IReadOnlyList<CardStats> RedForeign { get; init; } = [];

    // NPC deck cards now in our hand (Swap): seen, and no longer in Red's hand.
    public IReadOnlyList<CardStats> RedLost { get; init; } = [];

    // Draft: the star rating of each of our hand slots as dealt (null if not read). In a Draft
    // tournament every deck is one card per rarity in the same slot order, so this is also the
    // opponent's.
    public IReadOnlyList<byte>? SlotStars { get; init; }

    // Draft: the opponent's cards seen in earlier matches of this tournament (its drafted deck is
    // kept throughout), up to 5.
    public IReadOnlyList<CardStats> KnownDeck { get; init; } = [];

    public int HiddenRedCount => this.Red.Count(s => s.State == SlotState.Hidden);

    public bool RedFullyKnown => this.HiddenRedCount == 0;

    // Freshness key: two views with equal fingerprints lead to the same decision. Anything that can
    // change between solving and acting (a card landing, a turn passing, a forced draw) is in it.
    public string Fingerprint()
    {
        var board = string.Join(",", this.Board.Select(c => c is { } b ? $"{(int)b.Owner}{b.Card}" : "."));
        var blue = string.Join(",", this.Blue.Select(s => s.ToString()));
        var red = string.Join(",", this.Red.Select(s => s.ToString()));
        return $"{this.Rules.Mask}|{board}|{blue}|{red}|{this.ToMove}|{this.ForcedSlot}|{this.NpcTriadId}|{this.Round}|{string.Join(",", this.RedPlayed)}|{string.Join(",", this.KnownDeck)}";
    }

    // The position with Red's hand replaced by one hypothesis. Red's card order is not visible
    // to us, so an Order NPC is searched as if its card were drawn at random.
    public Position ToPosition(CardStats?[] redHand)
    {
        var blue = this.Blue.Select(s => s.State == SlotState.Visible ? s.Card : (CardStats?)null).ToArray();
        var redSelection = this.Rules.Has(TriadRule.Chaos) || (this.Rules.Has(TriadRule.Order) && !this.RedFullyKnown)
            ? CardSelection.Random
            : this.Rules.Has(TriadRule.Order) ? CardSelection.Ordered : CardSelection.Free;
        return new Position(this.Rules, this.Board, blue, redHand, this.ToMove)
        {
            ForcedSlot = this.ToMove == Side.Blue ? this.ForcedSlot : -1,
            RedSelection = redSelection,
        };
    }
}
