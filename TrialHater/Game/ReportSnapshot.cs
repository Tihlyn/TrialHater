using System.Globalization;
using System.Text;
using Dalamud.Plugin;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Reporting;

namespace TrialHater.Game;

// The plugin's state when a report is made, as text (state.txt, quoted in the issue): what the
// automation is doing and why it waits, the tracker, the view and the solver's decision for it,
// the last board frame and the windows on screen. Framework thread.
internal static class ReportSnapshot
{
    // The windows the automation reads or presses.
    public static readonly string[] Addons =
    [
        BoardReader.AddonName, "TripleTriadRequest", "TripleTriadSelDeck", "TripleTriadResult", "TripleTriadDeckConfirmation",
        "TripleTriadPickUpDeckSelect", "TripleTriadTournamentMLReport", "TripleTriadTournamentResult", "TripleTriadTournamentReward",
        "Talk", "SelectString", "SelectIconString", "SelectYesno", "ContentsFinderConfirm", "ContextMenu",
        "GoldSaucerInfo", "GSInfoCardDeck", "GSInfoEditDeck",
    ];

    private const int MaxNotes = 80;
    private const int MaxMoves = 30;

    // One line for the issue title.
    public static string Headline(Plugin plugin)
    {
        var tracker = plugin.Tracker;
        var against = plugin.CurrentNpc?.Name ?? tracker.Opponent;
        var where = tracker.InMatch ? $"match {tracker.MatchGeneration}"
            : plugin.Session.Active ? "farming"
            : plugin.Tournament.Active ? "tournament queue"
            : "no match";
        return $"{plugin.AutoPlayer.Status} · {where}{(against is null ? string.Empty : $" vs {against}")}";
    }

    // What the state suggests went wrong; the tester can pick another.
    public static ReportCategory Suggest(Plugin plugin)
    {
        var analysis = plugin.Analysis;
        if (plugin.Tracker.View is { } view && analysis.Latest is { Status: DecisionStatus.NoBelief } latest && latest.Fingerprint == view.Fingerprint())
            return ReportCategory.NoBelief;
        if (analysis.Busy && (DateTime.UtcNow - analysis.StartedAt).TotalSeconds > plugin.Configuration.ExploitBudgetSeconds + 5)
            return ReportCategory.SolveStalled;
        return plugin.AutoPlayer.Enabled || plugin.Session.Active || plugin.Tournament.Active ? ReportCategory.Stalled : ReportCategory.Other;
    }

