using TrialHater.Core.Tournament;
using Xunit;

namespace TrialHater.Tests;

public class TournamentPlannerTests
{
    private static TournamentActionKind Next(TournamentSnapshot s, TournamentPhase phase = TournamentPhase.Registering)
        => TournamentPlanner.Next(s, phase).Kind;

    [Fact]
    public void FollowsTheCapturedSignUp()
    {
        Assert.Equal(TournamentActionKind.Interact, Next(new TournamentSnapshot { CanInteract = true }));
        Assert.Equal(TournamentActionKind.AdvanceTalk, Next(new TournamentSnapshot { TalkOpen = true }));
        var menu = TournamentPlanner.Next(new TournamentSnapshot { MenuOpen = true, SignUpIndex = 0 }, TournamentPhase.Registering);
        Assert.Equal(TournamentActionKind.ChooseSignUp, menu.Kind);
        Assert.Equal(0, menu.Index);
        Assert.Equal(TournamentActionKind.ConfirmSignUp, Next(new TournamentSnapshot { YesNoOpen = true, YesNoIsSignUp = true }));
    }

    [Fact]
    public void CommencesOnlyTheTournamentItQueuedFor()
    {
        Assert.Equal(TournamentActionKind.Commence, Next(new TournamentSnapshot { DutyReadyOpen = true, DutyIsTournament = true }, TournamentPhase.Queued));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { DutyReadyOpen = true, DutyIsTournament = false }, TournamentPhase.Queued));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { DutyReadyOpen = true, DutyIsTournament = true }, TournamentPhase.Registering));
    }

    [Fact]
    public void LeavesOtherDialogsAlone()
    {
        // "Ready to begin match?" closes itself; a sign-up question is only answered while signing up.
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { YesNoOpen = true }, TournamentPhase.Playing));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { YesNoOpen = true, YesNoIsSignUp = true }, TournamentPhase.Queued));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { MenuOpen = true }));
    }

    [Fact]
    public void ClosesTheResultsThenTheRewards()
    {
        Assert.Equal(TournamentActionKind.CloseResult, Next(new TournamentSnapshot { ResultOpen = true }, TournamentPhase.Playing));
        Assert.Equal(TournamentActionKind.CloseReward, Next(new TournamentSnapshot { RewardOpen = true }, TournamentPhase.Playing));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { CanInteract = true, InDuty = true }, TournamentPhase.Playing));
    }
}
