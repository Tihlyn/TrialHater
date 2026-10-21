using System.Text.Json;

namespace Arena;

internal static class Report
{
    public static void Print(List<string> files)
    {
        var rows = files.SelectMany(File.ReadLines).Select(l => JsonSerializer.Deserialize<ResultLine>(l)!).ToList();
        var errors = rows.Where(r => r.Error is not null).ToList();
        rows = rows.Where(r => r.Error is null).ToList();
        Console.WriteLine($"{rows.Count} games ({errors.Count} errors{(errors.Count > 0 ? ", first: " + errors[0].Error : "")})");

        Console.WriteLine();
        Console.WriteLine($"{"policy",-12} {"opponent",-14} {"games",6} {"win",7} {"draw",7} {"loss",7} {"E[w-l]",7} {"weighted",9} {"max s",7} {"narrowed",9} {"fallback",9} {"exploit out",12}");
        foreach (var g in rows.GroupBy(r => (r.Policy, r.Opponent)).OrderBy(g => g.Key.Opponent).ThenBy(g => g.Key.Policy))
        {
            // Weighted: each scenario counts as often as it was played in the recorded matches.
            var weight = g.Sum(r => (double)r.Played);
            var weighted = g.Sum(r => r.Played * (double)r.Outcome) / weight;
            Console.WriteLine($"{g.Key.Policy,-12} {g.Key.Opponent,-14} {g.Count(),6} {Rate(g, 1),7:P1} {Rate(g, 0),7:P1} {Rate(g, -1),7:P1} {g.Average(r => r.Outcome),7:+0.000;-0.000} {weighted,9:+0.000;-0.000} {g.Max(r => r.MaxSeconds),7:0.0} {g.Sum(r => r.SureNarrowed),9} {g.Sum(r => r.Fallbacks),9} {g.Sum(r => r.ExploitOut),12}");
        }

        // Deal for deal: the same scenario, game seed and opponent under two policies.
        var policies = rows.Select(r => r.Policy).Distinct().Order().ToList();
        for (var a = 0; a < policies.Count; a++)
        {
            for (var b = a + 1; b < policies.Count; b++)
            {
                Console.WriteLine();
                Console.WriteLine($"paired: {policies[b]} vs {policies[a]}");
                var pairs = rows.Where(r => r.Policy == policies[a])
                    .Join(rows.Where(r => r.Policy == policies[b]), r => (r.Scenario, r.Game, r.Opponent), r => (r.Scenario, r.Game, r.Opponent), (x, y) => (A: x, B: y))
                    .ToList();
                foreach (var g in pairs.GroupBy(p => p.A.Opponent).OrderBy(g => g.Key))
                    Paired($"  {g.Key}", g.ToList());
                foreach (var g in pairs.GroupBy(p => (p.A.Opponent, p.A.Rules)).OrderBy(g => g.Key.Opponent).ThenByDescending(g => g.Count()))
                    Paired($"    {g.Key.Opponent} [{g.Key.Rules}]", g.ToList());
            }
        }
    }

    private static void Paired(string label, List<(ResultLine A, ResultLine B)> pairs)
    {
        var better = pairs.Count(p => p.B.Outcome > p.A.Outcome);
        var worse = pairs.Count(p => p.B.Outcome < p.A.Outcome);
        var delta = pairs.Average(p => p.B.Outcome - p.A.Outcome);
        Console.WriteLine($"{label,-48} {pairs.Count,6} deals: better {better,4}, worse {worse,4}, mean {delta:+0.000;-0.000;0.000}, sign test p={SignTest(better, worse):0.###}");
    }

    private static double Rate(IEnumerable<ResultLine> rows, int outcome)
    {
        var list = rows.ToList();
        return list.Count(r => r.Outcome == outcome) / (double)list.Count;
    }

    // Two-sided exact sign test on the deals where the policies differ.
    private static double SignTest(int better, int worse)
    {
        var n = better + worse;
        if (n == 0)
            return 1;
        var k = Math.Min(better, worse);
        var p = 0.0;
        for (var i = 0; i <= k; i++)
            p += Math.Exp(LogChoose(n, i) - (n * Math.Log(2)));
        return Math.Min(1, 2 * p);
    }

    private static double LogChoose(int n, int k)
    {
        var sum = 0.0;
        for (var i = 1; i <= k; i++)
            sum += Math.Log(n - k + i) - Math.Log(i);
        return sum;
    }
}
