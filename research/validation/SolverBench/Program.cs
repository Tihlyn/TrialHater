using System.Diagnostics;
using System.Text.Json;
using AutoTripleTriadGrind.Core.Triad.Data;
using AutoTripleTriadGrind.Core.Triad.Logic;
using AutoTripleTriadGrind.Core.Triad.Logic.Rules;
using SolverBench;

// Usage: SolverBench <cards.csv> [diffGames] [solveDecks] [regretGames]
var csvPath = args.Length > 0 ? args[0] : "../../data/cards.csv";
var diffGames = args.Length > 1 ? int.Parse(args[1]) : 20000;
var solveDecks = args.Length > 2 ? int.Parse(args[2]) : 60;
var regretGames = args.Length > 3 ? int.Parse(args[3]) : 120;

var cards = LoadCards(csvPath);
var table = new TriadCard[cards.Max(c => c.Id) + 1];
foreach (var c in cards) table[c.Id] = c;
TriadCards.Use(table);
Console.Error.WriteLine($"Loaded {cards.Count} cards");

var ruleSets = new (string name, bool same, bool plus)[] { ("Plain", false, false), ("Same", true, false), ("Plus", false, true), ("Same+Plus", true, true) };
var output = new Dictionary<string, object>();

// ---- 4 (separate run): safe-then-exploit policy vs the same game seeds as experiment 3 ----
if (args.Length > 4 && args[4] == "exploit")
{
    var minPlaced = args.Length > 5 ? int.Parse(args[5]) : 0;
    var results = new Dictionary<string, object>();
    foreach (var rs in new[] { ruleSets[0], ruleSets[3] })
    {
        var rng = new Random(3);
        var tally = new Dictionary<string, int[]>();
        var exploitMs = new List<double>();
        for (var g = 0; g < regretGames; g++)
        {
            var (blue, red) = (StrongDeck(cards, rng), StrongDeck(cards, rng));
            var blueStarts = g % 2 == 0;
            var seed = rng.Next();
            foreach (var redPolicy in new[] { "greedy", "noisy" })
            {
                foreach (var bluePolicy in new[] { "attg", "exact", "safeExploit" })
                {
                    var (eng, sim, state) = NewPair(blue, red, blueStarts, rs.same, rs.plus);
                    var solver = TriadSolver.CreateLive();
                    solver.InitializeSimulation(Rules(rs.same, rs.plus));
                    var redRng = new Random(seed);
                    while (eng.Placed < 9)
                    {
                        int hb, cell;
                        if (eng.BlueToMove)
                        {
                            if (bluePolicy == "attg") solver.FindNextMove(new TriadGameState(state), out hb, out cell, out _);
                            else if (bluePolicy == "exact" || eng.Placed < minPlaced) (hb, cell, _) = Fresh(eng, blue, red, blueStarts, rs).BestMove();
                            else
                            {
                                var sw = Stopwatch.StartNew();
                                (hb, cell) = new SafeExploit(Fresh(eng, blue, red, blueStarts, rs)).Choose();
                                exploitMs.Add(sw.Elapsed.TotalMilliseconds);
                            }
                        }
                        else if (redPolicy == "greedy")
                        {
                            var gm = SafeExploit.GreedyMoves(eng);
                            (hb, cell) = gm[redRng.Next(gm.Count)];
                        }
                        else if (redRng.NextDouble() < 0.5)
                        {
                            (hb, cell) = RandomMove(eng, redRng);
                        }
                        else
                        {
                            (hb, cell, _) = Fresh(eng, blue, red, blueStarts, rs).BestMove();
                        }
                        var owner = eng.BlueToMove ? TriadOwner.Blue : TriadOwner.Red;
                        sim.PlaceCard(state, hb, state.TurnDeck, owner, cell);
                        eng.Play(hb, cell);
                    }
                    var b = eng.BlueScore();
                    var key = $"{bluePolicy}_vs_{redPolicy}";
                    if (!tally.ContainsKey(key)) tally[key] = new int[3];
                    tally[key][b > 5 ? 2 : b == 5 ? 1 : 0]++;
                }
            }
            if (g % 10 == 9) Console.Error.WriteLine($"exploit {rs.name}: {g + 1}/{regretGames} {JsonSerializer.Serialize(tally)}");
        }
        results[rs.name] = new
        {
            games = regretGames,
            note = "noisy red = 50% uniform-random move, 50% perfect move; safeExploit's model is greedy, so it is mis-specified vs noisy",
            exploitMinPlaced = minPlaced,
            exploitMoveMs = exploitMs.Count > 0 ? Stats(exploitMs) : null,
            wdl = tally.ToDictionary(kv => kv.Key, kv => new { loss = kv.Value[0], draw = kv.Value[1], win = kv.Value[2] }),
        };
        Console.Error.WriteLine($"exploit {rs.name}: {JsonSerializer.Serialize(results[rs.name])}");
    }
    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

// ---- 1. Differential test: independent engine vs upstream simulation, random play ----
{
    var rng = new Random(1);
    var results = new Dictionary<string, object>();
    foreach (var rs in ruleSets)
    {
        var mismatches = 0; var moves = 0; var outcomeMismatch = 0;
        for (var g = 0; g < diffGames; g++)
        {
            var (blue, red) = (RandomDeck(cards, rng), RandomDeck(cards, rng));
            var blueStarts = rng.Next(2) == 0;
            var (eng, sim, state) = NewPair(blue, red, blueStarts, rs.same, rs.plus);
            while (eng.Placed < 9)
            {
                var hand = eng.BlueToMove ? eng.BlueHand : eng.RedHand;
                var hbs = Enumerable.Range(0, 5).Where(b => (hand & (1 << b)) != 0).ToArray();
                var cellsFree = Enumerable.Range(0, 9).Where(c => eng.Cell[c] == ExactEngine.Empty).ToArray();
                var hb = hbs[rng.Next(hbs.Length)]; var cell = cellsFree[rng.Next(cellsFree.Length)];
                var owner = eng.BlueToMove ? TriadOwner.Blue : TriadOwner.Red;
                sim.PlaceCard(state, hb, state.TurnDeck, owner, cell);
                eng.Play(hb, cell);
                moves++;
                if (!SameOwners(eng, state)) { mismatches++; break; }
            }
            if (eng.Placed == 9)
            {
                var b = eng.BlueScore();
                var mine = b > 5 ? TriadGameStatus.BlueWins : b == 5 ? TriadGameStatus.BlueDraw : TriadGameStatus.BlueLost;
                if (mine != state.Status) outcomeMismatch++;
            }
        }
        results[rs.name] = new { games = diffGames, moves, ownershipMismatchGames = mismatches, outcomeMismatchGames = outcomeMismatch };
        Console.Error.WriteLine($"diff {rs.name}: mismatches={mismatches} outcomeMismatch={outcomeMismatch}");
    }
    output["differential"] = results;
}

// ---- 2. Exact solve cost from the opening and after the first move ----
{
    var rng = new Random(2);
    var results = new Dictionary<string, object>();
    foreach (var rs in ruleSets)
    {
        var bestTimes = new List<double>(); var allTimes = new List<double>(); var nodes = new List<long>();
        var secondTimes = new List<double>();
        var values = new int[3];
        for (var d = 0; d < solveDecks; d++)
        {
            var (blue, red) = (StrongDeck(cards, rng), StrongDeck(cards, rng));
            var eng = new ExactEngine(Sides(blue), Sides(red), true, rs.same, rs.plus);
            var sw = Stopwatch.StartNew();
            var best = eng.BestMove();
            bestTimes.Add(sw.Elapsed.TotalMilliseconds);
            nodes.Add(eng.Nodes);
            values[best.value + 1]++;

            var eng2 = new ExactEngine(Sides(blue), Sides(red), true, rs.same, rs.plus);
            sw.Restart();
            eng2.ScoreAllMoves();
            allTimes.Add(sw.Elapsed.TotalMilliseconds);

            // Second player's first decision (8 cells x 5 cards) on a fresh table.
            var eng3 = new ExactEngine(Sides(blue), Sides(red), true, rs.same, rs.plus);
            eng3.Play(best.hand, best.cell);
            sw.Restart();
            eng3.BestMove();
            secondTimes.Add(sw.Elapsed.TotalMilliseconds);
        }
        results[rs.name] = new
        {
            decks = solveDecks,
            openingBestMoveMs = Stats(bestTimes),
            openingAll45MovesMs = Stats(allTimes),
            secondPlayerFirstMoveMs = Stats(secondTimes),
            openingNodesMedian = nodes.OrderBy(x => x).ElementAt(nodes.Count / 2),
            firstPlayerPerfectPlay = new { loss = values[0], draw = values[1], win = values[2] },
        };
        Console.Error.WriteLine($"solve {rs.name}: {JsonSerializer.Serialize(results[rs.name])}");
    }
    output["exactSolveCost"] = results;
}

// ---- 3. Upstream live agent move quality under FULL information (its best case) ----
{
    var rng = new Random(3);
    var results = new Dictionary<string, object>();
    foreach (var rs in new[] { ruleSets[0], ruleSets[3] })
    {
        int decisions = 0, suboptimal = 0, winToDrawOrLoss = 0, drawToLoss = 0;
        int[] attgVsPerfect = new int[3], exactVsPerfect = new int[3], attgVsGreedy = new int[3], exactVsGreedy = new int[3];
        var attgMs = new List<double>();
        for (var g = 0; g < regretGames; g++)
        {
            var (blue, red) = (StrongDeck(cards, rng), StrongDeck(cards, rng));
            var blueStarts = g % 2 == 0;
            var seed = rng.Next();
            foreach (var redPolicy in new[] { "perfect", "greedy" })
            {
                foreach (var bluePolicy in new[] { "attg", "exact" })
                {
                    var (eng, sim, state) = NewPair(blue, red, blueStarts, rs.same, rs.plus);
                    var solver = TriadSolver.CreateLive();
                    solver.InitializeSimulation(Rules(rs.same, rs.plus));
                    var greedyRng = new Random(seed);
                    while (eng.Placed < 9)
                    {
                        int hb, cell;
                        if (eng.BlueToMove)
                        {
                            if (bluePolicy == "attg")
                            {
                                var copy = new TriadGameState(state);
                                var sw = Stopwatch.StartNew();
                                solver.FindNextMove(copy, out hb, out cell, out _);
                                attgMs.Add(sw.Elapsed.TotalMilliseconds);
                                if (redPolicy == "perfect")
                                {
                                    var scored = Fresh(eng, blue, red, blueStarts, rs).ScoreAllMoves();
                                    var bestV = scored.Max(m => m.value);
                                    var chosen = scored.First(m => m.hand == hb && m.cell == cell).value;
                                    decisions++;
                                    if (chosen < bestV) suboptimal++;
                                    if (bestV == 1 && chosen < 1) winToDrawOrLoss++;
                                    if (bestV == 0 && chosen < 0) drawToLoss++;
                                }
                            }
                            else
                            {
                                (hb, cell, _) = Fresh(eng, blue, red, blueStarts, rs).BestMove();
                            }
                        }
                        else if (redPolicy == "perfect")
                        {
                            (hb, cell, _) = Fresh(eng, blue, red, blueStarts, rs).BestMove();
                        }
                        else
                        {
                            (hb, cell) = Greedy(eng, greedyRng);
                        }
                        var owner = eng.BlueToMove ? TriadOwner.Blue : TriadOwner.Red;
                        sim.PlaceCard(state, hb, state.TurnDeck, owner, cell);
                        eng.Play(hb, cell);
                    }
                    var b = eng.BlueScore();
                    var idx = b > 5 ? 2 : b == 5 ? 1 : 0;
                    var bucket = (bluePolicy, redPolicy) switch
                    {
                        ("attg", "perfect") => attgVsPerfect,
                        ("exact", "perfect") => exactVsPerfect,
                        ("attg", _) => attgVsGreedy,
                        _ => exactVsGreedy,
                    };
                    bucket[idx]++;
                }
            }
            if (g % 10 == 9) Console.Error.WriteLine($"regret {rs.name}: {g + 1}/{regretGames} subopt {suboptimal}/{decisions}");
        }
        static object Wdl(int[] a) => new { loss = a[0], draw = a[1], win = a[2] };
        results[rs.name] = new
        {
            games = regretGames,
            attgDecisionsScored = decisions,
            attgSuboptimalDecisions = suboptimal,
            attgThrewWinningPosition = winToDrawOrLoss,
            attgThrewDrawnPosition = drawToLoss,
            attgMoveMs = Stats(attgMs),
            blueAttgVsPerfectRed = Wdl(attgVsPerfect),
            blueExactVsPerfectRed = Wdl(exactVsPerfect),
            blueAttgVsGreedyRed = Wdl(attgVsGreedy),
            blueExactVsGreedyRed = Wdl(exactVsGreedy),
        };
        Console.Error.WriteLine($"regret {rs.name}: {JsonSerializer.Serialize(results[rs.name])}");
    }
    output["upstreamAgentRegret"] = results;
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    note = "Full-information benchmark. Upstream AutoTripleTriadGrind logic compiled unmodified; independent exact engine differential-tested against it.",
    machine = new { Environment.ProcessorCount, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() },
    results = output,
}, new JsonSerializerOptions { WriteIndented = true }));

