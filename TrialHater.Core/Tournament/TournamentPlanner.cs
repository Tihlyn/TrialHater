namespace TrialHater.Core.Tournament;

// What the tournament runner can see, read from the game by the plugin. Window flags mean "visible
// and ready for input".
public sealed record TournamentSnapshot
{
    // The duty pop (ContentsFinderConfirm) and whether it is for the Open Tournament.
    public bool DutyReadyOpen { get; init; }

    public bool DutyIsTournament { get; init; }

    public bool YesNoOpen { get; init; }

    // The Yes/No asks to sign up for the tournament (not "Ready to begin match?").
    public bool YesNoIsSignUp { get; init; }

    // The Official's menu (SelectString) and the index of its sign-up entry, -1 when absent.
    public bool MenuOpen { get; init; }

    public int SignUpIndex { get; init; } = -1;

    public bool TalkOpen { get; init; }

    public bool ResultOpen { get; init; }

    public bool RewardOpen { get; init; }

    public bool InDuty { get; init; }

    // The Official is targeted and within interaction range, and the player is free to act.
    public bool CanInteract { get; init; }

    // The Official is within interaction range but not targeted: leaving the tournament duty
    // clears the target and puts the player back in front of the Official.
    public bool OfficialInRange { get; init; }

    public bool PlayerOccupied { get; init; }

    // When sign-up opens next, null while it is open (OpenTournament.SignUpOpensAt).
    public DateTimeOffset? SignUpOpensAt { get; init; }
}

public enum TournamentPhase
{
    Registering,
    Queued,
    Playing,
}

public enum TournamentActionKind
{
    Wait,
    Retarget,
    Interact,
    AdvanceTalk,
    ChooseSignUp,
    ConfirmSignUp,
    Commence,
    CloseResult,
    CloseReward,
}

public sealed record TournamentAction(TournamentActionKind Kind, string Reason, int Index = -1)
{
    public static TournamentAction Wait(string reason) => new(TournamentActionKind.Wait, reason);
}

// The next step of queueing for and finishing an Open Tournament (docs/MODEL_MAPPING.md, session
// 17): talk to the Official, "Sign up for an Open tournament.", Yes; wait for the duty pop and
// commence; the draft and matches are auto play's; at the end close the results and the rewards.
// Windows the flow does not own (another duty's pop, "Ready to begin match?", which closes itself)
// are left alone.
public static class TournamentPlanner
{
    public static TournamentAction Next(TournamentSnapshot s, TournamentPhase phase)
    {
        if (s.RewardOpen)
            return new TournamentAction(TournamentActionKind.CloseReward, "tournament rewards");
        if (s.ResultOpen)
            return new TournamentAction(TournamentActionKind.CloseResult, "tournament results");
        if (s.DutyReadyOpen)
        {
            return s.DutyIsTournament && phase == TournamentPhase.Queued
                ? new TournamentAction(TournamentActionKind.Commence, "the tournament is ready")
                : TournamentAction.Wait("a duty is ready that this plugin did not queue for; not touching it");
        }

        if (s.YesNoOpen)
        {
            return s.YesNoIsSignUp && phase == TournamentPhase.Registering
                ? new TournamentAction(TournamentActionKind.ConfirmSignUp, "sign up")
                : TournamentAction.Wait("a Yes/No dialog is open");
        }

        switch (phase)
        {
            case TournamentPhase.Queued:
                return TournamentAction.Wait("queued; waiting for the tournament to start");
            case TournamentPhase.Playing:
                return TournamentAction.Wait("tournament in progress");
        }

        if (s.MenuOpen)
        {
            return s.SignUpIndex >= 0
                ? new TournamentAction(TournamentActionKind.ChooseSignUp, "Sign up for an Open tournament", s.SignUpIndex)
                : TournamentAction.Wait("the Official's menu has no sign-up entry (no tournament open to sign up for?)");
        }

        if (s.TalkOpen)
            return new TournamentAction(TournamentActionKind.AdvanceTalk, "the Official's line");
        if (s.InDuty)
            return TournamentAction.Wait("still in the tournament duty");
        if (s.PlayerOccupied)
            return TournamentAction.Wait("the game has the player occupied");
        // Outside the sign-up window the Official only repeats that it is closed.
        if (s.SignUpOpensAt is { } opens)
            return TournamentAction.Wait($"sign-up is closed; it opens at {opens.ToLocalTime():HH:mm}");
        if (s.CanInteract)
            return new TournamentAction(TournamentActionKind.Interact, "talk to the Official");
        return s.OfficialInRange
            ? new TournamentAction(TournamentActionKind.Retarget, "target the Official again")
            : TournamentAction.Wait("target the Open Tournament Official and stand next to it");
    }
}
