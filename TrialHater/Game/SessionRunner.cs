using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Session;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TrialHater.Game;

// Runs a farming session against one NPC: talk to it, challenge, confirm the deck, let the auto
// player play, then rematch until the goal is reached and quit. SessionPlanner decides; this class
// reads the windows for it and performs the one action it names. Framework thread only.
//
// Every action is taken once per window appearance, after the window has settled. If the window
// is still showing the same thing RetryAfter later, the action is sent once more; a second silence
// stops the session (dispatched is not accepted).
public sealed unsafe class SessionRunner
{
    // Node ids and the menu icon measured 2026-10-19 (docs/MODEL_MAPPING.md).
    private const uint ResultRematchNode = 20;
    private const uint ResultQuitNode = 21;
    private const uint RequestChallengeNode = 41;
    private const uint RequestQuitNode = 42;
    private const uint DeckConfirmNode = 3;
    // The "Triple Triad Challenge" entry's icon varies by NPC: 71101 at Malevolent Weasel, 71102 at
    // the hall overseer (2026-10-19). Probably the same marker in different states; the
    // neighbouring ids are accepted too, and a miss leaves the menu to the player with a status.
    private const int TriadMenuIconFirst = 71101;
    private const int TriadMenuIconLast = 71104;
    private const float InteractRange = 4f;

    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InteractRetryAfter = TimeSpan.FromSeconds(6);

    private static readonly ConditionFlag[] BusyFlags =
    [
        ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
        ConditionFlag.Casting, ConditionFlag.InCombat,
    ];

    private readonly IGameGui gameGui;
    private readonly ITargetManager targets;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly Action<string> log;
    private readonly Dictionary<string, (bool Visible, int Run)> appearances = new();
    private string currentKey = string.Empty;
    private DateTime keySeenAt;
    private DateTime? actedAt;
    private int attempts;

    public SessionRunner(IGameGui gameGui, ITargetManager targets, IObjectTable objects, ICondition condition, Action<string> log)
    {
        this.gameGui = gameGui;
        this.targets = targets;
        this.objects = objects;
        this.condition = condition;
        this.log = log;
    }

    public bool Active { get; private set; }

    public uint NpcTriadId { get; private set; }

    public string NpcName { get; private set; } = string.Empty;

    public int MaxMatches { get; set; } = 10;

    public bool StopRequested { get; set; }

    public int MatchesDone { get; private set; }

    public int Wins { get; private set; }

    public int Draws { get; private set; }

    public int Losses { get; private set; }

    public string Status { get; private set; } = "off";

    // Set by the plugin each frame: the saved deck to pick (row and its five card ids), or why to
    // wait for it. Row -1 = the window's confirm button.
    public int DeckRow { get; set; } = -1;

    public IReadOnlyList<ushort> DeckCards { get; set; } = [];

    public string? DeckPending { get; set; }

    public string LastAction { get; private set; } = string.Empty;

    public void Start(uint npcTriadId, string npcName, int maxMatches)
    {
        this.Active = true;
        this.NpcTriadId = npcTriadId;
        this.NpcName = npcName;
        this.MaxMatches = maxMatches;
        this.StopRequested = false;
        this.MatchesDone = this.Wins = this.Draws = this.Losses = 0;
        this.currentKey = string.Empty;
        this.Status = "starting";
        this.log($"[Session] started against {npcName} ({npcTriadId}), {(maxMatches == 0 ? "until stopped" : $"{maxMatches} match(es)")}");
    }

    public void Stop(string why)
    {
        if (!this.Active)
            return;
        this.Active = false;
        this.Status = $"stopped: {why}";
        this.log($"[Session] {this.Status}; {this.MatchesDone} played: {this.Wins}W {this.Draws}D {this.Losses}L");
    }

    public void MatchEnded(int blueCards)
    {
        if (!this.Active)
            return;
        this.MatchesDone++;
        if (blueCards > 5)
            this.Wins++;
        else if (blueCards == 5)
            this.Draws++;
        else
            this.Losses++;
    }