// ---------------- helpers ----------------

static List<TriadCard> LoadCards(string path)
{
    var list = new List<TriadCard>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var f = ParseCsv(line);
        var type = f[4] switch { "Primal" => TriadCardType.Primal, "Scion" => TriadCardType.Scion, "Society" => TriadCardType.Beastman, "Garlean" => TriadCardType.Garlean, _ => TriadCardType.None };
        var id = ushort.Parse(f[0]);
        list.Add(new TriadCard(id, byte.Parse(f[5]), byte.Parse(f[6]), byte.Parse(f[7]), byte.Parse(f[8]), type, byte.Parse(f[3]), id, 0));
    }
    return list;
}

static List<string> ParseCsv(string line)
{
    var res = new List<string>(); var sb = new System.Text.StringBuilder(); var q = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (q) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') q = false; else sb.Append(ch); }
        else if (ch == '"') q = true;
        else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
        else sb.Append(ch);
    }
    res.Add(sb.ToString());
    return res;
}

static ushort[] RandomDeck(List<TriadCard> cards, Random rng)
{
    while (true)
    {
        var d = cards.OrderBy(_ => rng.Next()).Take(5).ToArray();
        if (d.Count(c => c.Stars == 5) <= 1 && d.Count(c => c.Stars >= 4) <= 2) return d.Select(c => c.Id).ToArray();
    }
}

