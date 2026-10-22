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
    public void PicksTheChosenDeckRowOrWaitsForTheRanking()
    {
        var pick = SessionPlanner.Next(new SessionSnapshot { DeckSelectOpen = true, DeckRow = 1 }, Three, 0, sessionStarted: true);
        Assert.Equal(SessionActionKind.ConfirmDeck, pick.Kind);
        Assert.Equal(1, pick.Index);
        var fallback = SessionPlanner.Next(new SessionSnapshot { DeckSelectOpen = true }, Three, 0, sessionStarted: true);
        Assert.Equal(-1, fallback.Index);
        Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { DeckSelectOpen = true, DeckRow = 1, DeckPending = "ranking" }));
    }

    [Fact]
    public void WaitsForTheDeckToBeBuiltBeforeTalkingToTheNpc()
    {
        Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { CanInteract = true, Preparing = "building" }));
        // A match window that is already up still gets its action.
        Assert.Equal(SessionActionKind.Rematch, Next(new SessionSnapshot { ResultOpen = true, Preparing = "building" }, done: 1));
    }

    // 2026-10-22: Tsuzura was farmed with a deck built for last week's regional rule. A challenge
    // whose rules call for another deck is declined; the deck is built while nothing is on screen.
    [Fact]
    public void DeclinesAChallengeWhoseRulesNeedAnotherDeck()
    {
        var declined = SessionPlanner.Next(new SessionSnapshot { RequestOpen = true, RebuildDeck = "rules changed" }, Three, 1, sessionStarted: true);
        Assert.Equal(SessionActionKind.DeclineRequest, declined.Kind);
        Assert.Equal("rules changed", declined.Reason);
        Assert.Equal(SessionActionKind.AdvanceTalk, Next(new SessionSnapshot { TalkOpen = true, Preparing = "rebuilding" }, done: 1));
        Assert.Equal(SessionActionKind.Wait, Next(new SessionSnapshot { CanInteract = true, Preparing = "rebuilding" }, done: 1));
        Assert.Equal(SessionActionKind.Challenge, Next(new SessionSnapshot { RequestOpen = true }, done: 1));
    }

    [Fact]
    public void QuitsWhenTheGoalIsReached()
    {
        Assert.Equal(SessionActionKind.Quit, Next(new SessionSnapshot { ResultOpen = true }, done: 3));
        Assert.Equal(SessionActionKind.DeclineRequest, Next(new SessionSnapshot { RequestOpen = true }, done: 3));
        Assert.Equal(SessionActionKind.Done, Next(new SessionSnapshot { CanInteract = true }, done: 3));
        Assert.Equal(SessionActionKind.Quit, Next(new SessionSnapshot { ResultOpen = true }, done: 0, new SessionGoal(0, StopRequested: true)));
    }

    // A card session ignores the match count and quits once every reward card is in: at the
    // result window when the card is already in the bags, else at the rematch's challenge window.
    [Fact]
    public void CardSessionQuitsOnceEveryCardIsCollected()
    {
        var missing = new SessionGoal(0, false, CardsCollected: false);
        var collected = new SessionGoal(0, false, CardsCollected: true);
        Assert.Equal(SessionActionKind.Rematch, Next(new SessionSnapshot { ResultOpen = true }, done: 500, missing));
        var quit = SessionPlanner.Next(new SessionSnapshot { ResultOpen = true }, collected, 7, sessionStarted: true);
        Assert.Equal(SessionActionKind.Quit, quit.Kind);
        Assert.Equal("every reward card collected", quit.Reason);
        Assert.Equal(SessionActionKind.DeclineRequest, Next(new SessionSnapshot { RequestOpen = true }, done: 7, collected));
        Assert.Equal(SessionActionKind.Done, Next(new SessionSnapshot { CanInteract = true }, done: 7, collected));
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
