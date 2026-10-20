using Dalamud.Configuration;
using Dalamud.Plugin;

namespace TrialHater;

public sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;

    public bool ShowOverlay { get; set; } = true;

    // Places the recommended card on our turn. Off by default and off again after every reload
    // unless the user turns it on; persisted only so a hot reload mid-match keeps going.
    public bool AutoPlay { get; set; }

    // Opponent model inside the safe set (docs/RESEARCH.md): greedy with this much noise.
    public float ModelEpsilon { get; set; } = 0.1f;

    public bool Exploit { get; set; } = true;

    // Below this many placed cards the move is minimax only (exploiting the opening is slow).
    public int ExploitMinPlaced { get; set; } = 1;

    public float ExploitBudgetSeconds { get; set; } = 3f;

    // A short pause between our turn starting and the card going down, so the board animation
    // has settled and the play does not look instantaneous.
    public int PlaceDelayMs { get; set; } = 900;

    // Farming session: matches before quitting (0 = until stopped), and the pause before each
    // window action so the window has finished appearing.
    public int SessionMaxMatches { get; set; } = 10;

    public int SessionDelayMs { get; set; } = 1200;

    // Read the game's idle counters and send a brief Control press while auto play runs
    // unattended, so a duty (Open Tournament, Battlehall) does not eject us for inactivity.
    public bool AntiIdle { get; set; } = true;

    // Raw dumps of the game's own Triple Triad model at every turn, for mapping it
    // (pluginConfigs/TrialHater/model_probe.jsonl).
    public bool ModelProbe { get; set; }

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.pluginInterface = pluginInterface;
        config.Clamp();
        return config;
    }

    public void Save()
    {
        this.Clamp();
        this.pluginInterface?.SavePluginConfig(this);
    }

    private void Clamp()
    {
        this.ModelEpsilon = Math.Clamp(this.ModelEpsilon, 0f, 1f);
        this.ExploitMinPlaced = Math.Clamp(this.ExploitMinPlaced, 0, 9);
        this.ExploitBudgetSeconds = Math.Clamp(this.ExploitBudgetSeconds, 0.2f, 30f);
        this.PlaceDelayMs = Math.Clamp(this.PlaceDelayMs, 0, 10000);
        this.SessionMaxMatches = Math.Clamp(this.SessionMaxMatches, 0, 999);
        this.SessionDelayMs = Math.Clamp(this.SessionDelayMs, 300, 10000);
    }
}
