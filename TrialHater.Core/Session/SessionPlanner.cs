namespace TrialHater.Core.Session;

// One frame of what the session can see, read from the game by the plugin. Window flags mean
// "visible and ready for input". Keys change whenever a window shows something new (a new Talk
// line, the result prompt changing), so one action is taken per thing shown.
public sealed record SessionSnapshot
{
    public bool BoardOpen { get; init; }

    public bool ResultOpen { get; init; }

    // A match window is on screen at all, actionable or not (the result window appears before its
    // buttons go live). A session never ends while one is up: it quits through it.
    public bool MatchWindowVisible { get; init; }

    public string ResultKey { get; init; } = string.Empty;

    public bool DeckSelectOpen { get; init; }

    // The saved deck row to pick in the deck window (-1: its own confirm button, the game's
    // default deck), and why to wait instead (the saved decks are still being ranked).
    public int DeckRow { get; init; } = -1;

    public string? DeckPending { get; init; }

    // Why the session must not start yet (its deck is being built): it waits before talking to the
    // NPC. Match windows still get their actions.
    public string? Preparing { get; init; }

    public bool RequestOpen { get; init; }

    public string RequestKey { get; init; } = string.Empty;

    // Why this challenge must be declined: its rules are not the ones the session's deck was built
    // for, and a rematch only offers the saved decks, so the deck is built again first.
    public string? RebuildDeck { get; init; }

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

public sealed record SessionGoal(int MaxMatches, bool StopRequested, bool CardsCollected = false)
{
    // MaxMatches 0 = until stopped. CardsCollected: a card session has every card it farms for.
    public bool Reached(int matchesDone) => this.StopRequested || this.CardsCollected || (this.MaxMatches > 0 && matchesDone >= this.MaxMatches);

    public string Why(int matchesDone)
        => this.StopRequested ? "stop requested" : this.CardsCollected ? "every reward card collected" : $"{matchesDone} match(es) played";
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
                ? new SessionAction(SessionActionKind.Quit, "TripleTriadResult", s.ResultKey, goal.Why(matchesDone))
                : new SessionAction(SessionActionKind.Rematch, "TripleTriadResult", s.ResultKey, $"match {matchesDone + 1} next");
        }

        if (s.DeckSelectOpen)
        {
            if (s.DeckPending is { } pending)
                return SessionAction.Wait(pending);
            return s.DeckRow >= 0
                ? new SessionAction(SessionActionKind.ConfirmDeck, "TripleTriadSelDeck", "deck", $"deck row {s.DeckRow}", s.DeckRow)
                : new SessionAction(SessionActionKind.ConfirmDeck, "TripleTriadSelDeck", "deck", "deck selection");
        }
        if (s.RequestOpen)
        {
            if (stopping)
                return new SessionAction(SessionActionKind.DeclineRequest, "TripleTriadRequest", s.RequestKey, "goal reached");
            return s.RebuildDeck is { } why
                ? new SessionAction(SessionActionKind.DeclineRequest, "TripleTriadRequest", s.RequestKey, why)
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

        if (s.MatchWindowVisible)
            return SessionAction.Wait("waiting for the match window to take input");
        if (s.Preparing is { } preparing)
            return SessionAction.Wait(preparing);
        if (stopping)
            return new SessionAction(SessionActionKind.Done, string.Empty, string.Empty, goal.StopRequested ? "stopped" : goal.Why(matchesDone));
        if (s.PlayerOccupied)
            return SessionAction.Wait("the game has the player occupied");
        if (!s.CanInteract)
            return SessionAction.Wait(sessionStarted ? "waiting for the next window" : "target the NPC and stand next to it");
        return new SessionAction(SessionActionKind.Interact, "world", "npc", "talk to the NPC");
    }
}
