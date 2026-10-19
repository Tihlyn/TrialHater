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
    var blue = first.GetProperty("blue").EnumerateArray().Select(s => Parse(s.GetString()!, swapped)).ToArray();

    // Red's hand in play order (its slot order is not recoverable from the probe; play order works).
    var redCards = events.Where(e => e.M.Groups[1].Value == "red").Select(e => Parse(lines[e.Line + 1].GetProperty("board")[int.Parse(e.M.Groups[3].Value)].GetString()!, swapped)!.Value).ToList();
    while (redCards.Count < 5)
        redCards.Add(new CardStats(1, 1, 1, 1));
    var blueFirst = events[0].M.Groups[1].Value == "we";
    var engine = new Engine(new Position(rules, new BoardCard?[9], blue, redCards.Select(c => (CardStats?)c).ToArray(), blueFirst ? Side.Blue : Side.Red));
    var usedBlue = new bool[5];
    var redNext = 0;
    var label = $"{first.GetProperty("time").GetDateTime():HH:mm} npc {first.GetProperty("npc").GetUInt32()} [{rules}]";
    var ok = true;
    foreach (var (line, m) in events)
    {
        if (line + 1 >= lines.Count)
            break;
        var cell = int.Parse(m.Groups[3].Value);
        int slot;
        if (m.Groups[1].Value == "we")
        {
            var played = Parse(lines[line + 1].GetProperty("board")[cell].GetString()!, swapped);
            slot = Enumerable.Range(0, 5).First(s => !usedBlue[s] && blue[s] == played);
            usedBlue[slot] = true;
        }
        else
        {
            slot = redNext++;
        }

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