    public void Tick(DateTime now, int delayMs, Func<uint, bool> isSessionNpc)
    {
        // Appearance counters are kept even while idle so a session started mid-flow keys correctly.
        var snapshot = this.Read(isSessionNpc);
        if (!this.Active)
            return;

        var action = SessionPlanner.Next(snapshot, new SessionGoal(this.MaxMatches, this.StopRequested), this.MatchesDone, sessionStarted: this.MatchesDone > 0 || this.actedAt is not null);
        if (action.Kind == SessionActionKind.Done)
        {
            this.Stop(action.Reason);
            return;
        }

        if (action.Kind == SessionActionKind.Wait)
        {
            this.Status = action.Reason;
            return;
        }

        var key = $"{action.Kind}|{action.Window}|{action.Key}";
        if (key != this.currentKey)
        {
            this.currentKey = key;
            this.keySeenAt = now;
            this.actedAt = null;
            this.attempts = 0;
        }

        if (now - this.keySeenAt < TimeSpan.FromMilliseconds(delayMs))
        {
            this.Status = $"{action.Reason}: settling";
            return;
        }

        if (this.actedAt is { } sent)
        {
            var retry = action.Kind == SessionActionKind.Interact ? InteractRetryAfter : RetryAfter;
            if (now - sent < retry)
            {
                this.Status = $"{action.Reason}: waiting for the game";
                return;
            }

            if (this.attempts >= 2)
            {
                this.Stop($"the game ignored '{action.Kind}' twice ({this.LastAction})");
                return;
            }
        }

        var result = this.Perform(action);
        this.LastAction = $"{action.Kind}: {result.Detail}";
        if (!result.Sent)
        {
            this.Status = $"{action.Reason}: refused ({result.Detail})";
            return;
        }

        this.actedAt = now;
        this.attempts++;
        this.Status = action.Reason;
        this.log($"[Session] {this.LastAction} ({action.Reason}{(this.attempts > 1 ? ", retry" : string.Empty)})");
    }

    private DispatchResult Perform(SessionAction action) => action.Kind switch
    {
        SessionActionKind.Rematch => AddonOps.ActivateButton(this.Addon("TripleTriadResult"), ResultRematchNode),
        SessionActionKind.Quit => AddonOps.ActivateButton(this.Addon("TripleTriadResult"), ResultQuitNode),
        SessionActionKind.Challenge => AddonOps.ActivateButton(this.Addon("TripleTriadRequest"), RequestChallengeNode),
        SessionActionKind.DeclineRequest => AddonOps.ActivateButton(this.Addon("TripleTriadRequest"), RequestQuitNode),
        SessionActionKind.ConfirmDeck when action.Index >= 0 => this.PickDeck(action.Index),
        SessionActionKind.ConfirmDeck => AddonOps.ActivateButton(this.Addon("TripleTriadSelDeck"), DeckConfirmNode),
        SessionActionKind.AdvanceTalk => AddonOps.Click(this.Addon("Talk")),
        SessionActionKind.ChooseMenu => AddonOps.Callback(this.Addon("SelectIconString"), true, action.Index),
        SessionActionKind.Interact => this.Interact(),
        _ => DispatchResult.Refused($"nothing to do for {action.Kind}"),
    };

    // Clicking a deck row sends callback [row] and starts the match with that deck (2026-10-20:
    // [1] played Deck 2). The window lists each row as (valid, name, 5 x 88000 + card id) in
    // stride 7 from [2]; the row must hold exactly the chosen deck, or nothing is sent.
    private DispatchResult PickDeck(int row)
    {
        var addon = this.Addon("TripleTriadSelDeck");
        if (addon == null)
            return DispatchResult.Refused("deck window gone");
        var shown = Enumerable.Range(0, 5).Select(i => IntValue(addon, 4 + (7 * row) + i) - 88000).ToList();
        if (!shown.SequenceEqual(this.DeckCards.Select(id => (int)id)))
            return DispatchResult.Refused($"row {row} shows cards {string.Join(",", shown)}, not the chosen deck {string.Join(",", this.DeckCards)}");
        return AddonOps.Callback(addon, true, row);
    }

    private DispatchResult Interact()
    {
        if (this.targets.Target is not { } target)
            return DispatchResult.Refused("no target");
        var system = TargetSystem.Instance();
        if (system == null)
            return DispatchResult.Refused("no target system");
        system->InteractWithObject((GameObject*)target.Address, true);
        return new DispatchResult(true, $"interact with {target.Name}");
    }

