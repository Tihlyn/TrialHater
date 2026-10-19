using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Cards;
using TrialHater.Core.Policy;
using TrialHater.Core.Search;
using TrialHater.Game;
using TrialHater.Windows;

namespace TrialHater;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/thater";
    private const string RequestAddon = "TripleTriadRequest";
    private const string ResultAddon = "TripleTriadResult";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly ITargetManager targets;
    private readonly ModelProbe probe;
    private readonly MatchJournal journal;
    private long frameCounter;
    private int placedAtMatchStart;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IDataManager dataManager,
        ITargetManager targets,
        IObjectTable objects,
        ICondition condition)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.framework = framework;
        this.targets = targets;
        this.Log = log;

        this.Configuration = Configuration.Load(pluginInterface);
        this.Analysis = new AnalysisService((ex, msg) => log.Error(ex, msg));
        this.AutoPlayer = new AutoPlayer(msg => log.Information(msg)) { Enabled = this.Configuration.AutoPlay };
        this.Session = new SessionRunner(gameGui, targets, objects, condition, msg => log.Information(msg));
        this.probe = new ModelProbe(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "model_probe.jsonl"));
        this.journal = new MatchJournal(pluginInterface.GetPluginConfigDirectory(), (ex, msg) => log.Warning(ex, msg));
        this.Tracker.Event += this.OnTrackerEvent;
        this.Tracker.RedMoved += this.journal.RedMoved;
        this.Tracker.MatchEnded += this.OnMatchEnded;

        // ENpcBase is large; the sheets load on a worker and the plugin idles until they are in.
        Task.Run(() =>
        {
            try
            {
                var data = GameData.Load(dataManager);
                this.Data = data;
                log.Information($"[Data] {data.Cards.Count} cards, {data.Npcs.Count} NPC decks, {data.NpcBaseToTriad.Count} NPCs, {data.RuleNames.Count} rules.");
            }
            catch (Exception ex)
            {
                this.DataError = ex.Message;
                log.Error(ex, "[Data] failed to load the Triple Triad sheets");
            }
        });

        this.WindowSystem = new WindowSystem("TrialHater");
        this.MainWindow = new MainWindow(this);
        this.ConfigWindow = new ConfigWindow(this.Configuration);
        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.MainWindow.IsOpen = this.Configuration.ShowOverlay;

        commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle the TrialHater window. '/thater config' opens the settings, '/thater auto on|off' toggles auto play.",
            ShowInHelp = true,
        });

        pluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        framework.Update += this.OnFrameworkUpdate;
    }

    public IPluginLog Log { get; }

    public Configuration Configuration { get; }

    public GameData? Data { get; private set; }

    public string? DataError { get; private set; }

    public MatchTracker Tracker { get; } = new();

    public AnalysisService Analysis { get; }

    public AutoPlayer AutoPlayer { get; }

    public SessionRunner Session { get; }

    public WindowSystem WindowSystem { get; }

    public MainWindow MainWindow { get; }

    public ConfigWindow ConfigWindow { get; }

    public NpcDeck? CurrentNpc => this.Data?.Npcs.Get(this.Tracker.NpcTriadId);

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.Tracker.Event -= this.OnTrackerEvent;
        this.Tracker.RedMoved -= this.journal.RedMoved;
        this.Tracker.MatchEnded -= this.OnMatchEnded;
        this.Analysis.Dispose();
        this.pluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        this.commandManager.RemoveHandler(CommandName);
        this.WindowSystem.RemoveAllWindows();
        this.Configuration.Save();
    }

    public void SetAutoPlay(bool enabled)
    {
        this.AutoPlayer.Enabled = enabled;
        this.Configuration.AutoPlay = enabled;
        this.Configuration.Save();
    }

    public PolicyOptions PolicyOptions => new()
    {
        Exploit = this.Configuration.Exploit,
        ExploitMinPlaced = this.Configuration.ExploitMinPlaced,
        ExploitBudget = TimeSpan.FromSeconds(this.Configuration.ExploitBudgetSeconds),
        Model = new GreedyOpponent(this.Configuration.ModelEpsilon),
    };

    private void OnFrameworkUpdate(IFramework _)
    {
        this.frameCounter++;
        if (this.Data is not { } data)
            return;

        var board = this.Addon(BoardReader.AddonName);
        var frame = board == null ? null : BoardReader.Read(board, this.frameCounter);
        var resultVisible = this.IsVisible(ResultAddon);
        this.IdentifyNpc(data);
        this.Tracker.Update(frame, resultVisible, data, () => this.SavedDecks(data.Cards));

        if (this.Tracker.View is { ToMove: Core.Engine.Side.Blue } view)
            this.Analysis.Request(view, this.CurrentNpc, data.Cards, this.PolicyOptions);
        else if (!this.Tracker.InMatch && this.Analysis.Latest is not null)
            this.Analysis.Clear();

        this.AutoPlayer.Tick(this.Tracker, this.Analysis.Latest, this.Configuration.PlaceDelayMs, DateTime.UtcNow,
            (slot, cell) => BoardActuator.PlaceCard(this.Addon(BoardReader.AddonName), slot, cell));
        if (this.Configuration.AutoPlay != this.AutoPlayer.Enabled)
        {
            // The auto player switches itself off on repeated failures; keep the setting in step.
            this.Configuration.AutoPlay = this.AutoPlayer.Enabled;
            this.Configuration.Save();
        }

        if (this.Session.Active && !this.AutoPlayer.Enabled)
            this.Session.Stop("auto play is off");
        this.Session.Tick(DateTime.UtcNow, this.Configuration.SessionDelayMs,
            baseId => data.NpcBaseToTriad.TryGetValue(baseId, out var triad) && triad == this.Session.NpcTriadId);
    }

    // Farms the NPC we are facing: the one in the current match, else the current target.
    public string? StartSession()
    {
        if (this.Data is not { } data)
            return "sheets are still loading";
        var triad = this.Tracker.NpcTriadId;
        if (triad == 0 && this.targets.Target is { } target && data.NpcBaseToTriad.TryGetValue(target.BaseId, out var targeted))
            triad = targeted;
        if (triad == 0 || data.Npcs.Get(triad) is not { } npc)
            return "target a Triple Triad NPC first";
        this.SetAutoPlay(true);
        this.Session.Start(triad, npc.Name, this.Configuration.SessionMaxMatches);
        return null;
    }

    // The challenge window opens while the NPC is targeted; that target's base id names the
    // TripleTriad row. Kept for the whole match (the target may be cleared during play).
    private void IdentifyNpc(GameData data)
    {
        if (!this.IsVisible(RequestAddon) && this.Tracker.NpcTriadId != 0)
            return;
        if (this.targets.Target is not { } target)
            return;
        if (data.NpcBaseToTriad.TryGetValue(target.BaseId, out var triad) && triad != this.Tracker.NpcTriadId)
        {
            this.Tracker.NpcTriadId = triad;
            this.Log.Information($"[Npc] {data.Npcs.Get(triad)?.Name ?? "?"} (TripleTriad {triad}, ENpcBase {target.BaseId})");
        }
    }

    private IReadOnlyList<CardStats[]> SavedDecks(CardCatalog cards)
    {
        var decks = new List<CardStats[]>();
        var module = GoldSaucerModule.Instance();
        if (module == null)
            return decks;
        for (var i = 0; i < 10; i++)
        {
            var deck = module->GetDeck(i);
            if (deck == null)
                continue;
            var stats = new List<CardStats>(5);
            foreach (var id in deck->Cards)
                if (cards.Get(id) is { } card)
                    stats.Add(card.Stats);
            if (stats.Count == 5)
                decks.Add([.. stats]);
        }

        return decks;
    }

    // autoPlayed = how many of our cards the auto player placed this match.
    private void OnMatchEnded(int blueCards)
    {
        this.journal.MatchEnded(this.Tracker, this.CurrentNpc, blueCards, this.AutoPlayer.Placed - this.placedAtMatchStart);
        this.Session.MatchEnded(blueCards);
        this.placedAtMatchStart = this.AutoPlayer.Placed;
    }

    private void OnTrackerEvent(string text)
    {
        this.Log.Debug($"[Match] {text}");
        if (!this.Configuration.ModelProbe)
            return;
        try
        {
            this.probe.Capture(text, this.Tracker);
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, "[Probe] capture failed");
        }
    }

    private AtkUnitBase* Addon(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
    }

    private bool IsVisible(string name)
    {
        var addon = this.Addon(name);
        return addon != null && addon->IsVisible;
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "config":
                this.ConfigWindow.IsOpen = true;
                break;
            case "auto":
                var on = args.Length > 1 ? args[1].Equals("on", StringComparison.OrdinalIgnoreCase) : !this.AutoPlayer.Enabled;
                this.SetAutoPlay(on);
                this.Log.Information($"[Auto] auto play {(on ? "on" : "off")}");
                break;
            default:
                this.MainWindow.Toggle();
                this.Configuration.ShowOverlay = this.MainWindow.IsOpen;
                this.Configuration.Save();
                break;
        }
    }

    private void OpenMainUi() => this.MainWindow.IsOpen = true;

    private void OpenConfigUi() => this.ConfigWindow.IsOpen = true;
}
