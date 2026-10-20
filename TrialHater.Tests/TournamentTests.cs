using TrialHater.Core.Rules;
using static TrialHater.Tests.Fixtures;
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

    [Fact]
    public void LatestStartIsTheSlotNowFallsIn()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero), OpenTournament.LatestStart(new DateTimeOffset(2026, 10, 19, 23, 31, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), OpenTournament.LatestStart(new DateTimeOffset(2026, 10, 19, 22, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero), OpenTournament.LatestStart(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void OpponentDecksMergeWithinATournamentAndResetAfterIt()
    {
        var decks = new TournamentDecks();
        var t = new DateTimeOffset(2026, 10, 19, 23, 10, 0, TimeSpan.Zero);
        decks.Record("Fox Dragus", [C("1111"), C("2222"), C("3333")], t);
        // Second match: 2222 again (the same card, not a second copy) and two new ones.
        var known = decks.Record("fox dragus", [C("2222"), C("4444"), C("5555")], t.AddMinutes(15));
        Assert.Equal([C("1111"), C("2222"), C("3333"), C("4444"), C("5555")], known);
        Assert.Empty(decks.Get("Someone Else", t));
        Assert.Empty(decks.Get("Fox Dragus", t.AddHours(2)));
    }

    [Fact]
    public void ADeckThatContradictsTheKnownOneReplacesIt()
    {
        // Jonas, 2026-10-20: two tournaments in one slot, a new draft in the second.
        var decks = new TournamentDecks();
        var t = new DateTimeOffset(2026, 10, 20, 17, 5, 0, TimeSpan.Zero);
        decks.Record("Jonas", [C("3771"), C("5827"), C("9258"), C("866A"), C("6115")], t);
        var known = decks.Record("Jonas", [C("1111"), C("2222")], t.AddMinutes(15));
        Assert.Equal([C("1111"), C("2222")], known);
        decks.Clear();
        Assert.Empty(decks.Get("Jonas", t.AddMinutes(16)));
    }
}
