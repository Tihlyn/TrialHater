using System.Text.Json;
using System.Text.RegularExpressions;
using TrialHater.Core.Cards;
using TrialHater.Core.Rules;

namespace Arena;

// One recorded matchup to replay: the NPC, the rules in force, and the hand we brought (as dealt,
// before any Swap). Played = how many recorded matches had exactly this setup.
internal sealed record Scenario(uint Npc, string Name, string Rules, string[] Hand, int Played)
{
    public RuleSet RuleSet => RuleSet.Of(Parse.Rules(this.Rules));

    public CardStats[] HandStats => [.. this.Hand.Select(Parse.Card)];

    public string Key => $"{this.Npc}|{this.Rules}|{string.Join(" ", this.Hand)}";
}

internal static class Scenarios
{
    // Rules the arena cannot simulate from deck data: Draft and Random replace the deck, and an
    // unresolved Roulette has no rule yet.
    private static readonly TriadRule[] Unsupported = [TriadRule.Draft, TriadRule.Random, TriadRule.Roulette];

    // Every round-0 NPC match in the plugin's probe file whose NPC has deck data.
    public static List<Scenario> FromProbe(string probePath, NpcCatalog npcs)
    {
        var placement = new Regex(@"^(we|red) played ");
        var swap = new Regex(@"^swap: gave (\S+), received (\S+)");
        var matches = new List<List<JsonElement>>();
        foreach (var line in File.ReadLines(probePath))
        {
            var row = JsonDocument.Parse(line).RootElement;
            if (row.GetProperty("trigger").GetString()!.StartsWith("match #", StringComparison.Ordinal) || matches.Count == 0)
                matches.Add([]);
            matches[^1].Add(row);
        }

        var found = new Dictionary<string, Scenario>();
        foreach (var lines in matches)
        {
            var rows = lines.Where(r => !r.TryGetProperty("round", out var round) || round.ValueKind != JsonValueKind.Number || round.GetInt32() == 0).ToList();
            var first = rows.FirstOrDefault(r => placement.IsMatch(r.GetProperty("trigger").GetString()!));
            if (first.ValueKind == JsonValueKind.Undefined || first.GetProperty("blue").ValueKind != JsonValueKind.Array)
                continue;
            var rules = Parse.Rules(rows.Last().GetProperty("rules").GetString()!).ToList();
            if (rules.Any(Unsupported.Contains) || npcs.Get(first.GetProperty("npc").GetUInt32()) is not { } npc)
                continue;
            var hand = first.GetProperty("blue").EnumerateArray().Select(s => s.GetString()!).ToList();
            if (hand.Count != 5 || hand.Any(s => s == "-") || first.GetProperty("board").EnumerateArray().Any(s => s.GetString() != "-"))
                continue;

            var cards = hand.Select(Parse.Slot).ToList();
            if (rules.Contains(TriadRule.Swap))
            {
                // Back to the hand as dealt: the card we received goes, the card we gave returns.
                var note = rows.Select(r => swap.Match(r.GetProperty("trigger").GetString()!)).FirstOrDefault(m => m.Success);
                if (note is null)
                    continue;
                var received = cards.IndexOf(Parse.Card(note.Groups[2].Value));
                if (received < 0)
                    continue;
                cards[received] = Parse.Card(note.Groups[1].Value);
            }

            var text = cards.Select(c => c.ToString()).ToArray();
            var ruleText = string.Join(", ", rules.Order());
            var scenario = new Scenario(npc.TriadId, npc.Name, ruleText, text, 1);
            found[scenario.Key] = found.TryGetValue(scenario.Key, out var known) ? known with { Played = known.Played + 1 } : scenario;
        }

        return [.. found.Values.OrderByDescending(s => s.Played).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    public static void Save(string path, List<Scenario> scenarios)
        => File.WriteAllText(path, JsonSerializer.Serialize(scenarios, new JsonSerializerOptions { WriteIndented = true }));

    public static List<Scenario> Load(string path)
        => JsonSerializer.Deserialize<List<Scenario>>(File.ReadAllText(path))!;
}

internal static class Parse
{
    // "Plus, Swap" or "(none)".
    public static IEnumerable<TriadRule> Rules(string text)
        => text.Split(", ", StringSplitOptions.RemoveEmptyEntries).Where(r => r != "(none)").Select(Enum.Parse<TriadRule>);

    // "7A91" or "2647/Beastman": up, right, down, left (A = 10), then the type.
    public static CardStats Card(string text)
    {
        var parts = text.Split('/');
        var s = parts[0].Select(ch => ch is 'A' or 'a' ? (byte)10 : (byte)(ch - '0')).ToArray();
        return new CardStats(s[0], s[1], s[2], s[3], parts.Length > 1 ? Enum.Parse<CardType>(parts[1]) : CardType.None);
    }

    // A probe slot: "U,R,D,L tT rN oN tintN".
    public static CardStats Slot(string slot)
    {
        var parts = slot.Split(' ');
        var s = parts[0].Split(',').Select(byte.Parse).ToArray();
        return new CardStats(s[0], s[1], s[2], s[3], (CardType)byte.Parse(parts[1][1..]));
    }
}
