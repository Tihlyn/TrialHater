using System.Text.Json;
using TrialHater.Core.Engine;

namespace TrialHater.Core.Search;

// Root move values of full-information positions already solved, reused when the same position
// comes back. Farming one NPC with one deck deals the same openings again and again: at most 10
// hands x 5 Chaos draws when we open, and the NPC's first card x 9 cells when it does. The values
// are exact, so a reused value is the one a new solve would give: only the time changes. Early
// positions only (MaxPlaced), where a solve costs seconds; later ones solve in milliseconds.
// Thread-safe: the policy's workers read and fill it.
public sealed class SolveCache
{
    // Bump with any change to solver values (the file is dropped when it differs).
    public const int Version = 1;

    public const int MaxPlaced = 1;

    // About 500 B per position in the file.
    public const int MaxEntries = 10000;

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private long clock;

    public int Count
    {
        get
        {
            lock (this.gate)
                return this.entries.Count;
        }
    }

    // Goes up with every stored position; the plugin saves when it changed.
    public int Changes { get; private set; }

    // Our moves only (the policy's): the NPC's slot order is left out of the key.
    public static bool Applies(Position position) => position.Placed <= MaxPlaced && position.ToMove == Side.Blue;

    // The values of every legal root move of `position`, if it was solved before.
    public bool TryGet(Position position, out IReadOnlyList<MoveValue> values)
    {
        values = [];
        if (!Applies(position))
            return false;
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(Key(position), out var entry))
                return false;
            entry.Used = ++this.clock;
            values = Unpack(entry.Values);
            return true;
        }
    }

    // `values` must cover every legal root move of `position`.
    public void Put(Position position, IReadOnlyList<MoveValue> values)
    {
        if (!Applies(position))
            return;
        lock (this.gate)
        {
            this.entries[Key(position)] = new Entry { Values = Pack(values), Used = ++this.clock };
            if (this.entries.Count > MaxEntries)
            {
                // Drop the least recently used tenth.
                foreach (var key in this.entries.OrderBy(e => e.Value.Used).Take(this.entries.Count - (MaxEntries * 9 / 10)).Select(e => e.Key).ToList())
                    this.entries.Remove(key);
            }

            this.Changes++;
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.entries.Clear();
            this.Changes++;
        }
    }

    public string ToJson()
    {
        lock (this.gate)
            return JsonSerializer.Serialize(new CacheFile { Version = Version, Positions = this.entries.OrderBy(e => e.Value.Used).ToDictionary(e => e.Key, e => e.Value.Values) });
    }

    // An empty cache when the file is from another version.
    public static SolveCache FromJson(string json)
    {
        var cache = new SolveCache();
        var file = JsonSerializer.Deserialize<CacheFile>(json);
        if (file is null || file.Version != Version)
            return cache;
        foreach (var (key, values) in file.Positions)
            cache.entries[key] = new Entry { Values = values, Used = ++cache.clock };
        return cache;
    }

    // Everything the root values depend on: rules, board, both hands, who moves, the forced slot and
    // how each side picks its card. Our slots name our moves, so our hand keeps its order; the NPC's
    // order only matters under Order, so otherwise its cards are sorted and every deal of one hand
    // (whichever slots Three Open showed) is the same position.
    private static string Key(Position position)
    {
        var board = string.Join(" ", position.Board.Select(c => c is { } b ? $"{(b.Owner == Side.Blue ? 'B' : 'R')}{b.Card}" : "."));
        var blue = string.Join(" ", position.BlueHand.Select(c => c?.ToString() ?? "-"));
        var red = position.RedHand.Select(c => c?.ToString() ?? "-");
        if (position.RedSelection != CardSelection.Ordered)
            red = red.Where(c => c != "-").Order(StringComparer.Ordinal);
        return $"{position.Rules.Mask}|{board}|{blue}|{string.Join(" ", red)}|{position.ToMove}|{position.ForcedSlot}|{position.BlueSelection}|{position.RedSelection}|{position.RedHiddenSlots}";
    }

    // Per move: slot * 16 + cell, then the value in whole solver units (Solver.Scale), so the stored
    // number is exact and short.
    private static int[] Pack(IReadOnlyList<MoveValue> values)
    {
        var packed = new int[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            packed[i * 2] = (values[i].Move.HandSlot * 16) + values[i].Move.Cell;
            packed[(i * 2) + 1] = (int)Math.Round(values[i].Value * Solver.Scale);
        }

        return packed;
    }

    private static List<MoveValue> Unpack(int[] packed)
    {
        var values = new List<MoveValue>(packed.Length / 2);
        for (var i = 0; i + 1 < packed.Length; i += 2)
            values.Add(new MoveValue(new Move(packed[i] >> 4, packed[i] & 15), packed[i + 1] / Solver.Scale));
        return values;
    }

    private sealed class Entry
    {
        public int[] Values { get; set; } = [];

        public long Used { get; set; }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }

        public Dictionary<string, int[]> Positions { get; set; } = [];
    }
}
