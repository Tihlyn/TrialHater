using System.Text.Json;
using Arena;
using TrialHater.Core.Cards;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;

// Arena scenarios [probe=model_probe.jsonl] [out=scenarios.json] [data=research/data]
//   Recorded NPC matchups (NPC, rules, our dealt hand) from the plugin's probe file.
// Arena run [scenarios=scenarios.json] [games=20] [policies=baseline,surewin] [opponents=greedy:0.1]
//           [shard=0/1] [threads=N] [inner=1] [live=14] [exploit=3] [out=results.jsonl] [data=research/data] [limit=0] [filter=text]
//   Plays games x scenarios x policies x opponents; each game seed is shared by every policy and
//   opponent. Results already in `out` are skipped, so a stopped run resumes.
// Arena report results.jsonl [more.jsonl ...]
//   Win/draw/loss per policy and opponent, and the paired comparison between policies.
var command = args.Length > 0 ? args[0] : "help";
var options = args.Skip(1).Where(a => a.Contains('=')).ToDictionary(a => a[..a.IndexOf('=')], a => a[(a.IndexOf('=') + 1)..]);
string Opt(string key, string fallback) => options.GetValueOrDefault(key, fallback);

switch (command)
{
    case "scenarios":
    {
        var data = options.TryGetValue("data", out var given) ? given : DataDir();
        var npcs = NpcCatalog.FromCsv(Path.Combine(data, "npc-decks.csv"));
        var probe = Opt("probe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "TrialHater", "model_probe.jsonl"));
        var list = Scenarios.FromProbe(probe, npcs);
        Scenarios.Save(Opt("out", "scenarios.json"), list);
        Console.WriteLine($"{list.Count} scenarios from {list.Sum(s => s.Played)} recorded matches");
        foreach (var g in list.GroupBy(s => s.Rules).OrderByDescending(g => g.Sum(s => s.Played)))
            Console.WriteLine($"  [{g.Key}] {g.Count()} scenarios, {g.Sum(s => s.Played)} matches: {string.Join(", ", g.Select(s => s.Name).Distinct())}");
        return 0;
    }

    case "run":
        return Run();

    case "report":
        Report.Print(args.Skip(1).Where(a => !a.Contains('=')).ToList());
        return 0;

    default:
        Console.WriteLine("Arena scenarios | run | report  (see the header of Program.cs)");
        return 1;
}

int Run()
{
    var data = options.TryGetValue("data", out var given) ? given : DataDir();
    var catalog = CardCatalog.FromCsv(Path.Combine(data, "cards.csv"));
    var npcs = NpcCatalog.FromCsv(Path.Combine(data, "npc-decks.csv"));
    var scenarios = Scenarios.Load(Opt("scenarios", "scenarios.json"));
    if (options.TryGetValue("filter", out var filter))
        scenarios = scenarios.Where(s => s.Rules.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    var limit = int.Parse(Opt("limit", "0"));
    if (limit > 0)
        scenarios = scenarios.Take(limit).ToList();
    var games = int.Parse(Opt("games", "20"));
    var policies = Opt("policies", "baseline,surewin").Split(',');
    var opponents = Opt("opponents", "greedy:0.1").Split(',').Select(Opponents.Parse).ToList();
    var shard = Opt("shard", "0/1").Split('/').Select(int.Parse).ToArray();
    var threads = int.Parse(Opt("threads", Math.Max(1, Environment.ProcessorCount - 2).ToString()));
    var outPath = Opt("out", "results.jsonl");

    var done = new HashSet<string>();
    if (File.Exists(outPath))
        foreach (var line in File.ReadLines(outPath))
            if (JsonSerializer.Deserialize<ResultLine>(line) is { } r)
                done.Add(r.Key);

    // Work in a fixed order; this shard takes every n-th item.
    var work = (from s in Enumerable.Range(0, scenarios.Count)
                from g in Enumerable.Range(0, games)
                from o in opponents
                from p in policies
                select (Scenario: scenarios[s], Index: s, Game: g, Opponent: o, Policy: p))
        .Where((_, i) => i % shard[1] == shard[0])
        .Where(w => !done.Contains(ResultLine.MakeKey(w.Scenario.Key, w.Game, w.Opponent.Name, w.Policy)))
        .ToList();
    Console.WriteLine($"{work.Count} games to play on {threads} threads ({done.Count} already in {outPath})");

    using var writer = new StreamWriter(outPath, append: true);
    var gate = new object();
    var finished = 0;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = threads }, w =>
    {
        var npc = npcs.Get(w.Scenario.Npc)!;
        var seed = Game.Mix(StableHash(w.Scenario.Key), w.Game);
        GameResult result;
        try
        {
            result = Game.Play(w.Scenario, npc, catalog, Policy(w.Policy), w.Opponent, seed);
        }
        catch (Exception e)
        {
            result = new GameResult(0, 0, false, 0, 0, 0, 0, 0, 0, e.GetType().Name + ": " + e.Message);
        }

        var line = new ResultLine(w.Scenario.Key, w.Scenario.Name, w.Scenario.Rules, w.Scenario.Played, w.Game, seed, w.Policy, w.Opponent.Name,
            result.Outcome, result.BlueCards, result.BlueFirst, result.Decisions, Math.Round(result.MaxSeconds, 3), Math.Round(result.TotalSeconds, 3),
            result.SureNarrowed, result.Fallbacks, result.ExploitOut, result.Error);
        lock (gate)
        {
            writer.WriteLine(JsonSerializer.Serialize(line));
            writer.Flush();
            if (++finished % 50 == 0 || finished == work.Count)
                Console.WriteLine($"{finished}/{work.Count} ({clock.Elapsed:hh\\:mm\\:ss})");
        }
    });

    return 0;
}

