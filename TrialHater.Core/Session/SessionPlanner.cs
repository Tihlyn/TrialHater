namespace TrialHater.Core.Session;

// One frame of what the session can see, read from the game by the plugin. Window flags mean
// "visible and ready for input". Keys change whenever a window shows something new (a new Talk
// line, the result prompt changing), so one action is taken per thing shown.
public sealed record SessionSnapshot
{
    public bool BoardOpen { get; init; }

    public bool ResultOpen { get; init; }

    public string ResultKey { get; init; } = string.Empty;

    public bool DeckSelectOpen { get; init; }

    public bool RequestOpen { get; init; }

    public string RequestKey { get; init; } = string.Empty;

    public bool TalkOpen { get; init; }

    public string TalkKey { get; init; } = string.Empty;

    public bool MenuOpen { get; init; }

    // Index of the "Triple Triad Challenge" entry (icon 71101), -1 when the menu has none.
    public int MenuTriadIndex { get; init; } = -1;

    public string MenuKey { get; init; } = string.Empty;

    // A Yes/No dialog: the session never answers one, it waits for the player.
    public bool YesNoOpen { get; init; }

    // The session NPC is targeted and within interaction range, and the player is free to act.
    public bool CanInteract { get; init; }

    // The game says the player is busy (talking, in an event, between zones).
    public bool PlayerOccupied { get; init; }
}

public enum SessionActionKind
{
    Wait,
    Interact,
    ChooseMenu,
    AdvanceTalk,
    Challenge,
    DeclineRequest,
    ConfirmDeck,
    Rematch,
    Quit,
    Done,
}

public sealed record SessionAction(SessionActionKind Kind, string Window, string Key, string Reason, int Index = -1)
{
    public static SessionAction Wait(string reason) => new(SessionActionKind.Wait, string.Empty, string.Empty, reason);
}

public sealed record SessionGoal(int MaxMatches, bool StopRequested)
{
    // MaxMatches 0 = until stopped.
    public bool Reached(int matchesDone) => this.StopRequested || (this.MaxMatches > 0 && matchesDone >= this.MaxMatches);
}

// Decides the single next session action from what is on screen. The window being shown is the
// state: whatever window is up gets its one correct action, so a skipped or repeated window
// (rematch reuses the request window, a quit adds a Talk line) needs no special path. Captured
// flow (docs/MODEL_MAPPING.md): menu -> Talk -> Request -> deck -> board -> Result -> (rematch:
// Request -> deck -> board | quit: Talk).
public static class SessionPlanner
{
    public static SessionAction Next(SessionSnapshot s, SessionGoal goal, int matchesDone, bool sessionStarted)
    {
        var stopping = goal.Reached(matchesDone);
        if (s.YesNoOpen)
            return SessionAction.Wait("a Yes/No dialog is open; the session never answers dialogs");
        if (s.BoardOpen)
            return SessionAction.Wait("match in progress");
        if (s.ResultOpen)
        {
            return stopping
                ? new SessionAction(SessionActionKind.Quit, "TripleTriadResult", s.ResultKey, goal.StopRequested ? "stop requested" : $"{matchesDone} match(es) played")
                : new SessionAction(SessionActionKind.Rematch, "TripleTriadResult", s.ResultKey, $"match {matchesDone + 1} next");
        }

        if (s.DeckSelectOpen)
            return new SessionAction(SessionActionKind.ConfirmDeck, "TripleTriadSelDeck", "deck", "deck selection");
        if (s.RequestOpen)
        {
            return stopping
                ? new SessionAction(SessionActionKind.DeclineRequest, "TripleTriadRequest", s.RequestKey, "goal reached")
                : new SessionAction(SessionActionKind.Challenge, "TripleTriadRequest", s.RequestKey, "challenge");
        }

        if (s.TalkOpen)
            return new SessionAction(SessionActionKind.AdvanceTalk, "Talk", s.TalkKey, "NPC line");
        if (s.MenuOpen)
        {
            if (stopping)
                return SessionAction.Wait("goal reached; leaving the NPC menu to the player");
            return s.MenuTriadIndex < 0
                ? SessionAction.Wait("the NPC menu has no Triple Triad Challenge entry")
                : new SessionAction(SessionActionKind.ChooseMenu, "SelectIconString", s.MenuKey, "Triple Triad Challenge", s.MenuTriadIndex);
        }

        if (stopping)
            return new SessionAction(SessionActionKind.Done, string.Empty, string.Empty, goal.StopRequested ? "stopped" : $"{matchesDone} match(es) played");
        if (s.PlayerOccupied)
            return SessionAction.Wait("the game has the player occupied");
        if (!s.CanInteract)
            return SessionAction.Wait(sessionStarted ? "waiting for the next window" : "target the NPC and stand next to it");
        return new SessionAction(SessionActionKind.Interact, "world", "npc", "talk to the NPC");
    }
}
