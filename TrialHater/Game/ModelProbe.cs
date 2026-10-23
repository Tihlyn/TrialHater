using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TrialHater.Game;

// Mapping aid for the game's own match model, UIState.TripleTriad (0x1830 bytes, unmapped in
// ClientStructs). Appends one JSON line per tracker event: the event, the board as the addon shows
// it, and the raw model bytes, so fields can be correlated offline (docs/MODEL_MAPPING.md).
internal sealed unsafe class ModelProbe
{
    public const int ModelSize = 0x1830;

    private readonly string path;

    public ModelProbe(string path)
    {
        this.path = path;
    }

    public static nint ModelAddress()
    {
        var state = UIState.Instance();
        return state == null ? 0 : (nint)(&state->TripleTriad);
    }

    public void Capture(string trigger, MatchTracker tracker)
    {
        if (Read(trigger, tracker) is not { } sample)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        File.AppendAllLines(this.path, [JsonSerializer.Serialize(sample)]);
    }

    // The model as it is now with what the board shows, or null when there is none. Everything in
    // it is copied, so a report can serialize it on a worker.
    public static object? Read(string trigger, MatchTracker tracker)
    {
        var address = ModelAddress();
        if (address == 0)
            return null;
        var bytes = new ReadOnlySpan<byte>((void*)address, ModelSize).ToArray();
        var frame = tracker.Last;
        return new
        {
            time = DateTime.UtcNow,
            trigger,
            match = tracker.MatchGeneration,
            round = tracker.Round,
            turn = tracker.TurnGeneration,
            npc = tracker.NpcTriadId,
            rules = tracker.Rules.ToString(),
            turnState = frame?.TurnState,
            canAct = frame?.CanAct,
            board = frame?.Board.Select(Describe).ToArray(),
            blue = frame?.Blue.Select(Describe).ToArray(),
            red = frame?.Red.Select(Describe).ToArray(),
            values = frame?.LeadingValues.ToArray(),
            model = Convert.ToHexString(bytes),
        };
    }

    private static string Describe(SlotRaw slot)
        => !slot.HasCard ? "-" : $"{slot.Up},{slot.Right},{slot.Down},{slot.Left} t{slot.Type} r{slot.Rarity} o{slot.Owner} tint{slot.MinTint}";
}