    // `extra`: the plugin's own session state, which only it can see. Numbers read the same on
    // every tester's machine: the text is built under the invariant culture, nested ToStrings
    // included, and the thread's culture is put back before returning.
    public static string State(Plugin plugin, string extra, Func<string, bool> visible)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return Describe(plugin, extra, visible);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static string Describe(Plugin plugin, string extra, Func<string, bool> visible)
    {
        var s = new StringBuilder();
        var now = DateTimeOffset.Now;
        s.AppendLine($"TrialHater {Plugin.VersionText} at {now:yyyy-MM-dd HH:mm:ss zzz} ({now.UtcDateTime:HH:mm:ss} UTC)");
        s.AppendLine($"Dalamud {typeof(IDalamudPluginInterface).Assembly.GetName().Version}, {Environment.ProcessorCount} logical cores, sheets "
                     + (plugin.Data is not null ? "loaded" : plugin.DataError is { } error ? $"failed: {error}" : "loading"));

        Section(s, "Automation");
        var auto = plugin.AutoPlayer;
        s.AppendLine($"auto play: {(auto.Enabled ? "on" : "off")} · {auto.Status} · {auto.Placed} placed · last dispatch: {Or(auto.LastDispatch)}");
        var session = plugin.Session;
        s.AppendLine($"session: {(session.Active ? "active" : "off")} · {Or(session.NpcName)} (#{session.NpcTriadId}) · {session.MatchesDone}/{session.MaxMatches}"
                     + (session.UntilCards ? $" until every card drops, missing: {string.Join(", ", session.MissingCards)}" : string.Empty)
                     + $" · {session.Wins}W {session.Draws}D {session.Losses}L" + (session.StopRequested ? " · stop requested" : string.Empty));
        s.AppendLine($"  status: {session.Status}");
        s.AppendLine($"  last action: {Or(session.LastAction)}");
        s.AppendLine($"  deck row {session.DeckRow} [{string.Join(",", session.DeckCards)}] · pending: {Or(session.DeckPending)} · preparing: {Or(session.Preparing)} · rebuild: {Or(session.RebuildDeck)}");
        var tournament = plugin.Tournament;
        s.AppendLine(tournament.Active
            ? $"tournament: active · {tournament.Phase} · {tournament.TournamentsDone}/{tournament.MaxTournaments} · distance {tournament.Distance:0.0}"
            : "tournament: off");
        s.AppendLine($"  status: {tournament.Status}");
        s.AppendLine($"  last action: {Or(tournament.LastAction)}");
        s.AppendLine($"deck writer: {(plugin.DeckWriter.Active ? "active" : "idle")} · {Or(plugin.DeckWriter.Status)}");
        s.AppendLine($"deck search: {(plugin.Decks.Running ? "running" : "idle")} · {plugin.Decks.Npc?.Name ?? "-"} [{plugin.Decks.Rules}] · {Or(plugin.Decks.Progress)} · error: {Or(plugin.Decks.Error)}");
        s.AppendLine($"deck chooser: {(plugin.DeckChooser.Running ? "running" : "idle")} · row {plugin.DeckChooser.Row} · {Or(plugin.DeckChooser.Summary)}");
        s.AppendLine($"deck prep: {(plugin.DeckPrep.Running ? "running" : "idle")} · {Or(plugin.DeckPrep.Pending)} · {Or(plugin.DeckPrep.Summary)}");
        s.AppendLine($"draft: {(plugin.Draft.Running ? "running" : "idle")} · {plugin.Draft.Offer.Count} offered · {Or(plugin.Draft.Status)}");
        var idle = plugin.IdleGuard;
        s.AppendLine($"anti-AFK: {(idle.Enabled ? "on" : "off")} · {idle.Status} · {idle.NudgesThisSession} presses · {idle.LastTimers?.ToString() ?? "no timers"}");
        s.AppendLine(extra);

        Section(s, "Match");
        var tracker = plugin.Tracker;
        s.AppendLine($"in match: {tracker.InMatch} · ended: {tracker.Ended} · match {tracker.MatchGeneration} · round {tracker.Round} · turn {tracker.TurnGeneration}");
        s.AppendLine($"NPC #{tracker.NpcTriadId} {plugin.CurrentNpc?.Name ?? "(no deck data)"} · opponent on the board: {Or(tracker.Opponent)} · rules [{tracker.Rules}]");
        s.AppendLine($"warning: {Or(tracker.Warning)}");
        s.AppendLine($"NPC played: {(tracker.RedPlayed.Count == 0 ? "-" : string.Join(" ", tracker.RedPlayed))}");
        if (tracker.View is { } view)
            View(s, view);
        else
            s.AppendLine("no view");

        Section(s, "Solver");
        Solver(s, plugin.Analysis, tracker.View);

        Section(s, "Board frame");
        Frame(s, tracker.Last);

        Section(s, "Windows on screen");
        var shown = Addons.Where(visible).ToList();
        s.AppendLine(shown.Count == 0 ? "none of the watched windows" : string.Join(", ", shown));

        Section(s, $"Match log (last {MaxNotes})");
        var notes = tracker.Notes;
        for (var i = Math.Max(0, notes.Count - MaxNotes); i < notes.Count; i++)
            s.AppendLine(notes[i]);
        return s.ToString();
    }

