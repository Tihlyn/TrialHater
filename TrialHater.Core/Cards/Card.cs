namespace TrialHater.Core.Cards;

// TripleTriadCardType row ids. "None" is the untyped majority of the catalogue.
public enum CardType : byte
{
    None = 0,
    Primal = 1,
    Scion = 2,
    Beastman = 3,
    Garlean = 4,
}

// Everything the rules look at: four sides (A = 10) and the type. Two different cards with
// the same stats are interchangeable for play; identity only matters for belief and collection.
public readonly record struct CardStats(byte Up, byte Right, byte Down, byte Left, CardType Type = CardType.None)
{
    public const int SideCount = 4;

    // Sides are indexed clockwise from the top, so the side facing a neighbour is (side + 2) & 3.
    public byte Side(int side) => side switch
    {
        0 => this.Up,
        1 => this.Right,
        2 => this.Down,
        _ => this.Left,
    };

    public int SideSum => this.Up + this.Right + this.Down + this.Left;

    public override string ToString()
        => $"{Glyph(this.Up)}{Glyph(this.Right)}{Glyph(this.Down)}{Glyph(this.Left)}{(this.Type == CardType.None ? string.Empty : $"/{this.Type}")}";

    public static char Glyph(byte value) => value >= 10 ? 'A' : (char)('0' + value);
}

// One catalogue entry. Id is the TripleTriadCard / TripleTriadCardResident row id; Number is
// the album number the game displays, which is not the same thing (Tiisol Ja: id 475, No. 460).
public sealed record CardInfo(ushort Id, string Name, byte Stars, CardStats Stats, ushort Number = 0)
{
    public override string ToString() => $"{this.Name} [{this.Stats}] {this.Stars}*";
}