// A typical competitive composition: one 5-star, one 4-star, three 3-star.
static ushort[] StrongDeck(List<TriadCard> cards, Random rng)
{
    ushort Pick(int stars, HashSet<ushort> used) { var pool = cards.Where(c => c.Stars == stars && !used.Contains(c.Id)).ToList(); var id = pool[rng.Next(pool.Count)].Id; used.Add(id); return id; }
    var used = new HashSet<ushort>();
    return [Pick(5, used), Pick(4, used), Pick(3, used), Pick(3, used), Pick(3, used)];
}

static byte[][] Sides(ushort[] deck) => deck.Select(id => { var c = TriadCards.Get(id); return new[] { c.Up, c.Right, c.Down, c.Left }; }).ToArray();

static List<TriadRule> Rules(bool same, bool plus)
{
    var r = new List<TriadRule>();
    if (same) r.Add(TriadRules.Create(TriadRuleId.Same));
    if (plus) r.Add(TriadRules.Create(TriadRuleId.Plus));
    return r;
}

static (ExactEngine, TriadSimulation, TriadGameState) NewPair(ushort[] blue, ushort[] red, bool blueStarts, bool same, bool plus)
{
    var eng = new ExactEngine(Sides(blue), Sides(red), blueStarts, same, plus, ttBits: 10);
    var sim = new TriadSimulation();
    sim.Initialize(Rules(same, plus));
    var state = sim.StartGame(new TriadDeck(blue), new TriadDeck(red), blueStarts ? TriadGameStatus.InProgressBlue : TriadGameStatus.InProgressRed);
    return (eng, sim, state);
}

