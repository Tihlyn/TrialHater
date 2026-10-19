using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TrialHater.Game;

// What one dispatch did. Dispatched means the callback reached the addon, NOT that the game took
// the move: acceptance is a later observation (a card landing on the board).
public sealed record DispatchResult(bool Sent, string Detail)
{
    public static DispatchResult Refused(string why) => new(false, why);
}

// Plays a card the way the board's own click handler does: callback 14 with the cell in the low
// half and the hand slot in the high half (identical in Saucy and ATTG; to be matched against a
// manual placement captured with Cartographer). Framework thread only.
internal static unsafe class BoardActuator
{
    public const int PlaceCardEvent = 14;

    public static DispatchResult PlaceCard(AtkUnitBase* unit, int slot, int cell)
    {
        if (unit == null || !unit->IsVisible || unit->RootNode == null)
            return DispatchResult.Refused("board addon not open");
        if (slot is < 0 or > 4 || cell is < 0 or > 8)
            return DispatchResult.Refused($"slot {slot} / cell {cell} out of range");

        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = PlaceCardEvent;
        values[1].Type = AtkValueType.UInt;
        values[1].UInt = (uint)cell | ((uint)slot << 16);
        unit->FireCallback(2, values, true);
        return new DispatchResult(true, $"callback [{PlaceCardEvent}, {values[1].UInt:X}] slot {slot} -> cell {cell}");
    }
}
