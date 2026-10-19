using TrialHater.Core.Session;
using Xunit;

namespace TrialHater.Tests;

public class SessionPlannerTests
{
    private static readonly SessionGoal Three = new(3, false);

    private static SessionActionKind Next(SessionSnapshot s, int done = 0, SessionGoal? goal = null)
        => SessionPlanner.Next(s, goal ?? Three, done, sessionStarted: true).Kind;

    [Fact]
    public void FollowsTheCapturedFlow()
    {
        Assert.Equal(SessionActionKind.Interact, Next(new SessionSnapshot { CanInteract = true }));
        Assert.Equal(SessionActionKind.ChooseMenu, Next(new SessionSnapshot { MenuOpen = true, MenuTriadIndex = 0 }));
        Assert.Equal(SessionActionKind.AdvanceTalk, Next(new SessionSnapshot { TalkOpen = true }));
        Assert.Equal(SessionActionKind.Challenge, Next(new SessionSnapshot { RequestOpen = true }));
        Assert.Equal(SessionActionKind.ConfirmDeck, Next(new SessionSnapshot { DeckSelectOpen = true }));
        Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { BoardOpen = true }));
        Assert.Equal(SessionActionKind.Rematch, Next(new SessionSnapshot { ResultOpen = true }, done: 1));
    }

    [Fact]
    public void QuitsWhenTheGoalIsReached()
    {
        Assert.Equal(SessionActionKind.Quit, Next(new SessionSnapshot { ResultOpen = true }, done: 3));
        Assert.Equal(SessionActionKind.DeclineRequest, Next(new SessionSnapshot { RequestOpen = true }, done: 3));
        Assert.Equal(SessionActionKind.Done, Next(new SessionSnapshot { CanInteract = true }, done: 3));
        Assert.Equal(SessionActionKind.Quit, Next(new SessionSnapshot { ResultOpen = true }, done: 0, new SessionGoal(0, StopRequested: true)));
    }

    // 2026-10-19: the last result window opened before its buttons were live and the session
    // ended on the spot, leaving the window to the player.
    [Fact]
    public void DoesNotEndWhileTheResultWindowIsStillAppearing()
    {
        var appearing = new SessionSnapshot { MatchWindowVisible = true };
        Assert.Equal(SessionActionKind.Wait, Next(appearing, done: 3));
        Assert.Equal(SessionActionKind.Quit, Next(appearing with { ResultOpen = true }, done: 3));
        Assert.Equal(SessionActionKind.Done, Next(new SessionSnapshot(), done: 3));
    }

    [Fact]
    public void UnlimitedGoalKeepsGoing()
        => Assert.Equal(SessionActionKind.Rematch, Next(new SessionSnapshot { ResultOpen = true }, done: 500, new SessionGoal(0, false)));

    [Fact]
    public void NeverAnswersAYesNoDialog()
    {
        var s = new SessionSnapshot { YesNoOpen = true, ResultOpen = true, RequestOpen = true };
        Assert.Equal(SessionActionKind.Wait, Next(s));
    }

    [Fact]
    public void AMenuWithoutTriadIsLeftAlone()
        => Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { MenuOpen = true, MenuTriadIndex = -1 }));

    [Fact]
    public void ABusyPlayerIsNotSentToTheNpc()
        => Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { CanInteract = true, PlayerOccupied = true }));
}