    private SessionSnapshot Read(Func<uint, bool> isSessionNpc)
    {
        var board = this.Ready("TripleTriad");
        var result = this.Ready("TripleTriadResult");
        var request = this.Ready("TripleTriadRequest");
        var deck = this.Ready("TripleTriadSelDeck");
        var talk = this.Ready("Talk");
        var menu = this.Ready("SelectIconString");
        var yesNo = this.Ready("SelectYesno");

        // The result window stays up after a rematch is requested; it is actionable only while
        // its buttons are.
        var resultActionable = result != null
            && (Actionable(result, ResultRematchNode) || Actionable(result, ResultQuitNode));

        var player = this.objects.LocalPlayer;
        var target = this.targets.Target;
        var canInteract = player is not null && target is not null && isSessionNpc(target.BaseId)
            && Vector3Distance(player.Position, target.Position) <= InteractRange;

        return new SessionSnapshot
        {
            BoardOpen = board != null,
            ResultOpen = resultActionable,
            MatchWindowVisible = this.Visible("TripleTriad") || this.Visible("TripleTriadResult")
                                 || this.Visible("TripleTriadRequest") || this.Visible("TripleTriadSelDeck"),
            ResultKey = $"{this.Run("TripleTriadResult", result != null)}:{StringValue(result, 3)}",
            DeckSelectOpen = deck != null && Actionable(deck, DeckConfirmNode),
            DeckRow = this.DeckRow,
            DeckPending = this.DeckPending,
            RequestOpen = request != null && Actionable(request, RequestChallengeNode),
            RequestKey = this.Run("TripleTriadRequest", request != null).ToString(),
            // Only the session NPC's lines (Talk[1] is the speaker; some Triad lines leave it empty).
            TalkOpen = talk != null && (StringValue(talk, 1) is not { Length: > 0 } speaker
                                        || speaker.Equals(this.NpcName, StringComparison.OrdinalIgnoreCase)),
            TalkKey = $"{this.Run("Talk", talk != null)}:{StringValue(talk, 0)}",
            MenuOpen = menu != null,
            MenuTriadIndex = menu == null ? -1 : TriadMenuIndex(menu),
            MenuKey = this.Run("SelectIconString", menu != null).ToString(),
            YesNoOpen = yesNo != null,
            CanInteract = canInteract,
            PlayerOccupied = BusyFlags.Any(f => this.condition[f]),
        };
    }

    // Counts each time a window goes from hidden to shown.
    private int Run(string name, bool visible)
    {
        var (was, run) = this.appearances.GetValueOrDefault(name);
        if (visible && !was)
            run++;
        this.appearances[name] = (visible, run);
        return run;
    }

    private AtkUnitBase* Addon(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
    }

    private bool Visible(string name)
    {
        var addon = this.Addon(name);
        return addon != null && addon->IsVisible;
    }

    private AtkUnitBase* Ready(string name)
    {
        var addon = this.Addon(name);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    private static bool Actionable(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        return node != null && AddonOps.IsChainVisible(node, out _);
    }

    // The NPC menu lists entries as (icon, text, flag) from AtkValues[6]; [5] is the count.
    private static int TriadMenuIndex(AtkUnitBase* menu)
    {
        var count = IntValue(menu, 5);
        for (var i = 0; i < count && i < 16; i++)
        {
            if (IntValue(menu, 6 + (3 * i)) is >= TriadMenuIconFirst and <= TriadMenuIconLast)
                return i;
        }

        return -1;
    }

    private static int IntValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || index >= addon->AtkValuesCount)
            return 0;
        ref var v = ref addon->AtkValues[index];
        return v.Type is AtkValueType.Int or AtkValueType.UInt ? v.Int : 0;
    }

    private static string StringValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || index >= addon->AtkValuesCount)
            return string.Empty;
        ref var v = ref addon->AtkValues[index];
        if (v.Type is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString))
            return string.Empty;
        try
        {
            return v.String.ExtractText();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static float Vector3Distance(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Distance(a, b);
}
