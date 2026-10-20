using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TrialHater.Core.Session;

namespace TrialHater.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private readonly IdleGuard idleGuard;

    public ConfigWindow(Configuration configuration, IdleGuard idleGuard)
        : base("TrialHater settings###TrialHaterConfig")
    {
        this.configuration = configuration;
        this.idleGuard = idleGuard;
        this.Size = new Vector2(420, 320);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var c = this.configuration;
        var changed = false;

        ImGui.TextUnformatted("Move choice");
        var exploit = c.Exploit;
        if (ImGui.Checkbox("Exploit the NPC inside the safe moves", ref exploit))
        {
            c.Exploit = exploit;
            changed = true;
        }

        var epsilon = c.ModelEpsilon;
        if (ImGui.SliderFloat("Model noise (0 = pure greedy)", ref epsilon, 0f, 1f, "%.2f"))
        {
            c.ModelEpsilon = epsilon;
            changed = true;
        }

        var budget = c.ExploitBudgetSeconds;
        if (ImGui.SliderFloat("Exploit time budget (s)", ref budget, 0.2f, 15f, "%.1f"))
        {
            c.ExploitBudgetSeconds = budget;
            changed = true;
        }

        var minPlaced = c.ExploitMinPlaced;
        if (ImGui.SliderInt("Exploit from N placed cards", ref minPlaced, 0, 8))
        {
            c.ExploitMinPlaced = minPlaced;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Auto play");
        var delay = c.PlaceDelayMs;
        if (ImGui.SliderInt("Delay before placing (ms)", ref delay, 0, 4000))
        {
            c.PlaceDelayMs = delay;
            changed = true;
        }

        var pickDeck = c.SessionPicksDeck;
        if (ImGui.Checkbox("Sessions pick the best saved deck for the NPC", ref pickDeck))
        {
            c.SessionPicksDeck = pickDeck;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("At session start the saved decks are ranked against the NPC (as in the deck builder),\nand the best one is picked in the deck window. Off: the window's confirm button.");

        var buildDeck = c.SessionBuildsDeck;
        if (ImGui.Checkbox("Sessions build the best deck first", ref buildDeck))
        {
            c.SessionBuildsDeck = buildDeck;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Before talking to the NPC, a session searches the best deck from your collection and writes it\ninto the slot below when it is not saved yet and beats your saved decks by {Game.SessionDeckPrep.WriteMargin:0.00} or more.\nNeeds 'Sessions pick the best saved deck' and a slot.");

        var writeSlot = c.DeckWriteSlot + 1;
        string[] slots = ["None", "Deck 1", "Deck 2", "Deck 3", "Deck 4", "Deck 5"];
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Deck slot the plugin may overwrite", ref writeSlot, slots, slots.Length))
        {
            c.DeckWriteSlot = writeSlot - 1;
            changed = true;
        }

        var autoDraft = c.AutoDraft;
        if (ImGui.Checkbox("Draft the advised deck in Open Tournaments", ref autoDraft))
        {
            c.AutoDraft = autoDraft;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("With auto play on, presses the draft advisor's option of each pick and confirms the deck,\nbut only if the confirmation shows exactly the advised deck.");

        var antiIdle = this.idleGuard.Enabled;
        if (ImGui.Checkbox("Prevent inactivity ejection during auto play", ref antiIdle))
        {
            this.idleGuard.Enabled = antiIdle;
            this.idleGuard.Reset();
            c.AntiIdle = antiIdle;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("While auto play runs a match, a session or a duty (Open Tournament, Battlehall), sends a brief\nControl press to the game window when its idle counters exceed 30 seconds.");
        ImGui.TextDisabled($"Anti-AFK: {this.idleGuard.Status} ({this.idleGuard.NudgesThisSession} presses)");
        if (this.idleGuard.LastNudgeObservedReset == false)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Last press did not reduce the idle counters");

        ImGui.Separator();
        var probe = c.ModelProbe;
        if (ImGui.Checkbox("Record the game's match model (mapping aid)", ref probe))
        {
            c.ModelProbe = probe;
            changed = true;
        }

        if (changed)
            c.Save();
    }
}