// The policies under test. Each game runs on `inner` threads; the arena parallelises across games.
PolicyOptions Policy(string name)
{
    var exploit = TimeSpan.FromSeconds(double.Parse(Opt("exploit", "3"), System.Globalization.CultureInfo.InvariantCulture));
    // inner = solver threads per game. The solve budget keeps the live thread-seconds: the plugin
    // gets 40 s on `live` threads (14 on the 16-thread workstation), so one thread gets 14 x 40 s.
    var inner = int.Parse(Opt("inner", "1"));
    var live = int.Parse(Opt("live", "14"));
    var solve = TimeSpan.FromSeconds(new PolicyOptions().SolveBudget.TotalSeconds * live / inner);
    var baseOptions = new PolicyOptions { MaxParallelism = inner, SureWinParallelism = Math.Min(4, inner), ExploitBudget = exploit, SolveBudget = solve };
    return name switch
    {
        "baseline" => baseOptions with { SureWinBudget = TimeSpan.Zero },
        "surewin" => baseOptions,
        // Sure-win, and exploitation against the model fitted to logged NPC moves.
        "surewin-fit" => baseOptions with { Model = new SoftmaxGain(2) },
        _ => throw new ArgumentException($"unknown policy '{name}'"),
    };
}

static int StableHash(string text)
{
    unchecked
    {
        var hash = (int)2166136261;
        foreach (var ch in text)
            hash = (hash ^ ch) * 16777619;
        return hash & 0x7FFFFFFF;
    }
}

static string DataDir()
{
    // Next to the executable in a bundle, else research/data above it in the repository.
    var local = Path.Combine(AppContext.BaseDirectory, "data");
    if (File.Exists(Path.Combine(local, "cards.csv")))
        return local;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "research", "data", "cards.csv")))
            return Path.Combine(dir.FullName, "research", "data");
    throw new DirectoryNotFoundException("no data folder (cards.csv, npc-decks.csv); pass data=...");
}

internal sealed record ResultLine(
    string Scenario, string Npc, string Rules, int Played, int Game, int Seed, string Policy, string Opponent,
    int Outcome, int BlueCards, bool BlueFirst, int Decisions, double MaxSeconds, double TotalSeconds, int SureNarrowed, int Fallbacks, int ExploitOut, string? Error)
{
    public string Key => MakeKey(this.Scenario, this.Game, this.Opponent, this.Policy);

    public static string MakeKey(string scenario, int game, string opponent, string policy) => $"{scenario}#{game}#{opponent}#{policy}";
}
