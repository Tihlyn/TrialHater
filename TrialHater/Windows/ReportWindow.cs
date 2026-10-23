using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using TrialHater.Core.Reporting;

namespace TrialHater.Windows;

// Report an issue: what went wrong (pre-selected from the plugin's state), a few words, send. The
// report goes to the private reports repo through the relay (Game/IssueReporter); the window says
// how it went.
public sealed class ReportWindow : Window
{
    private static readonly (ReportCategory Category, string Hint)[] Choices =
    [
        (ReportCategory.Stalled, "Auto play, a farming session or the tournament queue stopped doing anything."),
        (ReportCategory.NoBelief, "Our turn, and the match panel showed a NoBelief warning instead of a move.\n"
                                  + "In an Open Tournament it plays anyway: the line under the move starts with FALLBACK: no belief."),
        (ReportCategory.SolveStalled, "'Solving...' ran far longer than usual, or never finished."),
        (ReportCategory.Other, "Anything else: a bad move, a wrong deck, a window left open... Say what in the box below."),
    ];

    private readonly Plugin plugin;
    private Theme.Scope theme;
    private ReportCategory category;
    private string description = string.Empty;

    public ReportWindow(Plugin plugin)
        : base("Report an issue###TrialHaterReport", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.Size = Theme.ReportSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = Theme.ReportMinimum, MaximumSize = Theme.Maximum };
    }

    // Opens on the category the plugin's state suggests (not while a report is on its way).
    public void Open(ReportCategory suggested)
    {
        if (!this.plugin.Reporter.Busy)
        {
            this.category = suggested;
            if (this.plugin.Reporter.Filed)
                this.description = string.Empty;
        }

        this.IsOpen = true;
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
        if (Chrome.Header("Report an issue", $"TrialHater {Plugin.VersionText}", (FontAwesomeIcon.Times, "Close")) == 0)
            this.IsOpen = false;
        var visible = Chrome.BeginBody("##report");
        if (visible)
            this.DrawForm();
        Chrome.EndBody(visible, min, max);
        Chrome.Footer($"install {this.plugin.Reporter.Fingerprint}");
        Chrome.PopInner();
    }

    private void DrawForm()
    {
        var reporter = this.plugin.Reporter;
        Widgets.Section("What went wrong");
        var selected = (int)this.category;
        foreach (var (choice, hint) in Choices)
        {
            ImGui.RadioButton(choice.Title(), ref selected, (int)choice);
            Widgets.HoverTooltip(hint);
        }

        this.category = (ReportCategory)selected;
        Widgets.Section(this.category == ReportCategory.Other ? "What happened" : "What happened (optional)");
        ImGui.InputTextMultiline("##description", ref this.description, IssueReport.MaxDescription, new Vector2(-1f, Theme.Px(110f)));
        Widgets.Muted("Sent with it: what the plugin is doing, the match on screen, its lines from dalamud.log, your recent matches "
                      + "and your settings. It goes to the private TrialHater reports repo; a copy stays in pluginConfigs/TrialHater/reports.");

        var needsText = this.category == ReportCategory.Other && this.description.Trim().Length == 0;
        ImGui.BeginDisabled(reporter.Busy || needsText);
        if (Widgets.Button(reporter.Busy ? "Sending..." : "Send report", true))
            reporter.Request(this.category, this.description);
        ImGui.EndDisabled();
        if (needsText)
        {
            ImGui.SameLine();
            Widgets.Muted("say what happened first");
        }

        if (reporter.Busy)
            Widgets.Busy(reporter.Status, Theme.Good);
        else if (reporter.Status.Length > 0)
            Widgets.Text(reporter.Status, reporter.Filed ? Theme.Good : Theme.Warn);
    }
}