    private static void View(StringBuilder s, MatchView view)
    {
        s.AppendLine($"to move: {view.ToMove} · forced slot {view.ForcedSlot} · round {view.Round} · NPC #{view.NpcTriadId}");
        for (var row = 0; row < 3; row++)
            s.AppendLine("  " + string.Join("  ", Enumerable.Range(row * 3, 3).Select(c => view.Board[c] is { } b ? $"{(b.Owner == Side.Blue ? "B" : "R")}:{b.Card}" : ".").Select(t => t.PadRight(14))));
        s.AppendLine($"blue: {string.Join(" | ", view.Blue)}");
        s.AppendLine($"red:  {string.Join(" | ", view.Red)}");
        if (view.RedForeign.Count + view.RedLost.Count > 0)
            s.AppendLine($"swap: red holds ours {string.Join(" ", view.RedForeign)} · we hold theirs {string.Join(" ", view.RedLost)}");
        if (view.KnownDeck.Count > 0 || view.SlotStars is not null)
            s.AppendLine($"draft: known deck {string.Join(" ", view.KnownDeck)} · slot stars {(view.SlotStars is { } stars ? string.Join("-", stars) : "-")}");
        s.AppendLine($"fingerprint: {view.Fingerprint()}");
    }

    private static void Solver(StringBuilder s, AnalysisService analysis, MatchView? view)
    {
        s.AppendLine(analysis.Busy ? $"busy for {(DateTime.UtcNow - analysis.StartedAt).TotalSeconds:0.0} s" : "idle");
        if (analysis.Error is { } error)
            s.AppendLine($"error: {error.Message} (for {(view is not null && error.Fingerprint == view.Fingerprint() ? "this view" : $"another view: {error.Fingerprint}")})");
        if (analysis.Latest is not { } decision)
        {
            s.AppendLine("no decision");
            return;
        }

        var fresh = view is not null && decision.Fingerprint == view.Fingerprint();
        s.AppendLine($"decision ({(fresh ? "for this view" : "for another view")}): {decision.Status} · {decision.Reason}");
        if (!fresh)
            s.AppendLine($"  its fingerprint: {decision.Fingerprint}");
        s.AppendLine($"  move {decision.Move?.ToString() ?? "-"} · {decision.Hypotheses} hand(s) · {decision.Nodes:N0} nodes · {decision.Elapsed.TotalMilliseconds:0} ms");
        s.AppendLine($"  belief: {Or(decision.Belief)}");
        foreach (var m in decision.Moves.Take(MaxMoves))
            s.AppendLine($"  {m.Move,-10} expected {m.ExpectedValue:+0.00;-0.00;0.00} worst {m.WorstValue:+0;-0;0} {(m.Safe ? "safe" : "    ")} model {(m.Exploit is { } x ? $"{x:+0.00;-0.00;0.00}" : "-")} flips {m.Captures}");
    }

    private static void Frame(StringBuilder s, BoardFrame? frame)
    {
        if (frame is null)
        {
            s.AppendLine("none");
            return;
        }

        s.AppendLine($"frame {frame.Frame} · visible {frame.Visible} · ready {frame.Ready} · turn state {frame.TurnState} · command {frame.Command?.ToString() ?? "-"} ({frame.Arg1?.ToString() ?? "-"}, {frame.Arg2?.ToString() ?? "-"})");
        s.AppendLine($"can act {frame.CanAct} · forced slot {frame.ForcedSlot} · seated red {frame.SeatedRed} · on board {frame.OnBoard} · hands {frame.BlueInHand}/{frame.RedInHand} · opponent {Or(frame.OpponentName)}");
        s.AppendLine($"values: {string.Join(",", frame.LeadingValues.Select(v => v?.ToString() ?? "_"))}");
        s.AppendLine($"texts: {string.Join(" | ", frame.Texts.Where(t => t.Length > 0))}");
    }

    private static void Section(StringBuilder s, string title) => s.AppendLine().AppendLine($"== {title}");

    private static string Or(string? text) => string.IsNullOrEmpty(text) ? "-" : text;
}
