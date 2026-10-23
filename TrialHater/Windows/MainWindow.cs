using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;
using TrialHater.Game;

namespace TrialHater.Windows;

// Shaped like a Triple Triad card. Always in view: auto play, what is running and the opponent.
// Then the draft advisor (while drafting), the match (board, hands, the move to play) and tabs
// for farming, decks, tournaments and the match log.
public sealed class MainWindow : Window
{
    private static readonly string[] CellNames = ["top left", "top", "top right", "left", "centre", "right", "bottom left", "bottom", "bottom right"];

    private readonly Plugin plugin;
    private Theme.Scope theme;
    private string? sessionError;
    private string? deckError;
    private string? writeError;
    private string? tournamentError;

    public MainWindow(Plugin plugin)
        : base("TrialHater###TrialHaterMain", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.Size = Theme.MainSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = Theme.MainMinimum, MaximumSize = Theme.Maximum };
    }

    public override void PreDraw() => this.theme = new Theme.Scope(this.plugin.Configuration.Backdrop);

    public override void PostDraw() => this.theme.Dispose();

    public override void Draw()
    {
        var config = this.plugin.Configuration;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        Chrome.Backdrop(config.Backdrop, config.BackdropOpacity);
        Chrome.PushInner();
        switch (Chrome.Header("TrialHater", Plugin.VersionText, (FontAwesomeIcon.Bug, "Report an issue (/thater report)"),
                    (FontAwesomeIcon.Cog, "Settings"), (FontAwesomeIcon.Times, "Close (/thater)")))
        {
            case 0:
                this.plugin.OpenReport();
                break;
            case 1:
                this.plugin.ConfigWindow.Toggle();
                break;
            case 2:
                this.IsOpen = false;
                break;
        }

        if (this.plugin.Data is not null)
            this.DrawStatus();
        var visible = Chrome.BeginBody("##body");
        if (visible)
            this.DrawBody();
        Chrome.EndBody(visible, min, max);
        Chrome.Footer($"{this.plugin.AutoPlayer.Placed} cards auto-placed  ·  anti-AFK: {this.plugin.IdleGuard.Status}");
        Chrome.PopInner();
    }

    private void DrawBody()
    {
        if (this.plugin.Data is null)
        {
            Widgets.Text(this.plugin.DataError is null ? "Loading the Triple Triad sheets..." : $"The sheets failed to load: {this.plugin.DataError}",
                this.plugin.DataError is null ? Theme.Muted : Theme.Red);
            return;
        }

        this.DrawDraft();
        if (this.plugin.Tracker.View is { } view)
            this.DrawMatch(view);
        else
            this.DrawIdle();

        ImGui.Dummy(new Vector2(0f, Theme.Px(2f)));
        if (!ImGui.BeginTabBar("##tabs"))
            return;
        if (ImGui.BeginTabItem("Farm"))
        {
            this.DrawSession();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Decks"))
        {
            this.DrawDeckBuilder();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Tournament"))
        {
            this.DrawTournamentQueue();
            this.DrawDraftRecord();
            this.DrawTournaments();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Match log"))
        {
            this.DrawNotes();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // Auto play, whatever automation is running (with its stop button) and the opponent.
    private void DrawStatus()
    {
        var plugin = this.plugin;
        var auto = plugin.AutoPlayer.Enabled;
        if (Widgets.ToggleRow("Auto play", ref auto, "Place the recommended card on our turn. '/thater auto on|off' does the same."))
            plugin.SetAutoPlay(auto);
        ImGui.SameLine(0f, Theme.Px(Theme.Gap * 2f));
        Widgets.Text(plugin.AutoPlayer.Status, auto ? Theme.Good : Theme.Muted);

        var session = plugin.Session;
        if (session.Active)
        {
            this.Activity("FARMING", $"{session.NpcName}  {Goal(session)}  ·  {session.Wins}W {session.Draws}D {session.Losses}L");
            if (!session.StopRequested)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop after this match"))
                    session.StopRequested = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##session"))
                session.Stop("stopped by the player");
        }
        else if (session.HasRecap)
        {
            this.Activity("FARMED", $"{session.NpcName}  {session.MatchesDone} played  ·  {session.Wins}W {session.Draws}D {session.Losses}L", Theme.Gold);
        }

        var tournament = plugin.Tournament;
        if (tournament.Active)
        {
            var goal = tournament.MaxTournaments == 0 ? "until stopped" : $"{tournament.TournamentsDone}/{tournament.MaxTournaments}";
            this.Activity("TOURNAMENT", $"{goal}  ·  {tournament.Phase}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##tournament"))
                tournament.Stop("stopped by the player");
        }

        if (plugin.DeckWriter.Active)
        {
            this.Activity("WRITING DECK", plugin.DeckWriter.Status);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##writer"))
                plugin.DeckWriter.Stop("stopped by the player");
        }

        if (plugin.Decks.Running)
        {
            this.Activity("DECK SEARCH", $"{plugin.Decks.Npc?.Name}: {plugin.Decks.Progress}", working: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##decks"))
                plugin.Decks.Cancel();
        }

        this.DrawOpponent();
        if (plugin.Tracker.Warning is { } warning)
            Widgets.Text(warning, Theme.Warn);
        Widgets.Ornament();
    }

    private void Activity(string label, string text, Vector4? color = null, bool working = false)
    {
        Widgets.Chip(label, color ?? Theme.Good);
        ImGui.SameLine(0f, Theme.Px(Theme.Gap));
        if (working)
        {
            Widgets.Spinner(Theme.Good);
            ImGui.SameLine(0f, Theme.Px(Theme.SmallGap));
        }

        ImGui.TextUnformatted(text);
    }

    private void DrawOpponent()
    {
        var tracker = this.plugin.Tracker;
        var npc = this.plugin.CurrentNpc;
        string name, detail = string.Empty;
        if (npc is null && tracker.InMatch && tracker.Opponent is { } opponent)
        {
            var known = tracker.View?.KnownDeck.Count ?? 0;
            name = opponent;
            detail = !tracker.Rules.Has(TriadRule.Draft) ? "no deck data, hidden cards sampled"
                : known > 0 ? $"drafted deck, {known}/5 cards known from an earlier match"
                : "drafted deck, hidden cards sampled";
        }
        else if (npc is not null)
        {
            name = npc.Name;
        }
        else if (tracker.NpcTriadId != 0)
        {
            name = $"#{tracker.NpcTriadId}";
        }
        else
        {
            Widgets.Muted("No NPC yet: target one before challenging it.");
            return;
        }

        Widgets.OutlinedText(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), name, Theme.Red);
        ImGui.Dummy(ImGui.CalcTextSize(name));
        if (detail.Length > 0)
        {
            ImGui.SameLine();
            Widgets.Muted(detail);
        }

        if (tracker.InMatch)
        {
            var chips = new List<(string, Vector4)> { ($"Match {tracker.MatchGeneration}", Theme.Muted) };
            if (tracker.Round > 0)
                chips.Add(($"Sudden Death {tracker.Round}", Theme.Warn));
            chips.AddRange(tracker.Rules.Rules.Select(r => (this.RuleName(r), Theme.Gold)));
            Widgets.Chips(chips);
        }
        else if (npc is not null && npc.Rules.Count > 0)
        {
            Widgets.Chips(npc.Rules.Select(r => (this.RuleName(r), Theme.Alpha(Theme.Gold, 0.75f)))
                .Append((npc.UsesRegionalRules ? "+ regional rules" : "sheet rules", Theme.Muted)));
        }
    }

    private void DrawIdle()
    {
        using (Widgets.Panel())
        {
            Widgets.DisplayText("No match on screen", Theme.HeadlineScale, Theme.Muted);
            Widgets.Muted("Target an NPC and challenge it: the board, both hands and the move to play show here.");
        }
    }

    // The match as the game lays it out: our hand, the board, the NPC's hand; scores on the
    // medallions; the move to play glowing green, then what it guarantees.
    private void DrawMatch(MatchView view)
    {
        var decision = this.plugin.Analysis.Latest;
        var fresh = decision is not null && decision.Fingerprint == view.Fingerprint() ? decision : null;
        var recommended = fresh?.Status == DecisionStatus.Ready ? fresh.Move : null;
        using (Widgets.Panel(view.ToMove == Side.Blue))
        {
            this.DrawTable(view, recommended);
            this.DrawDecision(view, fresh);
        }
    }

    private void DrawTable(MatchView view, Move? recommended)
    {
        var draw = ImGui.GetWindowDrawList();
        var avail = Widgets.ContentWidth;
        var g = Theme.Px(4f);
        var groupGap = Theme.Px(12f);
        const float handRatio = 0.72f, aspect = 1.26f;
        var cardW = Math.Clamp((avail - 2f * groupGap - 4f * g) / (3f + 4f * handRatio), Theme.Px(36f), Theme.Px(96f));
        var cardH = cardW * aspect;
        var handW = cardW * handRatio;
        var handH = handW * aspect;
        var boardW = 3f * cardW + 2f * g;
        var boardH = 3f * cardH + 2f * g;
        var handColumn = 2f * handW + g;
        var total = 2f * handColumn + boardW + 2f * groupGap;

        // Score row: medallions over the hands, whose turn over the board.
        var radius = Theme.Px(15f);
        var top = ImGui.GetCursorScreenPos();
        var left = top.X + Math.Max(0f, (avail - total) / 2f);
        var blueX = left + handColumn / 2f;
        var boardX = left + handColumn + groupGap;
        var redX = boardX + boardW + groupGap + handColumn / 2f;
        var blueScore = view.Board.Count(c => c?.Owner == Side.Blue) + view.Blue.Count(s => s.State != SlotState.Empty);
        var redScore = view.Board.Count(c => c?.Owner == Side.Red) + view.Red.Count(s => s.State != SlotState.Empty);
        Widgets.Medallion(draw, new Vector2(blueX, top.Y + radius), radius, Side.Blue, blueScore);
        Widgets.Medallion(draw, new Vector2(redX, top.Y + radius), radius, Side.Red, redScore);
        var ours = view.ToMove == Side.Blue;
        var turn = ours ? "Your turn" : "NPC's turn";
        Widgets.CenteredText(draw, new Vector2(boardX + boardW / 2f, top.Y + radius), turn, ours ? Theme.Good : Theme.Red, 1.15f);
        var y = top.Y + 2f * radius + Theme.Px(Theme.Gap);

        // Hands, centred on the board vertically: two columns, the fifth card centred.
        var handTop = y + (boardH - (3f * handH + 2f * g)) / 2f;
        for (var i = 0; i < 5; i++)
        {
            var offset = new Vector2(i == 4 ? (handW + g) / 2f : i % 2 * (handW + g), i / 2 * (handH + g));
            var blueMin = new Vector2(left, handTop) + offset;
            var redMin = new Vector2(boardX + boardW + groupGap, handTop) + offset;
            this.HandCard(draw, blueMin, blueMin + new Vector2(handW, handH), view.Blue[i], Side.Blue,
                recommended?.HandSlot == i, view.ForcedSlot == i && ours);
            this.HandCard(draw, redMin, redMin + new Vector2(handW, handH), view.Red[i], Side.Red, false, false);
        }

        for (var cell = 0; cell < 9; cell++)
        {
            var min = new Vector2(boardX + cell % 3 * (cardW + g), y + cell / 3 * (cardH + g));
            var max = min + new Vector2(cardW, cardH);
            if (view.Board[cell] is { } placed)
            {
                Widgets.CardFace(draw, min, max, placed.Card, placed.Owner);
                if (Hovering(min, max))
                    Widgets.Tooltip($"{(placed.Owner == Side.Blue ? "Ours" : "NPC's")}: {Describe(placed.Card)}");
            }
            else if (recommended?.Cell == cell && view.Blue[recommended.Value.HandSlot] is { State: SlotState.Visible } slot)
            {
                Widgets.EmptyCard(draw, min, max);
                Widgets.CardFace(draw, min, max, slot.Card, Side.Blue, 0.45f);
                Widgets.Glow(draw, min, max, Theme.Good);
            }
            else
            {
                Widgets.EmptyCard(draw, min, max, CellNames[cell]);
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(top.X, y));
        ImGui.Dummy(new Vector2(avail, boardH + Theme.Px(Theme.SmallGap)));
    }

    private void HandCard(ImDrawListPtr draw, Vector2 min, Vector2 max, HandSlot slot, Side side, bool recommended, bool forced)
    {
        switch (slot.State)
        {
            case SlotState.Visible:
                Widgets.CardFace(draw, min, max, slot.Card, side);
                if (Hovering(min, max))
                    Widgets.Tooltip(Describe(slot.Card) + (forced ? "\nThe rules force this card." : string.Empty));
                break;
            case SlotState.Hidden:
                Widgets.CardBack(draw, min, max);
                if (Hovering(min, max))
                    Widgets.Tooltip("Hidden: the solver weighs every card the NPC's deck could hold here.");
                break;
            default:
                Widgets.EmptyCard(draw, min, max);
                break;
        }

        if (recommended)
            Widgets.Glow(draw, min, max, Theme.Good);
        else if (forced)
            Widgets.Glow(draw, min, max, Theme.Gold);
    }

    private void DrawDecision(MatchView view, Decision? decision)
    {
        if (view.ToMove != Side.Blue)
        {
            Widgets.Muted("Waiting for the NPC...");
            return;
        }

        if (decision is null)
        {
            var analysis = this.plugin.Analysis;
            if (analysis.Busy)
            {
                // Past the exploit budget (plus the exact solve before it) the search is slow.
                var elapsed = (DateTime.UtcNow - analysis.StartedAt).TotalSeconds;
                var slow = elapsed > this.plugin.Configuration.ExploitBudgetSeconds + 5;
                Widgets.Busy($"Solving... {elapsed:0.0} s{(slow ? " (longer than usual)" : string.Empty)}", slow ? Theme.Warn : Theme.Good);
            }
            else if (analysis.Error is { } error && error.Fingerprint == view.Fingerprint())
            {
                Widgets.Text($"The solver failed: {error.Message}. Details in /xllog; it retries when the board changes.", Theme.Red);
            }
            else
            {
                Widgets.Muted("No decision yet.");
            }

            return;
        }

        if (decision.Status != DecisionStatus.Ready || decision.Chosen is not { } chosen)
        {
            Widgets.Text($"{decision.Status}: {decision.Reason}", Theme.Warn);
            return;
        }

        var card = view.Blue[chosen.Move.HandSlot];
        Widgets.DisplayText($"Play {card} on the {CellNames[chosen.Move.Cell]}", Theme.HeadlineScale, Theme.Good);
        Widgets.HoverTooltip($"Hand card {chosen.Move.HandSlot + 1} ({Describe(card.Card)}) to cell {chosen.Move.Cell}.");
        var chips = new List<(string, Vector4)>
        {
            ($"Guaranteed {Outcome(chosen.ExpectedValue)}", OutcomeColor(chosen.ExpectedValue)),
            ($"Worst hand: {Outcome(chosen.WorstValue)}", OutcomeColor(chosen.WorstValue)),
        };
        if (chosen.Exploit is { } exploit)
            chips.Add(($"vs NPC model {exploit:+0.00;-0.00;0.00}", Theme.Blue));
        Widgets.Chips(chips);
        Widgets.Muted($"{decision.Hypotheses} NPC hand(s) · {decision.Nodes:N0} nodes · {decision.Elapsed.TotalMilliseconds:0} ms · {decision.Reason}");

        if (!ImGui.CollapsingHeader($"All moves ({decision.Moves.Count})"))
            return;
        if (view.RedPlayed.Count > 0)
            Widgets.Muted($"NPC played: {string.Join("  ", view.RedPlayed)}");
        Widgets.Muted(decision.Belief);
        if (!ImGui.BeginTable("moves", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0f, Theme.Px(200f))))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Card");
        ImGui.TableSetupColumn("Cell");
        ImGui.TableSetupColumn("Expected");
        ImGui.TableSetupColumn("Worst");
        ImGui.TableSetupColumn("Vs model");
        ImGui.TableSetupColumn("Flips");
        ImGui.TableHeadersRow();
        foreach (var m in decision.Moves)
        {
            ImGui.TableNextRow();
            var color = m.Move == chosen.Move ? Theme.Good : m.Safe ? Theme.Text : Theme.Muted;
            Cell(view.Blue[m.Move.HandSlot].ToString(), color);
            Cell(CellNames[m.Move.Cell], color);
            Cell($"{m.ExpectedValue:+0.00;-0.00;0.00}", color);
            Cell($"{m.WorstValue:+0;-0;0}", color);
            Cell(m.Exploit is { } x ? $"{x:+0.00;-0.00;0.00}" : "-", color);
            Cell(m.Captures.ToString(), color);
        }

        ImGui.EndTable();
    }

    // Open Tournament draft: the option to take at each pick (you press them, or auto play does).
    private void DrawDraft()
    {
        var draft = this.plugin.Draft;
        if (draft.Offer.Count == 0)
            return;
        using (Widgets.Panel(true))
        {
            Widgets.Section("Draft advisor");
            Widgets.Chips(draft.Rules.Rules.Select(r => (this.RuleName(r), Theme.Gold)));
            if (draft.Running || draft.Advice is not { } advice)
            {
                if (draft.Running)
                    Widgets.Busy(draft.Status, Theme.Good);
                else
                    Widgets.Text(draft.Status, Theme.Warn);
                return;
            }

            if (advice.Picks.Count == 0)
            {
                Widgets.Text(advice.Status, Theme.Warn);
                return;
            }

            var best = advice.Ranked[0];
            if (draft.ConfirmDiff is { } diff)
            {
                if (diff.Count == 0)
                    Widgets.Text("Deck to confirm = the advised deck.", Theme.Good);
                foreach (var (picked, advised) in diff)
                    Widgets.Text($"Deck to confirm has {picked.Name} ({picked.Stars}*), but the advice was {advised.Name}.", Theme.Warn);
            }

            Widgets.DisplayText($"Best deck {best.Expected:+0.00;-0.00;0.00} per game", Theme.HeadlineScale, Theme.Good);
            Widgets.Muted($"{best.Win:P0} wins, {best.Loss:P0} losses in simulation. Take the green option of each pick; the others show the best deck with that option, against the best deck.");
            if (ImGui.BeginTable("draft", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("Pick", ImGuiTableColumnFlags.WidthFixed, Theme.Px(34f));
                ImGui.TableSetupColumn("Option 1");
                ImGui.TableSetupColumn("Option 2");
                ImGui.TableSetupColumn("Option 3");
                ImGui.TableHeadersRow();
                for (var pick = 0; pick < 3; pick++)
                {
                    ImGui.TableNextRow();
                    Cell($"{pick + 1}", Theme.Gold);
                    foreach (var option in advice.Options.Where(o => o.Pick == pick).OrderBy(o => o.Choice))
                    {
                        var chosen = advice.Best[pick].Choice == option.Choice;
                        var cards = string.Join(" + ", option.Cards.Select(c => $"{c.Name} {c.Stars}*"));
                        ImGui.TableNextColumn();
                        if (chosen)
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, Theme.Color(Theme.GoodFill));
                        Widgets.Text(chosen ? $"> {cards}" : $"{cards}  {option.Value - best.Expected:+0.00;-0.00;0.00}", chosen ? Theme.Good : Theme.Muted);
                        if (this.plugin.Character.Drafts is { } record)
                            DraftTrack(record, draft.Rules, option.Cards);
                    }
                }

                ImGui.EndTable();
            }

            Widgets.Muted(advice.Status);
        }
    }

    // How an option's cards did when we drafted them before under these rules.
    private static void DraftTrack(DraftRecord record, RuleSet rules, IReadOnlyList<CardInfo> cards)
    {
        var tracks = cards.Select(c => (Card: c, Track: record.Card(rules, c.Id))).Where(x => x.Track.Vetted + x.Track.Discarded > 0).ToList();
        if (tracks.Count == 0)
            return;
        var vetted = tracks.Sum(x => x.Track.Vetted);
        var discarded = tracks.Sum(x => x.Track.Discarded);
        Widgets.Text($"vetted {vetted}, discarded {discarded}", vetted >= discarded ? Theme.Good : Theme.Red);
        Widgets.HoverTooltip(string.Join("\n", tracks.Select(x => $"{x.Card.Name}: top half {x.Track.Vetted}x, bottom half {x.Track.Discarded}x")));
    }

    // Our past tournaments: the deck we drafted and how it placed (vetted = top half).
    private void DrawDraftRecord()
    {
        if (this.plugin.Character.Drafts is not { Results.Count: > 0 } record)
            return;
        var results = record.Results;
        Widgets.Section("Our drafts");
        Widgets.Muted($"{this.plugin.Character.Name}: {results.Count(r => r.Vetted)} vetted, {results.Count(r => !r.Vetted)} discarded "
                      + $"(top {DraftRecord.VettedRank} of 8 = vetted). Ties in the draft advice go to the cards with the better record.");
        if (!ImGui.BeginTable("drafts", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, Theme.Px(80f));
        ImGui.TableSetupColumn("Place", ImGuiTableColumnFlags.WidthFixed, Theme.Px(70f));
        ImGui.TableSetupColumn("Expected", ImGuiTableColumnFlags.WidthFixed, Theme.Px(60f));
        ImGui.TableSetupColumn("Deck");
        ImGui.TableHeadersRow();
        foreach (var r in results.Reverse().Take(12))
        {
            var color = r.Vetted ? Theme.Good : Theme.Red;
            ImGui.TableNextRow();
            Cell($"{r.Tournament.ToLocalTime():ddd HH:mm}", Theme.Gold);
            Cell($"{r.Rank} ({r.Points} pts)", color);
            Cell(r.Expected is { } e ? $"{e:+0.00;-0.00;0.00}" : "-", Theme.Muted);
            var names = r.Deck.Select(id => this.plugin.Data?.Cards.Get(id)?.Name ?? $"#{id}");
            Cell((r.Advised ? "" : "(not the advice) ") + string.Join(", ", names), r.Advised ? Theme.Text : Theme.Warn);
            Widgets.HoverTooltip(string.Join(", ", r.Rules.Rules.Select(this.RuleName)));
        }

        ImGui.EndTable();
    }

    // Farming: talk, challenge, deck, auto play, rematch until the goal, quit.
    private void DrawSession()
    {
        var session = this.plugin.Session;
        var config = this.plugin.Configuration;
        Widgets.Section("Farm the targeted NPC");
        if (!session.Active)
        {
            var max = config.SessionMaxMatches;
            ImGui.SetNextItemWidth(Theme.Px(110f));
            if (ImGui.InputInt("Matches (0 = until stopped)", ref max))
            {
                config.SessionMaxMatches = max;
                config.Save();
            }

            if (Widgets.Button("Start farming", true))
                this.sessionError = this.plugin.StartSession();
            ImGui.SameLine();
            if (Widgets.Button("Farm until every card drops", true))
                this.sessionError = this.plugin.StartSession(untilCards: true);
            Widgets.HoverTooltip("Farms until every card this NPC drops is in the collection or in the bags, then quits. The match count is not used.");
            Widgets.Muted("Target the NPC and stand next to it.");
            if (this.plugin.FacingRewards() is { } rewards)
            {
                Widgets.Muted(rewards.Cards.Count == 0 ? $"{rewards.Npc} drops no cards." : $"{rewards.Npc} drops ({rewards.Cards.Count(c => !c.Collected)} missing):");
                Widgets.Chips(rewards.Cards.Select(c => (c.Collected ? $"{c.Card} (have)" : c.Card, c.Collected ? Theme.Muted : Theme.Gold)));
            }

            if (this.sessionError is { } error)
                Widgets.Text(error, Theme.Warn);
            if (session.HasRecap)
            {
                ImGui.TextUnformatted($"Last session against {session.NpcName}: {session.MatchesDone} played");
                Widgets.Chips([($"{session.Wins} won", Theme.Good), ($"{session.Draws} drawn", Theme.Gold), ($"{session.Losses} lost", Theme.Red)]);
            }
        }
        else
        {
            ImGui.TextUnformatted($"Farming {session.NpcName}: {Goal(session)}");
            Widgets.Chips([($"{session.Wins} won", Theme.Good), ($"{session.Draws} drawn", Theme.Gold), ($"{session.Losses} lost", Theme.Red)]);
            if (session.UntilCards)
                Widgets.Chips(session.MissingCards.Select(c => (c, Theme.Gold)));
            if (!session.StopRequested && ImGui.Button("Stop after this match"))
                session.StopRequested = true;
            if (!session.StopRequested)
                ImGui.SameLine();
            if (Widgets.DangerButton("Stop now"))
                session.Stop("stopped by the player");
        }

        Widgets.Text(session.Status, session.Active ? Theme.Good : Theme.Muted);
        if ((this.plugin.DeckPrep.Pending ?? (this.plugin.DeckChooser.Running ? "ranking the saved decks against this NPC" : null)) is { } pending)
            Widgets.Busy(pending, Theme.Good);
        if (config.SessionPicksDeck && this.plugin.DeckChooser.Summary.Length > 0)
            Widgets.Muted(this.plugin.DeckChooser.Summary);
        if (config.SessionBuildsDeck && this.plugin.DeckPrep.Summary.Length > 0)
            Widgets.Muted($"Deck building: {this.plugin.DeckPrep.Summary}");
        if (session.LastAction.Length > 0)
            Widgets.Muted($"Last: {session.LastAction}");
    }

    private static string Goal(SessionRunner session)
        => session.UntilCards ? $"until every card drops ({session.MissingCards.Count} missing, {session.MatchesDone} played)"
            : session.MaxMatches == 0 ? "until stopped" : $"{session.MatchesDone}/{session.MaxMatches}";

    // Best decks from the collection against the NPC we face, and how the saved decks compare.
    private void DrawDeckBuilder()
    {
        var decks = this.plugin.Decks;
        Widgets.Section("Deck builder");
        if (decks.Running)
        {
            Widgets.Busy($"Searching against {decks.Npc?.Name}: {decks.Progress}", Theme.Good);
            if (ImGui.SmallButton("Cancel"))
                decks.Cancel();
        }
        else
        {
            if (Widgets.Button("Find the best deck", true))
                this.deckError = this.plugin.StartDeckSearch();
            ImGui.SameLine();
            Widgets.Muted("against the targeted NPC (or the last one played)");
            if ((this.deckError ?? decks.Error) is { } error)
                Widgets.Text(error, Theme.Warn);
        }

        if (decks.Npc is not { } npc)
            return;
        ImGui.TextUnformatted(npc.Name);
        ImGui.SameLine();
        Widgets.Muted($"({decks.RulesSource})");
        Widgets.Chips(decks.Rules.Rules.Select(r => (this.RuleName(r), Theme.Gold)));
        if (decks.Result is not { } result)
            return;
        if (result.CachedAt is { } cachedAt)
            Widgets.Text($"Reused the search of {cachedAt.ToLocalTime():g}: no card obtained since would be tried.", Theme.Good);
        Widgets.Muted($"{result.Status}. Expected = wins minus losses per game against the NPC model (+1 = always win).");
        if (ImGui.BeginTable("decks", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Deck", ImGuiTableColumnFlags.WidthFixed, Theme.Px(80f));
            ImGui.TableSetupColumn("Expected", ImGuiTableColumnFlags.WidthFixed, Theme.Px(60f));
            ImGui.TableSetupColumn("W / D / L", ImGuiTableColumnFlags.WidthFixed, Theme.Px(100f));
            ImGui.TableSetupColumn("Cards (in play order)");
            ImGui.TableHeadersRow();
            for (var i = 0; i < result.Saved.Count && i < decks.Saved.Count; i++)
                DeckRow(decks.Saved[i].Name, result.Saved[i], Theme.Muted);
            for (var i = 0; i < result.Best.Count; i++)
            {
                var score = result.Best[i];
                var same = decks.Saved.FirstOrDefault(d => SameCards(d.Cards, score.Cards));
                DeckRow(same is null ? $"#{i + 1}" : $"#{i + 1} = {same.Name}", score, i == 0 ? Theme.Good : Theme.Text);
            }

            ImGui.EndTable();
        }

        this.DrawDeckWrite(result.Best.Count > 0 ? result.Best[0].Cards : null);
    }

    // Writes the best deck into the slot the settings allow (Gold Saucer deck editor).
    private void DrawDeckWrite(IReadOnlyList<CardInfo>? best)
    {
        var writer = this.plugin.DeckWriter;
        var slot = this.plugin.Configuration.DeckWriteSlot;
        if (writer.Active)
        {
            Widgets.Text($"Writing: {writer.Status}", Theme.Good);
            if (ImGui.SmallButton("Stop writing"))
                writer.Stop("stopped by the player");
            return;
        }

        if (best is not null && slot >= 0)
        {
            var current = this.plugin.Decks.Saved.FirstOrDefault(d => d.Index == slot);
            if (Widgets.Button($"Write #1 to Deck {slot + 1}", true))
                this.writeError = this.plugin.StartDeckWrite(best);
            ImGui.SameLine();
            Widgets.Muted(current is null ? "(empty slot)" : $"(replaces {current.Name}: {string.Join(", ", current.Cards.Select(c => c.Name))})");
        }
        else if (best is not null)
        {
            Widgets.Muted("Build #1 in a free deck slot, or allow a slot in Settings to have it written.");
        }

        if ((this.writeError ?? (writer.Status.Length > 0 ? writer.Status : null)) is { } status)
            Widgets.Text(status, status.StartsWith("done", StringComparison.Ordinal) ? Theme.Good : Theme.Warn);
    }

    private static void DeckRow(string label, Core.Deck.DeckScore score, Vector4 color)
    {
        ImGui.TableNextRow();
        Cell(label, color);
        Cell($"{score.Expected:+0.00;-0.00;0.00}", color);
        Cell($"{score.Win:P0} / {score.Draw:P0} / {score.Loss:P0}", color);
        if (score.PerfectWin is { } pw)
            Widgets.HoverTooltip($"With both hands known, perfect play: {pw:P0} win / {score.PerfectDraw:P0} draw / {score.PerfectLoss:P0} loss over the NPC's hands");
        ImGui.TableNextColumn();
        Widgets.Text(string.Join(", ", score.Cards.Select(c => $"{c.Name} {c.Stars}*")), color);
        Widgets.HoverTooltip(string.Join("\n", score.Cards.Select(c => $"{c.Name}  {c.Stars}*  [{c.Stats}]")));
    }

    private static bool SameCards(IReadOnlyList<CardInfo> a, IReadOnlyList<CardInfo> b)
        => a.Select(c => c.Id).Order().SequenceEqual(b.Select(c => c.Id).Order());

    // Queue for Open Tournaments with the targeted Official and play them through.
    private void DrawTournamentQueue()
    {
        var runner = this.plugin.Tournament;
        var config = this.plugin.Configuration;
        Widgets.Section("Open Tournament queue");
        if (!runner.Active)
        {
            var count = config.TournamentCount;
            ImGui.SetNextItemWidth(Theme.Px(110f));
            if (ImGui.InputInt("Tournaments (0 = until stopped)", ref count))
            {
                config.TournamentCount = Math.Max(0, count);
                config.Save();
            }

            if (Widgets.Button("Queue", true))
                this.tournamentError = this.plugin.StartTournament();
            ImGui.SameLine();
            Widgets.Muted("Target the Open Tournament Official and stand next to it.");
            if (this.tournamentError is { } error)
                Widgets.Text(error, Theme.Warn);
        }
        else
        {
            var goal = runner.MaxTournaments == 0 ? "until stopped" : $"{runner.TournamentsDone}/{runner.MaxTournaments}";
            ImGui.TextUnformatted($"Open Tournament {goal}: {runner.Phase}");
            if (Widgets.DangerButton("Stop queueing"))
                runner.Stop("stopped by the player");
        }

        Widgets.Text(runner.Status, runner.Active ? Theme.Good : Theme.Muted);
        if (runner.LastAction.Length > 0)
            Widgets.Muted($"Last: {runner.LastAction}");
    }

    // The next Open Tournaments (Gold Saucer), computed from the sheet rotation, in local time.
    private void DrawTournaments()
    {
        var rows = this.plugin.Data!.TournamentRules;
        if (rows.Count == 0)
            return;
        Widgets.Section("Schedule");
        if (!ImGui.BeginTable("schedule", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, Theme.Px(80f));
        ImGui.TableSetupColumn("Rules");
        foreach (var t in OpenTournament.Upcoming(rows, DateTimeOffset.UtcNow, 10))
        {
            var advanced = t.Rules.Rules.Count() > 1;
            ImGui.TableNextRow();
            Cell($"{t.StartUtc.ToLocalTime():ddd HH:mm}", Theme.Gold);
            Cell(string.Join(", ", t.Rules.Rules.Select(this.RuleName)), advanced ? Theme.Good : Theme.Muted);
        }

        ImGui.EndTable();
    }

    private void DrawNotes()
    {
        var notes = this.plugin.Tracker.Notes;
        if (notes.Count == 0)
        {
            Widgets.Muted("Nothing yet: the tracker notes every turn and event of the match here.");
            return;
        }

        for (var i = notes.Count - 1; i >= Math.Max(0, notes.Count - 30); i--)
            Widgets.Muted(notes[i]);
    }

    private string RuleName(TriadRule rule) => this.plugin.Data!.RuleNames.GetValueOrDefault(rule, rule.ToString());

    private static bool Hovering(Vector2 min, Vector2 max) => ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);

    private static void Cell(string text, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(color, text);
    }

    private static string Describe(CardStats card)
        => $"up {Widgets.Glyph(card.Up)}, right {Widgets.Glyph(card.Right)}, down {Widgets.Glyph(card.Down)}, left {Widgets.Glyph(card.Left)}"
           + (card.Type == CardType.None ? string.Empty : $", {card.Type}");

    private static string Outcome(double value) => value switch
    {
        >= 0.999 => "win",
        <= -0.999 => "loss",
        > -0.001 and < 0.001 => "draw",
        _ => $"{value:+0.00;-0.00}",
    };

    private static Vector4 OutcomeColor(double value) => value switch
    {
        > 0.001 => Theme.Good,
        < -0.001 => Theme.Red,
        _ => Theme.Gold,
    };
}
