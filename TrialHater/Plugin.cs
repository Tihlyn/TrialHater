using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;
using TrialHater.Core.Search;
using TrialHater.Core.Session;
using TrialHater.Game;
using TrialHater.Windows;

namespace TrialHater;

public sealed unsafe class Plugin : IDalamudPlugin
{
    // Under Chaos the model may choose among moves within ChaosModelBand of the best expected value:
    // a quarter fewer losses against the fitted NPC model in the arena, never worse against other
    // models, within noise against a perfect opponent (docs/RESEARCH.md §11-12). Other rules keep
    // exploitation to exact ties until the arena says otherwise.
    public const double ChaosModelBand = 0.1;

    // Regional rules change about once a day; more rebuilds than this in one session means the
    // challenge window reads differently than expected, and the saved decks are ranked instead.
    private const int MaxSessionRebuilds = 2;

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
    private readonly IPlayerState playerState;
    private readonly ModelProbe probe;
    private readonly MatchJournal journal;

    // The rules each NPC's challenge window showed last (sheet + regional, Roulette not rolled).
    private readonly Dictionary<uint, (RuleSet Rules, DateTimeOffset Seen)> challengeRules = new();

    private long frameCounter;
    private int placedAtMatchStart;
    private bool writtenChecked = true;
    private int draftGeneration;

