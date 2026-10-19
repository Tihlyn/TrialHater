using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TrialHater.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration configuration;

    public ConfigWindow(Configuration configuration)
        : base("TrialHater settings###TrialHaterConfig")
    {
        this.configuration = configuration;
        this.Size = new Vector2(420, 260);
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
