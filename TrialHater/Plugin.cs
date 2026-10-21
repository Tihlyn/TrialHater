using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Policy;
using TrialHater.Core.Search;
using TrialHater.Core.Session;
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
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly ModelProbe probe;
    private readonly MatchJournal journal;
    private long frameCounter;
    private int placedAtMatchStart;
    private bool writtenChecked = true;
    private int draftGeneration;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IDataManager dataManager,
        ITargetManager targets,
        IObjectTable objects,
        ICondition condition,
        ITextureProvider textureProvider)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.framework = framework;
        this.targets = targets;
        this.objects = objects;
        this.condition = condition;
        this.Log = log;

        this.Configuration = Configuration.Load(pluginInterface);
        this.Analysis = new AnalysisService((ex, msg) => log.Error(ex, msg));
        this.AutoPlayer = new AutoPlayer(msg => log.Information(msg)) { Enabled = this.Configuration.AutoPlay };
        this.Session = new SessionRunner(gameGui, targets, objects, condition, msg => log.Information(msg));
        this.IdleGuard = new IdleGuard(msg => log.Information(msg), IdleInput.ReadGameTimers, IdleInput.FindGameWindow,
            IdleInput.SendControl, IdleInput.ControlHeld) { Enabled = this.Configuration.AntiIdle };
        this.probe = new ModelProbe(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "model_probe.jsonl"));
        this.journal = new MatchJournal(pluginInterface.GetPluginConfigDirectory(), (ex, msg) => log.Warning(ex, msg));
        this.Decks = new DeckService((ex, msg) => log.Error(ex, msg));
        this.DeckChooser = new SessionDeckChooser(msg => log.Information(msg));
        this.DeckWriter = new DeckWriter(gameGui, msg => log.Information(msg));
        this.DeckPrep = new SessionDeckPrep(msg => log.Information(msg));
        this.Draft = new DraftService(gameGui, pluginInterface.GetPluginConfigDirectory(), msg => log.Information(msg));
        this.Tournament = new TournamentRunner(gameGui, targets, objects, condition, msg => log.Information(msg));
        this.Tracker.Event += this.OnTrackerEvent;
        this.Tracker.RedMoved += this.journal.RedMoved;
        this.Tracker.MatchEnded += this.OnMatchEnded;

        // ENpcBase is large; the sheets load on a worker and the plugin idles until they are in.
        Task.Run(() =>
        {
            try
            {
                var data = GameData.Load(dataManager);
                this.Session.TriadMenuText = data.TriadMenuText;
                this.Data = data;
                log.Information($"[Data] {data.Cards.Count} cards, {data.Npcs.Count} NPC decks, {data.NpcBaseToTriad.Count} NPCs, {data.RuleNames.Count} rules.");
            }
            catch (Exception ex)
            {
                this.DataError = ex.Message;
                log.Error(ex, "[Data] failed to load the Triple Triad sheets");
            }
        });

        CardArt.Initialize(textureProvider);
        this.WindowSystem = new WindowSystem("TrialHater");
        this.MainWindow = new MainWindow(this);
        this.ConfigWindow = new ConfigWindow(this.Configuration, this.IdleGuard);
        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.MainWindow.IsOpen = this.Configuration.OpenAtStartup;

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

    // "v1.2.3" from the assembly version (Directory.Build.props); the fourth part only when set.
    public static string VersionText { get; } = FormatVersion(typeof(Plugin).Assembly.GetName().Version);

    public IPluginLog Log { get; }

    public Configuration Configuration { get; }

    public GameData? Data { get; private set; }

    public string? DataError { get; private set; }

    public MatchTracker Tracker { get; } = new();

    public AnalysisService Analysis { get; }

    public AutoPlayer AutoPlayer { get; }

    public SessionRunner Session { get; }

    public IdleGuard IdleGuard { get; }

    public DeckService Decks { get; }

    public SessionDeckChooser DeckChooser { get; }

    public DeckWriter DeckWriter { get; }

    public SessionDeckPrep DeckPrep { get; }

    public DraftService Draft { get; }

    public TournamentRunner Tournament { get; }

    public WindowSystem WindowSystem { get; }

    public MainWindow MainWindow { get; }

    public ConfigWindow ConfigWindow { get; }

    public NpcDeck? CurrentNpc => this.Data?.Npcs.Get(this.Tracker.NpcTriadId);

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.IdleGuard.Reset();
        this.Decks.Dispose();
        this.DeckChooser.Dispose();
        this.DeckPrep.Dispose();
        this.Draft.Dispose();
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
        {
            this.IdleGuard.Tick(false, false, DateTime.UtcNow);
            return;
        }

        var board = this.Addon(BoardReader.AddonName);
        var frame = board == null ? null : BoardReader.Read(board, this.frameCounter, this.objects.LocalPlayer?.Name.TextValue);
        var resultVisible = this.IsVisible(ResultAddon);
        if (!this.Tracker.InMatch)
            this.IdentifyNpc(data);
        this.Tracker.Update(frame, resultVisible, data, () => this.SavedDecks(data.Cards));
        this.CheckOpponent(data, frame);

        if (this.Tracker.View is { ToMove: Core.Engine.Side.Blue } view)
            this.Analysis.Request(view, this.CurrentNpc, data.Cards, this.PolicyOptions);
        else if (!this.Tracker.InMatch && this.Analysis.Latest is not null)
            this.Analysis.Clear();

        // Automation acts first and the anti-idle press goes last, so a synthetic keystroke never
        // lands in the moments before a dispatch; automation stands aside while it is held.
        var blocked = this.IdleGuard.BlocksAutomation;
        if (!blocked)
        {
            this.AutoPlayer.Tick(this.Tracker, this.Analysis.Latest, this.Configuration.PlaceDelayMs, DateTime.UtcNow,
                (slot, cell) => BoardActuator.PlaceCard(this.Addon(BoardReader.AddonName), slot, cell));
        }

        if (this.Configuration.AutoPlay != this.AutoPlayer.Enabled)
        {
            // The auto player switches itself off on repeated failures; keep the setting in step.
            this.Configuration.AutoPlay = this.AutoPlayer.Enabled;
            this.Configuration.Save();
        }

        if (this.Session.Active && !this.AutoPlayer.Enabled)
            this.Session.Stop("auto play is off");
        if (!blocked)
            this.DeckWriter.Tick(DateTime.UtcNow);
        this.CheckWrittenDeck(data);
        this.Draft.Tick(data, DateTimeOffset.UtcNow, this.AutoPlayer.Enabled && !blocked,
            this.AutoPlayer.Enabled && !blocked && this.Configuration.AutoDraft);
        if (this.Draft.OfferGeneration != this.draftGeneration)
        {
            // A new draft: a new tournament, with new decks for everyone.
            this.draftGeneration = this.Draft.OfferGeneration;
            this.Tracker.Decks.Clear();
        }
        if (this.DeckPrep.Running && !this.Session.Active)
            this.DeckPrep.Cancel();
        this.DeckPrep.Tick(this.DeckWriter, this.DeckChooser);
        this.Session.Preparing = this.Session.Active ? this.DeckPrep.Pending : null;

        var pickDeck = this.Configuration.SessionPicksDeck;
        this.Session.DeckRow = pickDeck ? this.DeckChooser.Row : -1;
        this.Session.DeckCards = this.DeckChooser.Cards;
        this.Session.DeckPending = pickDeck && this.Session.Active ? this.DeckChooser.Pending : null;
        if (!blocked)
        {
            this.Session.Tick(DateTime.UtcNow, this.Configuration.SessionDelayMs,
                baseId => data.NpcBaseToTriad.TryGetValue(baseId, out var triad) && triad == this.Session.NpcTriadId);
        }

        if (this.Tournament.Active && !this.AutoPlayer.Enabled)
            this.Tournament.Stop("auto play is off");
        if (!blocked)
            this.Tournament.Tick(DateTime.UtcNow, this.Configuration.SessionDelayMs);

        // In a duty the inactivity kick also runs between matches (tournament rounds, queues).
        var inDuty = this.condition[ConditionFlag.BoundByDuty] || this.condition[ConditionFlag.BoundByDuty56] || this.condition[ConditionFlag.BoundByDuty95];
        this.IdleGuard.Tick(this.AutoPlayer.Enabled, this.Tracker.InMatch || this.Session.Active || this.Tournament.Active || inDuty, DateTime.UtcNow);
    }

    // Farms the NPC we are facing.
    public string? StartSession()
    {
        if (this.Data is not { } data)
            return "sheets are still loading";
        if (this.FacingNpc(data) is not { } npc)
            return "target a Triple Triad NPC first";
        if (this.Tournament.Active)
            return "a tournament queue is running";
        this.SetAutoPlay(true);
        this.Session.Start(npc.TriadId, npc.Name, this.Configuration.SessionMaxMatches);
        if (!this.Configuration.SessionPicksDeck)
            return null;

        // Building opens the Gold Saucer window: only from a clean start, before the NPC's menu or
        // any Triad window is up.
        var cleanStart = !this.Tracker.InMatch && !new[] { RequestAddon, "TripleTriadSelDeck", ResultAddon, "Talk", "SelectIconString" }.Any(this.IsVisible);
        if (this.Configuration.SessionBuildsDeck && this.Configuration.DeckWriteSlot >= 0 && cleanStart && !this.DeckWriter.Active)
            this.PrepareSessionDeck(data, npc);
        else
            this.ChooseSessionDeck(data, npc);
        return null;
    }

    // Builds the best deck against the session NPC first (SessionDeckPrep), then plays it.
    private void PrepareSessionDeck(GameData data, NpcDeck npc)
    {
        var owned = Collection.Owned(data.Cards);
        if (owned.Count < 5)
        {
            this.Log.Information("[Deck] session: could not read the card collection; picking among the saved decks");
            this.ChooseSessionDeck(data, npc);
            return;
        }

        var history = this.journal.RulesHistory(npc.TriadId);
        var (rules, _) = DeckRules.ExpectedRules(npc, history);
        this.DeckPrep.Start(npc, rules, history.Count == 0, owned, Collection.SavedDecks(data.Cards), data.Cards,
            this.Configuration.DeckWriteSlot, () => this.ChooseSessionDeck(data, npc));
    }

    // Ranks the saved decks against the session NPC under the rules its recorded matches had.
    private void ChooseSessionDeck(GameData data, NpcDeck npc)
    {
        var history = this.journal.RulesHistory(npc.TriadId);
        var (rules, _) = DeckRules.ExpectedRules(npc, history);
        this.DeckChooser.Start(npc, rules, history.Count == 0, Collection.SavedDecks(data.Cards), data.Cards);
    }

    // Queues for Open Tournaments with the targeted Official; the draft and matches run on auto.
    public string? StartTournament()
    {
        if (this.Data is null)
            return "sheets are still loading";
        if (this.Session.Active)
            return "a farming session is running";
        var error = this.Tournament.Start(this.Configuration.TournamentCount);
        if (error is null)
            this.SetAutoPlay(true);
        return error;
    }

    // Searches the best decks from the collection against the NPC we are facing, under the rules
    // its recorded matches had (sheet rules + regional ones), and scores the saved decks too.
    public string? StartDeckSearch()
    {
        if (this.Data is not { } data)
            return "sheets are still loading";
        if (this.FacingNpc(data) is not { } npc)
            return "target a Triple Triad NPC first";
        var owned = Collection.Owned(data.Cards);
        if (owned.Count < 5)
            return "could not read the card collection";
        this.journal.WriteOwned(owned);
        var (rules, source) = DeckRules.ExpectedRules(npc, this.journal.RulesHistory(npc.TriadId));
        this.Decks.Start(npc, rules, source, owned, Collection.SavedDecks(data.Cards), data.Cards);
        this.Log.Information($"[Deck] searching decks against {npc.Name} [{rules}] ({source}), {owned.Count} owned cards");
        return null;
    }

    // Writes a deck into the slot the settings allow, through the Gold Saucer deck editor.
    public string? StartDeckWrite(IReadOnlyList<CardInfo> deck)
    {
        if (this.Data is not { } data)
            return "sheets are still loading";
        var slot = this.Configuration.DeckWriteSlot;
        if (slot < 0)
            return "no deck slot may be written (Settings)";
        if (this.Tracker.InMatch || this.Session.Active)
            return "not during a match or a session";
        if (this.DeckWriter.Active)
            return "a deck is already being written";
        if (DeckRules.Problem(deck) is { } problem)
            return $"not a legal deck: {problem}";
        var owned = Collection.Owned(data.Cards).Select(c => c.Id).ToHashSet();
        if (deck.FirstOrDefault(c => !owned.Contains(c.Id)) is { } missing)
            return $"{missing.Name} is not in the collection";
        this.writtenChecked = false;
        this.DeckWriter.Start(slot, deck);
        return null;
    }

    // After a write, compare the saved deck in game memory with what was written.
    private void CheckWrittenDeck(GameData data)
    {
        if (this.writtenChecked || this.DeckWriter.Active || this.DeckWriter.Written is not { } written)
            return;
        this.writtenChecked = true;
        var saved = Collection.SavedDecks(data.Cards).FirstOrDefault(d => d.Index == written.Slot);
        var matches = saved is not null && saved.Cards.Select(c => c.Id).SequenceEqual(written.Cards);
        this.Log.Information(matches
            ? $"[DeckWriter] Deck {written.Slot + 1} in game memory matches what was written"
            : $"[DeckWriter] Deck {written.Slot + 1} in game memory is {(saved is null ? "incomplete" : string.Join(",", saved.Cards.Select(c => c.Id)))}, not {string.Join(",", written.Cards)}");
    }

    // The NPC we are facing: the one in the current match, else the current target, else the
    // last match's (the target can be cleared once its menu is open).
    private NpcDeck? FacingNpc(GameData data)
    {
        var triad = this.Tracker.InMatch ? this.Tracker.NpcTriadId : 0;
        if (triad == 0 && this.targets.Target is { } target && data.NpcBaseToTriad.TryGetValue(target.BaseId, out var targeted))
            triad = targeted;
        if (triad == 0)
            triad = this.Tracker.NpcTriadId;
        return triad == 0 ? null : data.Npcs.Get(triad);
    }

    // The board names its opponent (AtkValues[4]). If that is not the NPC we identified from the
    // target - a tournament player, or a stale target from an earlier NPC - look the name up, and
    // fall back to "unknown opponent" rather than reasoning from the wrong deck.
    private void CheckOpponent(GameData data, BoardFrame? frame)
    {
        if (!this.Tracker.InMatch || frame?.OpponentName is not { Length: > 0 } name)
            return;
        if (this.CurrentNpc is { } npc && npc.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return;
        var byName = data.NpcByName(name);
        var triad = byName?.TriadId ?? 0;
        if (triad == this.Tracker.NpcTriadId)
            return;
        this.Tracker.NpcTriadId = triad;
        this.Log.Information(byName is null
            ? $"[Npc] opponent '{name}' has no deck data; hidden cards will be sampled"
            : $"[Npc] opponent '{name}' identified by name (TripleTriad {triad})");
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
        => Collection.SavedDecks(cards).Select(d => d.Cards.Select(c => c.Stats).ToArray()).ToList();

    // autoPlayed = how many of our cards the auto player placed this match.
    private void OnMatchEnded(int blueCards)
    {
        this.journal.MatchEnded(this.Tracker, this.CurrentNpc, blueCards, this.AutoPlayer.Placed - this.placedAtMatchStart);
        this.Session.MatchEnded(blueCards);

        // The first recorded match shows the NPC's regional rules: rank the saved decks again.
        if (this.Session.Active && this.Configuration.SessionPicksDeck && this.DeckChooser.SheetRulesOnly
            && this.Data is { } data && data.Npcs.Get(this.Session.NpcTriadId) is { } npc)
            this.ChooseSessionDeck(data, npc);
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
                break;
        }
    }

    private static string FormatVersion(Version? version) => version is null ? "dev"
        : version.Revision > 0 ? $"v{version}" : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private void OpenMainUi() => this.MainWindow.IsOpen = true;

    private void OpenConfigUi() => this.ConfigWindow.IsOpen = true;
}