    // The rules the session's deck was built or ranked for, and the rules to build it for again once
    // the challenge that showed them is declined.
    private RuleSet? sessionDeckRules;
    private RuleSet? rebuildFor;
    private int sessionRebuilds;

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
        ITextureProvider textureProvider,
        IPlayerState playerState)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.framework = framework;
        this.targets = targets;
        this.objects = objects;
        this.condition = condition;
        this.playerState = playerState;
        this.Log = log;

        this.Configuration = Configuration.Load(pluginInterface);
        this.Analysis = new AnalysisService((ex, msg) => log.Error(ex, msg));
        this.AutoPlayer = new AutoPlayer(msg => log.Information(msg)) { Enabled = this.Configuration.AutoPlay };
        this.Session = new SessionRunner(gameGui, targets, objects, condition, msg => log.Information(msg));
        this.IdleGuard = new IdleGuard(msg => log.Information(msg), IdleInput.ReadGameTimers, IdleInput.FindGameWindow,
            IdleInput.SendControl, IdleInput.ControlHeld) { Enabled = this.Configuration.AntiIdle };
        this.probe = new ModelProbe(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "model_probe.jsonl"));
        this.journal = new MatchJournal(pluginInterface.GetPluginConfigDirectory(), (ex, msg) => log.Warning(ex, msg));
        this.Character = new CharacterData(pluginInterface.GetPluginConfigDirectory(), msg => log.Information(msg));
        this.Solved = new SolveCacheFile(pluginInterface.GetPluginConfigDirectory(), msg => log.Information(msg));
        this.Decks = new DeckService((ex, msg) => log.Error(ex, msg));
        this.DeckChooser = new SessionDeckChooser(msg => log.Information(msg));
        this.DeckWriter = new DeckWriter(gameGui, msg => log.Information(msg));
        this.DeckPrep = new SessionDeckPrep(msg => log.Information(msg));
        this.Draft = new DraftService(gameGui, pluginInterface.GetPluginConfigDirectory(), this.Character, msg => log.Information(msg));
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
                log.Information($"[Data] {data.Cards.Count} cards, {data.Npcs.Count} NPC decks ({data.Npcs.All.Count(n => n.Rewards.Count > 0)} dropping cards), {data.NpcBaseToTriad.Count} NPCs, {data.RuleNames.Count} rules.");
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
        this.ConfigWindow = new ConfigWindow(this.Configuration, this.IdleGuard, this.Character);
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

    // Per character: the deck cache and our tournament drafts.
    public CharacterData Character { get; }

    // Solved early positions (openings), reused when farming deals them again.
    public SolveCacheFile Solved { get; }

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
        this.Character.Dispose();
        this.Solved.Dispose();
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

    public PolicyOptions PolicyOptionsFor(MatchView view) => new()
    {
        Exploit = this.Configuration.Exploit,
        ExploitMinPlaced = this.Configuration.ExploitMinPlaced,
        ExploitBudget = TimeSpan.FromSeconds(this.Configuration.ExploitBudgetSeconds),
        Model = new GreedyOpponent(this.Configuration.ModelEpsilon),
        ModelBand = view.Rules.Has(TriadRule.Chaos) ? ChaosModelBand : 0,
        Cache = this.Solved.Cache,
    };

    private void OnFrameworkUpdate(IFramework _)
    {
        this.frameCounter++;
        if (this.Data is not { } data)
        {
            this.IdleGuard.Tick(false, false, DateTime.UtcNow);
            return;
        }

        this.Character.Update(this.playerState.IsLoaded ? this.playerState.ContentId : 0, this.playerState.CharacterName, data.Cards, DateTime.UtcNow);
        this.Solved.Tick(DateTime.UtcNow);
        var board = this.Addon(BoardReader.AddonName);
        var frame = board == null ? null : BoardReader.Read(board, this.frameCounter, this.objects.LocalPlayer?.Name.TextValue);
        var resultVisible = this.IsVisible(ResultAddon);
        if (!this.Tracker.InMatch)
            this.IdentifyNpc(data);
        this.Tracker.Update(frame, resultVisible, data, () => this.SavedDecks(data.Cards));
        if (this.Tracker.InMatch && !this.Session.Active)
            this.Session.ClearRecap();
        this.CheckOpponent(data, frame);

        if (this.Tracker.View is { ToMove: Core.Engine.Side.Blue } view)
            this.Analysis.Request(view, this.CurrentNpc, data.Cards, this.PolicyOptionsFor(view));
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
        this.CheckChallengeRules(data);
        this.Session.Preparing = !this.Session.Active ? null
            : this.DeckPrep.Pending ?? (this.rebuildFor is { } r ? $"building a deck for [{r}] once the NPC's windows close" : null);
        this.Session.RebuildDeck = this.rebuildFor is { } rebuild ? $"its rules [{rebuild}] need another deck; declining to build it" : null;

        var pickDeck = this.Configuration.SessionPicksDeck;
        this.Session.DeckRow = pickDeck ? this.DeckChooser.Row : -1;
        this.Session.DeckCards = this.DeckChooser.Cards;
        this.Session.DeckPending = pickDeck && this.Session.Active ? this.DeckChooser.Pending : null;
        if (this.Session is { Active: true, UntilCards: true } && data.Npcs.Get(this.Session.NpcTriadId) is { } farmed)
        {
            this.Session.UpdateMissing(MissingRewards(data, farmed));
            if (!this.Session.StopRequested && this.Session.MissingCards.Count > 0 && Collection.FreeBagSlots() == 0)
            {
                this.Session.StopRequested = true;
                this.Log.Warning("[Session] the bags are full; stopping after this match so a dropped card has room");
            }
        }

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

    // Farms the NPC we are facing: for the set number of matches, or (untilCards) until every card
    // it drops is collected.
    public string? StartSession(bool untilCards = false)
    {
        if (this.Data is not { } data)
            return "sheets are still loading";
        if (this.FacingNpc(data) is not { } npc)
            return "target a Triple Triad NPC first";
        if (this.Tournament.Active)
            return "a tournament queue is running";
        var missing = untilCards ? MissingRewards(data, npc) : null;
        if (untilCards && npc.Rewards.Count == 0)
            return $"{npc.Name} drops no cards";
        if (missing is { Count: 0 })
            return $"every card {npc.Name} drops is already collected";
        if (untilCards && Collection.FreeBagSlots() == 0)
            return "the bags are full: a dropped card needs a free slot";
        this.SetAutoPlay(true);
        this.Session.Start(npc.TriadId, npc.Name, this.Configuration.SessionMaxMatches, missing);
        this.sessionDeckRules = null;
        this.rebuildFor = null;
        this.sessionRebuilds = 0;
        if (!this.Configuration.SessionPicksDeck)
            return null;

        // The first challenge window checks these rules (CheckChallengeRules).
        var (rules, source) = this.RulesFor(npc);
        this.Log.Information($"[Deck] session: expecting [{rules}] ({source})");
        if (this.SessionBuildsDecks && this.ClearForDeckBuilding() && !this.DeckWriter.Active)
            this.PrepareSessionDeck(data, npc, rules);
        else
            this.ChooseSessionDeck(data, npc, rules);
        return null;
    }

    // The NPC's reward cards not collected yet, by name.
    private static List<string> MissingRewards(GameData data, NpcDeck npc)
        => npc.Rewards.Where(r => !Collection.Collected(r)).Select(r => data.Cards.Get(r.CardId)?.Name ?? $"#{r.CardId}").ToList();

    // The facing NPC's reward cards and whether each is collected, for the farming panel.
    public (string Npc, List<(string Card, bool Collected)> Cards)? FacingRewards()
    {
        if (this.Data is not { } data || this.FacingNpc(data) is not { } npc)
            return null;
        return (npc.Name, npc.Rewards.Select(r => (data.Cards.Get(r.CardId)?.Name ?? $"#{r.CardId}", Collection.Collected(r))).ToList());
    }

    // Builds the best deck against the session NPC under `rules` first (SessionDeckPrep), then plays it.
    private void PrepareSessionDeck(GameData data, NpcDeck npc, RuleSet rules)
    {
        var owned = Collection.Owned(data.Cards);
        if (owned.Count < 5)
        {
            this.Log.Information("[Deck] session: could not read the card collection; picking among the saved decks");
            this.ChooseSessionDeck(data, npc, rules);
            return;
        }

        this.sessionDeckRules = rules;
        this.DeckPrep.Start(npc, rules, owned, Collection.SavedDecks(data.Cards), data.Cards, this.Character.Cache,
            this.Configuration.DeckWriteSlot, () => this.ChooseSessionDeck(data, npc, rules));
    }

    // Ranks the saved decks against the session NPC under `rules`.
    private void ChooseSessionDeck(GameData data, NpcDeck npc, RuleSet rules)
    {
        this.sessionDeckRules = rules;
        this.DeckChooser.Start(npc, rules, Collection.SavedDecks(data.Cards), data.Cards, this.Character.Cache);
    }

    // The rules a match against `npc` will have (Roulette not rolled yet): those its challenge
    // window showed last, else the ones its recorded matches suggest.
    private (RuleSet Rules, string Source) RulesFor(NpcDeck npc)
        => this.challengeRules.TryGetValue(npc.TriadId, out var shown)
            ? (shown.Rules, $"shown by its challenge window at {shown.Seen.ToLocalTime():HH:mm}")
            : DeckRules.ExpectedRules(npc, this.journal.RulesHistory(npc.TriadId));

    private bool SessionBuildsDecks => this.Configuration.SessionBuildsDeck && this.Configuration.DeckWriteSlot >= 0;

    // Building a deck opens the Gold Saucer window: only while no match, NPC menu or Triad window is up.
    private bool ClearForDeckBuilding()
        => !this.Tracker.InMatch && !new[] { RequestAddon, "TripleTriadSelDeck", ResultAddon, "Talk", "SelectIconString" }.Any(this.IsVisible);

    // The challenge window shows the rules in force before every match, rematches included. A
    // session deck chosen for other rules is chosen again for these: the saved decks are ranked
    // again before the deck window, or, when the session builds decks, the challenge is declined and
    // the deck built and written first, since a rematch only offers the saved decks.
    private void CheckChallengeRules(GameData data)
    {
        if (!this.Session.Active)
            this.rebuildFor = null;
        if (this.Tracker.NpcTriadId != 0 && SessionRunner.RequestRules(this.Addon(RequestAddon)) is { } shown)
        {
            var triad = this.Tracker.NpcTriadId;
            if (!this.challengeRules.TryGetValue(triad, out var last) || last.Rules != shown)
                this.Log.Information($"[Npc] {data.Npcs.Get(triad)?.Name ?? "?"}: the challenge window shows [{shown}]");
            this.challengeRules[triad] = (shown, DateTimeOffset.UtcNow);
            if (this.Session.Active && triad == this.Session.NpcTriadId && this.rebuildFor is null
                && this.sessionDeckRules is { } chosen && DeckRules.ForEvaluation(chosen) != DeckRules.ForEvaluation(shown)
                && data.Npcs.Get(triad) is { } npc)
            {
                var rebuild = this.SessionBuildsDecks && this.sessionRebuilds < MaxSessionRebuilds;
                this.Log.Information($"[Deck] session: the deck was chosen for [{chosen}], the challenge shows [{shown}]"
                                     + (rebuild ? "; declining it to build a deck for these rules" : "; ranking the saved decks again")
                                     + (this.SessionBuildsDecks && !rebuild ? $" (already rebuilt {this.sessionRebuilds} times this session)" : string.Empty));
                if (rebuild)
                {
                    this.rebuildFor = shown;
                    this.sessionRebuilds++;
                }
                else
                {
                    this.ChooseSessionDeck(data, npc, shown);
                }
            }
        }

        if (this.rebuildFor is { } rules && !this.DeckWriter.Active && this.ClearForDeckBuilding()
            && data.Npcs.Get(this.Session.NpcTriadId) is { } farmed)
        {
            this.rebuildFor = null;
            this.PrepareSessionDeck(data, farmed, rules);
        }
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
    // its challenge window showed last or its recorded matches had (sheet rules + regional ones),
    // and scores the saved decks too.
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
        var (rules, source) = this.RulesFor(npc);
        this.Decks.Start(npc, rules, source, owned, Collection.SavedDecks(data.Cards), data.Cards, this.Character.Cache);
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
