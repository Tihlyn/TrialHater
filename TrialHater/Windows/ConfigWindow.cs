using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using TrialHater.Core.Session;
using TrialHater.Game;

namespace TrialHater.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly string[] Slots = ["None", "Deck 1", "Deck 2", "Deck 3", "Deck 4", "Deck 5"];

    private readonly Configuration configuration;
    private readonly IdleGuard idleGuard;
    private readonly CharacterData character;
    private Theme.Scope theme;

    public ConfigWindow(Configuration configuration, IdleGuard idleGuard, CharacterData character)
        : base("TrialHater settings###TrialHaterConfig", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.idleGuard = idleGuard;
        this.character = character;
        this.Size = Theme.ConfigSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = Theme.ConfigMinimum, MaximumSize = Theme.Maximum };
    }

    public override void PreDraw() => this.theme = new Theme.Scope(this.configuration.Backdrop);

    public override void PostDraw() => this.theme.Dispose();

    public override void Draw()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        Chrome.Backdrop(this.configuration.Backdrop, this.configuration.BackdropOpacity);
        Chrome.PushInner();
        if (Chrome.Header("Settings", $"TrialHater {Plugin.VersionText}", (FontAwesomeIcon.Times, "Close")) == 0)
            this.IsOpen = false;
        var visible = Chrome.BeginBody("##settings");
        var changed = visible && this.DrawSettings();
        Chrome.EndBody(visible, min, max);
        Chrome.Footer("Changes apply and save immediately");
        Chrome.PopInner();
        if (changed)
            this.configuration.Save();
    }

    private bool DrawSettings()
    {
        var c = this.configuration;
        var changed = false;

        Widgets.Section("Window");
        var openAtStartup = c.OpenAtStartup;
        if (Widgets.ToggleRow("Open the main window when the plugin loads", ref openAtStartup,
                "On: TrialHater's window opens with the game (and after every plugin reload).\nOff: open it with /thater or from the plugin installer."))
        {
            c.OpenAtStartup = openAtStartup;
            changed = true;
        }

        ImGui.TextUnformatted("Backdrop");
        foreach (var backdrop in Enum.GetValues<Backdrop>())
        {
            if (Pill(backdrop.ToString(), c.Backdrop == backdrop))
            {
                c.Backdrop = backdrop;
                changed = true;
            }

            ImGui.SameLine();
        }

        ImGui.NewLine();
        var opacity = c.BackdropOpacity;
        Label("Backdrop opacity");
        if (ImGui.SliderFloat("##opacity", ref opacity, 0.3f, 1f, "%.2f"))
        {
            c.BackdropOpacity = opacity;
            changed = true;
        }

        Widgets.Section("Move choice");
        var exploit = c.Exploit;
        if (Widgets.ToggleRow("Exploit the NPC inside the safe moves", ref exploit,
                "Among the moves that keep the best guaranteed result, take the one most likely to win against how NPCs play."))
        {
            c.Exploit = exploit;
            changed = true;
        }

        var epsilon = c.ModelEpsilon;
        Label("Model noise (0 = pure greedy)");
        if (ImGui.SliderFloat("##epsilon", ref epsilon, 0f, 1f, "%.2f"))
        {
            c.ModelEpsilon = epsilon;
            changed = true;
        }

        var budget = c.ExploitBudgetSeconds;
        Label("Exploit time budget (s)");
        if (ImGui.SliderFloat("##budget", ref budget, 0.2f, 15f, "%.1f"))
        {
            c.ExploitBudgetSeconds = budget;
            changed = true;
        }

        var minPlaced = c.ExploitMinPlaced;
        Label("Exploit from N placed cards");
        if (ImGui.SliderInt("##minPlaced", ref minPlaced, 0, 8))
        {
            c.ExploitMinPlaced = minPlaced;
            changed = true;
        }

        Widgets.Section("Auto play");
        var delay = c.PlaceDelayMs;
        Label("Delay before placing (ms)");
        if (ImGui.SliderInt("##delay", ref delay, 0, 4000))
        {
            c.PlaceDelayMs = delay;
            changed = true;
        }

        var antiIdle = this.idleGuard.Enabled;
        if (Widgets.ToggleRow("Prevent inactivity ejection during auto play", ref antiIdle,
                "While auto play runs a match, a session or a duty (Open Tournament, Battlehall), sends a brief\nControl press to the game window when its idle counters exceed 30 seconds."))
        {
            this.idleGuard.Enabled = antiIdle;
            this.idleGuard.Reset();
            c.AntiIdle = antiIdle;
            changed = true;
        }

        Widgets.Muted($"Anti-AFK: {this.idleGuard.Status} ({this.idleGuard.NudgesThisSession} presses)");
        if (this.idleGuard.LastNudgeObservedReset == false)
            Widgets.Text("Last press did not reduce the idle counters", Theme.Warn);

        Widgets.Section("Farming sessions and decks");
        var pickDeck = c.SessionPicksDeck;
        if (Widgets.ToggleRow("Sessions pick the best saved deck for the NPC", ref pickDeck,
                "At session start the saved decks are ranked against the NPC (as in the deck builder),\nand the best one is picked in the deck window. Off: the window's confirm button."))
        {
            c.SessionPicksDeck = pickDeck;
            changed = true;
        }

        var buildDeck = c.SessionBuildsDeck;
        if (Widgets.ToggleRow("Sessions build the best deck first", ref buildDeck,
                $"Before talking to the NPC, a session searches the best deck from your collection and writes it\ninto the slot below when it is not saved yet and beats your saved decks by {Game.SessionDeckPrep.WriteMargin:0.00} or more.\nNeeds 'Sessions pick the best saved deck' and a slot."))
        {
            c.SessionBuildsDeck = buildDeck;
            changed = true;
        }

        var writeSlot = c.DeckWriteSlot + 1;
        Label("Deck slot the plugin may overwrite");
        if (ImGui.Combo("##slot", ref writeSlot, Slots, Slots.Length))
        {
            c.DeckWriteSlot = writeSlot - 1;
            changed = true;
        }

        this.DrawDeckCache();

        Widgets.Section("Open Tournament");
        var autoDraft = c.AutoDraft;
        if (Widgets.ToggleRow("Draft the advised deck", ref autoDraft,
                "With auto play on, presses the draft advisor's option of each pick and confirms the deck,\nbut only if the confirmation shows exactly the advised deck."))
        {
            c.AutoDraft = autoDraft;
            changed = true;
        }

        Widgets.Section("Diagnostics");
        var probe = c.ModelProbe;
        if (Widgets.ToggleRow("Record the game's match model (mapping aid)", ref probe,
                "Dumps the game's own Triple Triad model at every turn to pluginConfigs/TrialHater/model_probe.jsonl."))
        {
            c.ModelProbe = probe;
            changed = true;
        }

        return changed;
    }

    // The logged-in character's deck cache: what it holds, and a way to start over.
    private void DrawDeckCache()
    {
        if (this.character.Cache is not { } cache)
        {
            Widgets.Muted("Deck cache: log in to a character (it is kept per character).");
            return;
        }

        Widgets.Muted($"Deck cache for {this.character.Name}: {cache.Searches} search(es), {cache.Scores} deck score(s).");
        Widgets.HoverTooltip("A deck search against an NPC is reused until a newly obtained card would be among the cards it tries,\n"
                             + "and each deck's score is kept per NPC and rules. Kept per character, since collections differ.");
        if (cache.Searches + cache.Scores > 0 && Widgets.DangerButton("Clear the deck cache"))
            this.character.ClearCache();
    }

    // A label on its own line; the control under it takes the full width.
    private static void Label(string text)
    {
        ImGui.TextUnformatted(text);
        ImGui.SetNextItemWidth(-1f);
    }

    private static bool Pill(string label, bool selected)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Theme.Pressed : Theme.Control);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.GoldBright : Theme.Muted);
        var clicked = ImGui.Button(label, new Vector2(Theme.Px(90f), 0f));
        ImGui.PopStyleColor(2);
        return clicked;
    }
}
