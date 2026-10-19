using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;
using Xunit;

namespace TrialHater.Tests;

public class TournamentTests
{
    // The TripleTriadTournament sheet as of 2026-10-19.
    private static readonly RuleSet[] Rows =
    [
        RuleSet.Of(TriadRule.Draft),
        RuleSet.Of(TriadRule.Draft, TriadRule.Same, TriadRule.ThreeOpen),
        RuleSet.Of(TriadRule.Draft),
        RuleSet.Of(TriadRule.Draft, TriadRule.Ascension, TriadRule.FallenAce),
        RuleSet.Of(TriadRule.Draft),
        RuleSet.Of(TriadRule.Draft, TriadRule.Plus, TriadRule.Same),
        RuleSet.Of(TriadRule.Draft),
        RuleSet.Of(TriadRule.Draft, TriadRule.Reverse, TriadRule.ThreeOpen),
        RuleSet.Of(TriadRule.Draft),
        RuleSet.Of(TriadRule.Draft, TriadRule.Plus, TriadRule.Swap),
    ];

    [Fact]
    public void MatchesTheInGameScheduleWindow()
    {
        // Opened at 22:57 UTC (00:57 local, UTC+2); the window listed 10/20 1:00 ... 19:00 local.
        var now = new DateTimeOffset(2026, 10, 19, 22, 57, 12, TimeSpan.Zero);
        var schedule = OpenTournament.Upcoming(Rows, now, 10).ToList();
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero), schedule[0].StartUtc);
        Assert.Equal([5, 6, 7, 8, 9, 10, 1, 2, 3, 4], schedule.Select(s => s.Row));
        Assert.Equal(RuleSet.Of(TriadRule.Draft, TriadRule.Plus, TriadRule.Same), schedule[1].Rules);       // 3:00 local
        Assert.Equal(RuleSet.Of(TriadRule.Draft, TriadRule.Reverse, TriadRule.ThreeOpen), schedule[3].Rules); // 7:00
        Assert.Equal(RuleSet.Of(TriadRule.Draft, TriadRule.Plus, TriadRule.Swap), schedule[5].Rules);       // 11:00
        Assert.Equal(RuleSet.Of(TriadRule.Draft, TriadRule.Same, TriadRule.ThreeOpen), schedule[7].Rules);  // 15:00
        Assert.Equal(RuleSet.Of(TriadRule.Draft, TriadRule.Ascension, TriadRule.FallenAce), schedule[9].Rules); // 19:00
    }

    [Fact]
    public void AStartExactlyNowIsListedFirst()
    {
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(start, OpenTournament.Upcoming(Rows, start, 1).Single().StartUtc);
    }
}