// Replays the current position into a fresh engine with a large table.
static ExactEngine Fresh(ExactEngine from, ushort[] blue, ushort[] red, bool blueStarts, (string name, bool same, bool plus) rs)
{
    var e = new ExactEngine(Sides(blue), Sides(red), blueStarts, rs.same, rs.plus, ttBits: 20);
    for (var c = 0; c < 9; c++) e.Cell[c] = from.Cell[c];
    e.OwnerMask = from.OwnerMask; e.BlueHand = from.BlueHand; e.RedHand = from.RedHand; e.Placed = from.Placed;
    e.RebuildKey();
    return e;
}

static bool SameOwners(ExactEngine eng, TriadGameState state)
{
    for (var c = 0; c < 9; c++)
    {
        var empty = eng.Cell[c] == ExactEngine.Empty;
        if (empty != state.Board[c].IsEmpty) return false;
        if (empty) continue;
        var blue = (eng.OwnerMask & (1 << c)) != 0;
        if (blue != (state.Board[c].Owner == TriadOwner.Blue)) return false;
    }
    return true;
}

// Proxy NPC: maximise immediate own-card count, random tie-break. A model, not measured NPC behaviour.
static (int, int) Greedy(ExactEngine eng, Random rng)
{
    var hand = eng.BlueToMove ? eng.BlueHand : eng.RedHand;
    var best = new List<(int, int)>(); var bestScore = int.MinValue;
    for (var hb = 0; hb < 5; hb++)
    {
        if ((hand & (1 << hb)) == 0) continue;
        for (var c = 0; c < 9; c++)
        {
            if (eng.Cell[c] != ExactEngine.Empty) continue;
            var u = eng.Play(hb, c);
            var s = 9 - System.Numerics.BitOperations.PopCount((uint)eng.OwnerMask) - (9 - eng.Placed); // red-owned cells
            eng.Undo(hb, c, u);
            if (s > bestScore) { bestScore = s; best.Clear(); }
            if (s == bestScore) best.Add((hb, c));
        }
    }
    return best[rng.Next(best.Count)];
}

static (int, int) RandomMove(ExactEngine eng, Random rng)
{
    var hand = eng.BlueToMove ? eng.BlueHand : eng.RedHand;
    var hbs = Enumerable.Range(0, 5).Where(b => (hand & (1 << b)) != 0).ToArray();
    var cellsFree = Enumerable.Range(0, 9).Where(c => eng.Cell[c] == ExactEngine.Empty).ToArray();
    return (hbs[rng.Next(hbs.Length)], cellsFree[rng.Next(cellsFree.Length)]);
}

static object Stats(List<double> xs)
{
    var s = xs.OrderBy(x => x).ToList();
    double P(double p) => s[Math.Min(s.Count - 1, (int)(p * s.Count))];
    return new { n = s.Count, median = Math.Round(P(0.5), 2), p90 = Math.Round(P(0.9), 2), max = Math.Round(s[^1], 2), mean = Math.Round(s.Average(), 2) };
}
