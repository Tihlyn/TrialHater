using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;
using TrialHater.Core.Search;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class SolveCacheTests
{
    private const uint PridefulStag = 2293821;

    // Our opening against Prideful Stag (Plus): every NPC card hidden, 6 possible hands.
    private static MatchView Opening(int forced = -1, RuleSet? rules = null)
    {
        HandSlot[] blue = [.. new[] { "5468", "7528", "8744", "7479", "7A91" }.Select(c => HandSlot.Of(C(c)))];
        HandSlot[] red = [HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden, HandSlot.Hidden];
        return new MatchView(rules ?? RuleSet.Of(TriadRule.Plus), EmptyBoard(), blue, red, Side.Blue, forced, PridefulStag);
    }

    [Fact]
    public void ASecondOpeningReusesTheValuesAndDecidesTheSame()
    {
        var npc = Npcs.Get(PridefulStag)!;
        var cache = new SolveCache();
        var options = new PolicyOptions { Cache = cache, MaxParallelism = 4 };
        var first = MovePolicy.Decide(Opening(), npc, Catalog, options);
        Assert.Equal(first.Hypotheses, cache.Count);
        Assert.DoesNotContain("solved before", first.Reason);

        var second = MovePolicy.Decide(Opening(), npc, Catalog, options);
        Assert.Contains($"{first.Hypotheses} of {first.Hypotheses} hand(s) solved before", second.Reason);
        Assert.Equal(first.Move, second.Move);
        Assert.Equal(first.Moves, second.Moves);
        Assert.True(second.Nodes < first.Nodes);

        var uncached = MovePolicy.Decide(Opening(), npc, Catalog, options with { Cache = null });
        Assert.Equal(uncached.Moves, second.Moves);
    }

    [Fact]
    public void TheFileKeepsValuesExactlyAndDropsOtherVersions()
    {
        // Chaos values are fractions of 14400: a forced draw makes them averages.
        var position = Pos(RuleSet.Of(TriadRule.Chaos), Board(R("6656")), Hand("5468", "7528", "8744", "7479", "7A91"), Hand(null, "7345", "2277", "7553", "3721")) with { ForcedSlot = 2 };
        var values = new Solver(position).ScoreRootMoves();
        var cache = new SolveCache();
        cache.Put(position, values);

        Assert.True(SolveCache.FromJson(cache.ToJson()).TryGet(position, out var loaded));
        Assert.Equal(values, loaded);
        Assert.Contains(values, v => v.Value * Solver.Scale % 1 == 0 && Math.Abs(v.Value) is > 0 and < 1);

        // The NPC's slot order does not change our moves (no Order): the same position.
        var shuffled = Pos(RuleSet.Of(TriadRule.Chaos), Board(R("6656")), Hand("5468", "7528", "8744", "7479", "7A91"), Hand("3721", null, "7553", "2277", "7345")) with { ForcedSlot = 2 };
        Assert.True(cache.TryGet(shuffled, out var same));
        Assert.Equal(values, same);
        Assert.False(cache.TryGet(position with { ForcedSlot = 1 }, out _));
        Assert.Equal(0, SolveCache.FromJson(cache.ToJson().Replace($"\"Version\":{SolveCache.Version}", "\"Version\":0")).Count);
    }

    [Fact]
    public void LaterPositionsAreNotKept()
    {
        var position = Pos(RuleSet.Of(TriadRule.Plus), Board(B("5468"), R("6656")), Hand(null, null, "8744", "7479", "7A91"), Hand(null, "7345", "2277", "7553", "3721"));
        var cache = new SolveCache();
        cache.Put(position, new Solver(position).ScoreRootMoves());
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(position, out _));
    }
}
