using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;

namespace TrialHater.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 BlueColor = new(0.45f, 0.70f, 1f, 1f);
    private static readonly Vector4 RedColor = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 MutedColor = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 GoodColor = new(0.4f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.8f, 0.3f, 1f);

    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("TrialHater###TrialHaterMain")
    {
        this.plugin = plugin;
        this.Size = new Vector2(560, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose()
    {
        this.plugin.Configuration.ShowOverlay = false;
        this.plugin.Configuration.Save();
    }

    public override void Draw()
    {
        if (this.plugin.Data is null)
        {
            ImGui.TextColored(this.plugin.DataError is null ? MutedColor : RedColor,
                this.plugin.DataError is null ? "Loading Triple Triad sheets..." : $"Sheets failed to load: {this.plugin.DataError}");
            return;
        }

        this.DrawHeader();
        this.DrawSession();
        var tracker = this.plugin.Tracker;
        if (tracker.View is not { } view)
        {
            ImGui.TextColored(MutedColor, "No match on screen.");
            this.DrawNotes();
            return;
        }

        var decision = this.plugin.Analysis.Latest;
        var fresh = decision is not null && decision.Fingerprint == view.Fingerprint();
        ImGui.Separator();
        this.DrawBoard(view, fresh ? decision!.Move : null);
        ImGui.Separator();
        this.DrawDecision(view, fresh ? decision : null);
        this.DrawNotes();
    }

    private void DrawHeader()
    {
        var plugin = this.plugin;
        var auto = plugin.AutoPlayer.Enabled;
        if (ImGui.Checkbox("Auto play", ref auto))
            plugin.SetAutoPlay(auto);
        ImGui.SameLine();
        ImGui.TextColored(auto ? GoodColor : MutedColor, plugin.AutoPlayer.Status);
        ImGui.SameLine(ImGui.GetWindowWidth() - 80);
        if (ImGui.SmallButton("Settings"))
            plugin.ConfigWindow.IsOpen = true;

        var tracker = plugin.Tracker;
        var npc = plugin.CurrentNpc;
        ImGui.TextUnformatted($"NPC: {npc?.Name ?? (tracker.NpcTriadId == 0 ? "unknown (target the NPC before challenging)" : $"#{tracker.NpcTriadId}")}");
        if (tracker.InMatch)
        {
            ImGui.TextUnformatted($"Match #{tracker.MatchGeneration}{(tracker.Round > 0 ? $", Sudden Death round {tracker.Round}" : string.Empty)} | rules: {tracker.Rules}");
            if (npc is not null && npc.Rules.Count > 0)
                ImGui.TextColored(MutedColor, $"NPC sheet rules: {string.Join(", ", npc.Rules)}{(npc.UsesRegionalRules ? " + regional" : string.Empty)}");
        }

        if (tracker.Warning is { } warning)
            ImGui.TextColored(WarnColor, warning);
    }

    private string? sessionError;

    // Farming: talk, challenge, deck, auto play, rematch until the goal, quit.
    private void DrawSession()
    {
        var session = this.plugin.Session;
        var config = this.plugin.Configuration;
        if (!ImGui.CollapsingHeader("Session", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!session.Active)
        {
            var max = config.SessionMaxMatches;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Matches (0 = until stopped)", ref max))
            {
                config.SessionMaxMatches = max;
                config.Save();
            }

            if (ImGui.Button("Start session"))
                this.sessionError = this.plugin.StartSession();
            ImGui.SameLine();
            ImGui.TextColored(MutedColor, "target the NPC, stand next to it");
            if (this.sessionError is { } error)
                ImGui.TextColored(WarnColor, error);
        }
        else
        {
            var goal = session.MaxMatches == 0 ? "until stopped" : $"{session.MatchesDone}/{session.MaxMatches}";
            ImGui.TextUnformatted($"Farming {session.NpcName}: {goal} | {session.Wins}W {session.Draws}D {session.Losses}L");
            if (!session.StopRequested && ImGui.Button("Stop after this match"))
                session.StopRequested = true;
            ImGui.SameLine();
            if (ImGui.Button("Stop now"))
                session.Stop("stopped by the player");
        }

        ImGui.TextColored(session.Active ? GoodColor : MutedColor, session.Status);
        if (session.LastAction.Length > 0)
            ImGui.TextColored(MutedColor, $"last: {session.LastAction}");
    }

    private void DrawBoard(MatchView view, Move? recommended)
    {
        ImGui.TextUnformatted($"{(view.ToMove == Side.Blue ? "Our turn" : "Opponent's turn")}{(view.ForcedSlot >= 0 ? $" (forced slot {view.ForcedSlot})" : string.Empty)}");
        if (ImGui.BeginTable("hands", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(BlueColor, "Our hand");
            for (var i = 0; i < view.Blue.Length; i++)
            {
                var mark = recommended?.HandSlot == i ? " <-" : string.Empty;
                ImGui.TextColored(recommended?.HandSlot == i ? GoodColor : BlueColor, $"{i}: {view.Blue[i]}{mark}");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(RedColor, "NPC hand");
            for (var i = 0; i < view.Red.Length; i++)
                ImGui.TextColored(view.Red[i].State == SlotState.Hidden ? MutedColor : RedColor, $"{i}: {view.Red[i]}");
            if (view.RedPlayed.Count > 0)
                ImGui.TextColored(MutedColor, $"played: {string.Join(" ", view.RedPlayed)}");
            ImGui.EndTable();
        }

        if (!ImGui.BeginTable("board", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
            return;
        for (var cell = 0; cell < 9; cell++)
        {
            if (cell % 3 == 0)
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 64);
            ImGui.TableNextColumn();
            if (recommended?.Cell == cell)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(new Vector4(0.2f, 0.5f, 0.25f, 0.6f)));
            if (view.Board[cell] is { } card)
            {
                var color = card.Owner == Side.Blue ? BlueColor : RedColor;
                var c = card.Card;
                ImGui.TextColored(color, $"   {Glyph(c.Up)}");
                ImGui.TextColored(color, $" {Glyph(c.Left)}   {Glyph(c.Right)}");
                ImGui.TextColored(color, $"   {Glyph(c.Down)} {(c.Type == Core.Cards.CardType.None ? string.Empty : c.Type.ToString()[..1])}");
            }
            else
            {
                ImGui.TextColored(MutedColor, $"  [{cell}]   ");
            }
        }

        ImGui.EndTable();
    }

    private void DrawDecision(MatchView view, Decision? decision)
    {
        if (view.ToMove != Side.Blue)
        {
            ImGui.TextColored(MutedColor, "Waiting for the NPC.");
            return;
        }

        if (decision is null)
        {
            ImGui.TextColored(MutedColor, this.plugin.Analysis.Busy ? "Solving..." : "No decision yet.");
            return;
        }

        if (decision.Status != DecisionStatus.Ready || decision.Chosen is not { } chosen)
        {
            ImGui.TextColored(WarnColor, $"{decision.Status}: {decision.Reason}");
            return;
        }

        ImGui.TextColored(GoodColor, $"Play slot {chosen.Move.HandSlot} ({view.Blue[chosen.Move.HandSlot]}) on cell {chosen.Move.Cell}");
        ImGui.TextUnformatted($"Guaranteed (expected over {decision.Hypotheses} NPC hand(s)): {Outcome(chosen.ExpectedValue)}; worst hand {Outcome(chosen.WorstValue)}"
                              + (chosen.Exploit is { } e ? $"; vs NPC model {e:+0.00;-0.00}" : string.Empty));
        ImGui.TextColored(MutedColor, $"{decision.Reason} | {decision.Belief} | {decision.Nodes:N0} nodes, {decision.Elapsed.TotalMilliseconds:0} ms");

        if (!ImGui.CollapsingHeader($"All moves ({decision.Moves.Count})"))
            return;
        if (!ImGui.BeginTable("moves", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 220)))
            return;
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Cell");
        ImGui.TableSetupColumn("Expected");
        ImGui.TableSetupColumn("Worst");
        ImGui.TableSetupColumn("Vs model");
        ImGui.TableSetupColumn("Flips");
        ImGui.TableHeadersRow();
        foreach (var m in decision.Moves)
        {
            ImGui.TableNextRow();
            var color = m.Move == chosen.Move ? GoodColor : m.Safe ? new Vector4(1, 1, 1, 1) : MutedColor;
            ImGui.TableNextColumn();
            ImGui.TextColored(color, m.Move.HandSlot.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(color, m.Move.Cell.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(color, $"{m.ExpectedValue:+0.00;-0.00;0.00}");
            ImGui.TableNextColumn();
            ImGui.TextColored(color, $"{m.WorstValue:+0;-0;0}");
            ImGui.TableNextColumn();
            ImGui.TextColored(color, m.Exploit is { } x ? $"{x:+0.00;-0.00;0.00}" : "-");
            ImGui.TableNextColumn();
            ImGui.TextColored(color, m.Captures.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawNotes()
    {
        var notes = this.plugin.Tracker.Notes;
        if (notes.Count == 0 || !ImGui.CollapsingHeader("Match log"))
            return;
        for (var i = notes.Count - 1; i >= Math.Max(0, notes.Count - 30); i--)
            ImGui.TextColored(MutedColor, notes[i]);
    }

    private static string Glyph(byte value) => value >= 10 ? "A" : value.ToString();

    private static string Outcome(double value) => value switch
    {
        >= 0.999 => "win",
        <= -0.999 => "loss",
        > -0.001 and < 0.001 => "draw",
        _ => $"{value:+0.00;-0.00}",
    };
}
