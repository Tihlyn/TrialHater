using System.Text.Json;
using System.Text.RegularExpressions;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;

// Usage: ProbeReplay [model_probe.jsonl]
// Each probe line is written at a tracker event and carries the board as it stood just before that
// event's frame, so the board on line i+1 is the result of the placement announced on line i.
// Probe lines written before the 2026-10-19 left/right fix have the two sides swapped; they are
// detected by the time stamp and corrected.
var path = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "TrialHater", "model_probe.jsonl");
var swappedBefore = DateTime.Parse("2026-10-19T19:15:00Z").ToUniversalTime();
var rows = File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
var placement = new Regex(@"^(we|red) played (\S+) at (\d)$");

// The match counter restarts with every plugin reload, so matches are split on their start line.
var matches = new List<List<JsonElement>>();
foreach (var row in rows)
{
    if (row.GetProperty("trigger").GetString()!.StartsWith("match #", StringComparison.Ordinal) || matches.Count == 0)
        matches.Add([]);
    matches[^1].Add(row);
}

int checkedPlacements = 0, mismatches = 0, replayed = 0;
foreach (var lines in matches.Where(m => m.Any(r => placement.IsMatch(r.GetProperty("trigger").GetString()!))))
{
    var swapped = lines[0].GetProperty("time").GetDateTime().ToUniversalTime() < swappedBefore;
    var rules = RuleSet.Of(lines.Last().GetProperty("rules").GetString()!.Split(", ", StringSplitOptions.RemoveEmptyEntries)
        .Where(r => r != "(none)").Select(Enum.Parse<TriadRule>));
    var events = lines.Select((r, i) => (Line: i, M: placement.Match(r.GetProperty("trigger").GetString()!))).Where(x => x.M.Success).ToList();
    var first = lines[events[0].Line];

    // A plugin (re)loaded mid-match records from a board that already has cards on it; the hands
    // and placements before that are unknown, so there is nothing to replay.
    if (first.GetProperty("board") is { ValueKind: JsonValueKind.Array } start && start.EnumerateArray().Any(s => s.GetString() != "-"))
    {
        Console.WriteLine($"{first.GetProperty("time").GetDateTime():HH:mm} npc {first.GetProperty("npc").GetUInt32()}: skipped (recording starts mid-match)");
        continue;
    }

    // Both hands in play order, read from the board after each placement: slot order is not
    // needed to check captures, and a hand we could not see (the board's blue player when we sat
    // red, recorded before the reader mirrored it) still replays.
    List<CardStats> PlayOrder(string who)
    {
        var cards = events.Where(e => e.M.Groups[1].Value == who && e.Line + 1 < lines.Count)
            .Select(e => Parse(lines[e.Line + 1].GetProperty("board")[int.Parse(e.M.Groups[3].Value)].GetString()!, swapped)!.Value).ToList();
        while (cards.Count < 5)
            cards.Add(new CardStats(1, 1, 1, 1));
        return cards;
    }

    var blueCards = PlayOrder("we");
    var redCards = PlayOrder("red");
    var blueFirst = events[0].M.Groups[1].Value == "we";
    var engine = new Engine(new Position(rules, new BoardCard?[9], blueCards.Select(c => (CardStats?)c).ToArray(), redCards.Select(c => (CardStats?)c).ToArray(), blueFirst ? Side.Blue : Side.Red));
    var blueNext = 0;
    var redNext = 0;
    var label = $"{first.GetProperty("time").GetDateTime():HH:mm} npc {first.GetProperty("npc").GetUInt32()} [{rules}]";
    var ok = true;
    foreach (var (line, m) in events)
    {
        if (line + 1 >= lines.Count)
            break;
        var cell = int.Parse(m.Groups[3].Value);
        var slot = m.Groups[1].Value == "we" ? blueNext++ : redNext++;

        if ((m.Groups[1].Value == "we") != engine.BlueToMove)
        {
            Console.WriteLine($"{label}: turn order differs at '{m.Value}'");
            ok = false;
            break;
        }

        engine.Play(slot, cell);
        checkedPlacements++;
        var expected = string.Concat(lines[line + 1].GetProperty("board").EnumerateArray().Select(s =>
            s.GetString() == "-" ? '.' : s.GetString()!.Contains(" o1 ") ? 'B' : 'R'));
        var actual = string.Concat(Enumerable.Range(0, 9).Select(c => engine.CellInstance(c) == Engine.Empty ? '.' : engine.IsBlue(c) ? 'B' : 'R'));
        if (expected != actual)
        {
            mismatches++;
            ok = false;
            Console.WriteLine($"{label}: after '{m.Value}' game {expected} engine {actual}");
            break;
        }
    }

    replayed++;
    Console.WriteLine($"{label}: {(ok ? "ok" : "MISMATCH")}");
}

Console.WriteLine($"{replayed} matches, {checkedPlacements} placements checked, {mismatches} mismatches");
return mismatches == 0 ? 0 : 1;

// "U,R,D,L tT rN oN tintN" as the probe prints a slot.
static CardStats? Parse(string slot, bool swapped)
{
    if (slot == "-")
        return null;
    var parts = slot.Split(' ');
    var s = parts[0].Split(',').Select(byte.Parse).ToArray();
    var type = (CardType)byte.Parse(parts[1][1..]);
    return swapped ? new CardStats(s[0], s[3], s[2], s[1], type) : new CardStats(s[0], s[1], s[2], s[3], type);
}
