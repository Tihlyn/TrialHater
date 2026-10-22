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

    // 2026-10-22: leaving the duty cleared the target and the next sign-up waited for the player.
    [Fact]
    public void TargetsTheOfficialAgainAfterTheDuty()
    {
        Assert.Equal(TournamentActionKind.Retarget, Next(new TournamentSnapshot { OfficialInRange = true }));
        Assert.Equal(TournamentActionKind.Interact, Next(new TournamentSnapshot { OfficialInRange = true, CanInteract = true }));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { OfficialInRange = true, InDuty = true }));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { OfficialInRange = true, PlayerOccupied = true }));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { OfficialInRange = true }, TournamentPhase.Queued));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot()));
    }

    // 2026-10-22: past the sign-up window the queue talked to the Official over and over.
    [Fact]
    public void WaitsForTheSignUpWindow()
    {
        var opens = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { CanInteract = true, SignUpOpensAt = opens }));
        Assert.Equal(TournamentActionKind.Wait, Next(new TournamentSnapshot { OfficialInRange = true, SignUpOpensAt = opens }));
        // What is already on screen still finishes; the rewards close whatever the time.
        Assert.Equal(TournamentActionKind.AdvanceTalk, Next(new TournamentSnapshot { TalkOpen = true, SignUpOpensAt = opens }));
        Assert.Equal(TournamentActionKind.CloseReward, Next(new TournamentSnapshot { RewardOpen = true, SignUpOpensAt = opens }, TournamentPhase.Playing));
        Assert.Equal(TournamentActionKind.Commence, Next(new TournamentSnapshot { DutyReadyOpen = true, DutyIsTournament = true, SignUpOpensAt = opens }, TournamentPhase.Queued));
    }
}
