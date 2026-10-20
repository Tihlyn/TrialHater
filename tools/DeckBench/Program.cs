using System.Diagnostics;
using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

// Usage: DeckBench <npc id>[,<npc id>...] [rules override, e.g. Plus+Reverse] [--deck id,id,id,id,id] [--owned owned_cards.txt]
//                  [--playouts N] [--bits tableBits] [--finalists N]
// Rules default to the NPC's sheet rules (regional rules are not in the sheet: pass them). Owned
// cards default to the whole catalogue; the plugin writes the real list to owned_cards.txt.
var data = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../research/data"));
var catalog = CardCatalog.FromCsv(Path.Combine(data, "cards.csv"));
var npcs = NpcCatalog.FromCsv(Path.Combine(data, "npc-decks.csv"));
var ids = args[0].Split(',').Select(uint.Parse).ToList();
var rulesArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !a.Contains(','));
var deckArg = Arg("--deck");
var ownedArg = Arg("--owned");
var defaults = new DeckSearchOptions();
var evaluation = defaults.Evaluation with
{
    Playouts = Arg("--playouts") is { } p ? int.Parse(p) : defaults.Evaluation.Playouts,
    TableBits = Arg("--bits") is { } b ? int.Parse(b) : defaults.Evaluation.TableBits,
};
var finalists = Arg("--finalists") is { } f ? int.Parse(f) : defaults.Finalists;
var owned = ownedArg is null
    ? catalog.All.ToList()
    : File.ReadLines(ownedArg).Select(l => catalog.Get(ushort.Parse(l.Trim()))).OfType<CardInfo>().ToList();
var saved = deckArg is null ? new List<IReadOnlyList<CardInfo>>() : [deckArg.Split(',').Select(s => catalog.Get(ushort.Parse(s))!).ToList()];

foreach (var id in ids)
{
    var npc = npcs.Get(id) ?? throw new ArgumentException($"no NPC {id}");
    var rules = rulesArg is null ? RuleSet.Of(npc.Rules) : RuleSet.Of(rulesArg.Split('+').Select(Enum.Parse<TriadRule>));
    var watch = Stopwatch.StartNew();
    var result = DeckOptimizer.Find(npc, rules, owned, saved, catalog, new DeckSearchOptions { Evaluation = evaluation, Finalists = finalists }, m => Console.WriteLine($"[{watch.Elapsed.TotalSeconds,6:F1}s] {m}"));
    Console.WriteLine($"== {result.Status} ({watch.Elapsed.TotalSeconds:F1} s)");
    foreach (var s in result.Saved)
        Console.WriteLine($"  saved   {Line(s)}");
    foreach (var s in result.Best)
        Console.WriteLine($"  best    {Line(s)}");
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Line(DeckScore s)
    => $"E={s.Expected:+0.00;-0.00} W/D/L {s.Win:P0}/{s.Draw:P0}/{s.Loss:P0} ({s.Games} games)"
       + (s.PerfectWin is { } pw ? $" perfect {pw:P0}/{s.PerfectDraw:P0}/{s.PerfectLoss:P0}" : "")
       + $"  {string.Join(", ", s.Cards.Select(c => $"{c.Id} {c.Stats} {c.Stars}*"))}";
