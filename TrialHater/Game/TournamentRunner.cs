using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Tournament;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TrialHater.Game;

// Queues for Open Tournaments and finishes them: talks to the Official the player targeted (and
// targets it again when leaving the duty cleared the target), signs up while sign-up is open (and
// waits for the next tournament when it is not), commences when the duty pops, and closes the
// results and rewards. The draft and the matches
// are DraftService's and the auto player's. TournamentPlanner decides; this class reads the windows
// and performs the one action it names, once per window appearance after the window has settled,
// retried once (dispatched is not accepted). Framework thread only.
public sealed unsafe class TournamentRunner
{
    // Captured 2026-10-20 (docs/MODEL_MAPPING.md, session 17). The texts are the English client's;
    // with another language nothing matches and the runner waits with a reason instead of guessing.
    private const string DutyName = "Triple Triad Open Tournament";
    private const string SignUpEntry = "Sign up for an Open tournament.";
    private const string SignUpQuestion = "Sign up for this tournament?";
    private const uint YesNode = 8;
    private const uint CommenceNode = 63;
    private const uint ResultCloseNode = 44;
    private const uint RewardCloseNode = 20;
    private const float InteractRange = 4f;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan ResultSettle = TimeSpan.FromSeconds(4);
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
    private readonly Dictionary<string, DateTime> shownAt = new();
    private string currentKey = string.Empty;
    private DateTime keySeenAt;
    private DateTime? actedAt;
    private int attempts;
    private uint officialBaseId;
    private string officialName = string.Empty;

    public TournamentRunner(IGameGui gameGui, ITargetManager targets, IObjectTable objects, ICondition condition, Action<string> log)
    {
        this.gameGui = gameGui;
        this.targets = targets;
        this.objects = objects;
        this.condition = condition;
        this.log = log;
    }

    public bool Active { get; private set; }

    public TournamentPhase Phase { get; private set; }

    public int MaxTournaments { get; private set; }

    public int TournamentsDone { get; private set; }

    public string Status { get; private set; } = "off";

    public string LastAction { get; private set; } = string.Empty;

    // Distance to the target's hitbox edge at the last read, or -1.
    public float Distance { get; private set; } = -1;

    // `maxTournaments` 0 = until stopped. Needs the Official targeted.
    public string? Start(int maxTournaments)
    {
        if (this.targets.Target is not { } target)
            return "target the Open Tournament Official first";
        this.officialBaseId = target.BaseId;
        this.officialName = target.Name.TextValue;
        this.MaxTournaments = Math.Max(0, maxTournaments);
        this.TournamentsDone = 0;
        this.Phase = TournamentPhase.Registering;
        this.currentKey = string.Empty;
        this.actedAt = null;
        this.Active = true;
        this.Status = "starting";
        this.log($"[Tournament] queueing with {this.officialName} ({this.officialBaseId}), {(this.MaxTournaments == 0 ? "until stopped" : $"{this.MaxTournaments} tournament(s)")}");
        return null;
    }

    public void Stop(string why)
    {
        if (!this.Active)
            return;
        this.Active = false;
        this.Status = $"stopped: {why}";
        this.log($"[Tournament] {this.Status}; {this.TournamentsDone} tournament(s) played");
    }

    public void Tick(DateTime now, int delayMs)
    {
        var snapshot = this.Read(now);
        if (!this.Active)
            return;

        var action = TournamentPlanner.Next(snapshot, this.Phase);
        if (action.Kind == TournamentActionKind.Wait)
        {
            // Each new reason once in the log, so a stall says why.
            if (action.Reason != this.Status)
                this.log($"[Tournament] waiting: {action.Reason} (target {this.targets.Target?.Name.TextValue ?? "none"}, distance {this.Distance:0.0})");
            this.Status = action.Reason;
            return;
        }

        // Window actions are keyed and settled by the window's appearance; Interact (no window) by
        // the moment it became the next step.
        var window = WindowOf(action.Kind);
        var shown = window is null ? (DateTime?)null : this.shownAt.GetValueOrDefault(window, now);
        var key = $"{action.Kind}|{shown:O}";
        if (key != this.currentKey)
        {
            this.currentKey = key;
            this.keySeenAt = now;
            this.actedAt = null;
            this.attempts = 0;
        }

        var since = shown ?? this.keySeenAt;

        var settle = action.Kind == TournamentActionKind.CloseResult ? ResultSettle : TimeSpan.FromMilliseconds(Math.Max(delayMs, Settle.TotalMilliseconds));
        if (now - since < settle)
        {
            this.Status = $"{action.Reason}: settling";
            return;
        }

        if (this.actedAt is { } sent)
        {
            var retry = action.Kind == TournamentActionKind.Interact ? InteractRetryAfter : RetryAfter;
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
        this.log($"[Tournament] {this.LastAction} ({action.Reason}{(this.attempts > 1 ? ", retry" : string.Empty)})");
        this.Advance(action.Kind);
    }

    private void Advance(TournamentActionKind kind)
    {
        switch (kind)
        {
            case TournamentActionKind.ConfirmSignUp:
                this.Phase = TournamentPhase.Queued;
                break;
            case TournamentActionKind.Commence:
                this.Phase = TournamentPhase.Playing;
                break;
            case TournamentActionKind.CloseReward:
                this.TournamentsDone++;
                this.Phase = TournamentPhase.Registering;
                if (this.MaxTournaments > 0 && this.TournamentsDone >= this.MaxTournaments)
                    this.Stop("goal reached");
                break;
        }
    }

    private DispatchResult Perform(TournamentAction action) => action.Kind switch
    {
        TournamentActionKind.Retarget => this.Retarget(),
        TournamentActionKind.Interact => this.Interact(),
        TournamentActionKind.AdvanceTalk => this.AdvanceTalk(),
        TournamentActionKind.ChooseSignUp => AddonOps.Callback(this.Addon("SelectString"), true, action.Index),
        TournamentActionKind.ConfirmSignUp => AddonOps.ActivateButton(this.Addon("SelectYesno"), YesNode),
        TournamentActionKind.Commence => AddonOps.ActivateButton(this.Addon("ContentsFinderConfirm"), CommenceNode),
        TournamentActionKind.CloseResult => AddonOps.ActivateButton(this.Addon("TripleTriadTournamentResult"), ResultCloseNode),
        TournamentActionKind.CloseReward => AddonOps.ActivateButton(this.Addon("TripleTriadTournamentReward"), RewardCloseNode),
        _ => DispatchResult.Refused($"nothing to do for {action.Kind}"),
    };

    private static string? WindowOf(TournamentActionKind kind) => kind switch
    {
        TournamentActionKind.AdvanceTalk => "Talk",
        TournamentActionKind.ChooseSignUp => "SelectString",
        TournamentActionKind.ConfirmSignUp => "SelectYesno",
        TournamentActionKind.Commence => "ContentsFinderConfirm",
        TournamentActionKind.CloseResult => "TripleTriadTournamentResult",
        TournamentActionKind.CloseReward => "TripleTriadTournamentReward",
        _ => null,
    };

    // Same as the farming session: the Talk window's own click, or (no click handler) the empty
    // callback a real click sends.
    private DispatchResult AdvanceTalk()
    {
        var talk = this.Addon("Talk");
        var click = AddonOps.Click(talk);
        return click.Sent || !AddonOps.CanReceiveInput(talk, out _) ? click : AddonOps.Callback(talk, true);
    }

    private DispatchResult Interact()
    {
        if (this.targets.Target is not { } target || target.BaseId != this.officialBaseId)
            return DispatchResult.Refused("the Official is not targeted");
        var system = TargetSystem.Instance();
        if (system == null)
            return DispatchResult.Refused("no target system");
        system->InteractWithObject((GameObject*)target.Address, true);
        return new DispatchResult(true, $"interact with {target.Name}");
    }

    private DispatchResult Retarget()
    {
        if (this.OfficialInRange() is not { } official)
            return DispatchResult.Refused("the Official is not in range");
        this.targets.Target = official;
        return new DispatchResult(true, $"target {official.Name}");
    }

    // The nearest targetable object with the Official's base id within interaction range.
    private IGameObject? OfficialInRange()
    {
        if (this.objects.LocalPlayer is not { } player || this.officialBaseId == 0)
            return null;
        return this.objects
            .Where(o => o.BaseId == this.officialBaseId && o.IsTargetable)
            .Select(o => (Object: o, Distance: System.Numerics.Vector3.Distance(player.Position, o.Position) - o.HitboxRadius))
            .Where(o => o.Distance <= InteractRange)
            .OrderBy(o => o.Distance)
            .Select(o => o.Object)
            .FirstOrDefault();
    }

    private TournamentSnapshot Read(DateTime now)
    {
        var duty = this.Track("ContentsFinderConfirm", now);
        var yesNo = this.Track("SelectYesno", now);
        var menu = this.Track("SelectString", now);
        var talk = this.Track("Talk", now);
        var result = this.Track("TripleTriadTournamentResult", now);
        var reward = this.Track("TripleTriadTournamentReward", now);

        var player = this.objects.LocalPlayer;
        var target = this.targets.Target;
        // Measured to the edge of the NPC's hitbox: the Official stands behind a counter.
        this.Distance = player is null || target is null ? -1 : System.Numerics.Vector3.Distance(player.Position, target.Position) - target.HitboxRadius;
        var canInteract = player is not null && target is not null && target.BaseId == this.officialBaseId && this.Distance <= InteractRange;

        return new TournamentSnapshot
        {
            DutyReadyOpen = duty != null,
            DutyIsTournament = duty != null && AddonOps.StringValue(duty, 1) == DutyName,
            YesNoOpen = yesNo != null,
            YesNoIsSignUp = yesNo != null && AddonOps.StringValue(yesNo, 0).Contains(SignUpQuestion, StringComparison.Ordinal),
            MenuOpen = menu != null,
            SignUpIndex = menu == null ? -1 : SignUpIndexOf(menu),
            // Only the Official's lines (some lines leave the speaker empty, as in the session).
            TalkOpen = talk != null && (AddonOps.StringValue(talk, 1) is not { Length: > 0 } speaker
                                        || speaker.Equals(this.officialName, StringComparison.OrdinalIgnoreCase)),
            ResultOpen = result != null,
            RewardOpen = reward != null,
            InDuty = this.condition[ConditionFlag.BoundByDuty] || this.condition[ConditionFlag.BoundByDuty56] || this.condition[ConditionFlag.BoundByDuty95],
            CanInteract = canInteract,
            // Searched only when the next step would be talking to it.
            OfficialInRange = this.Active && this.Phase == TournamentPhase.Registering && !canInteract && this.OfficialInRange() is not null,
            PlayerOccupied = BusyFlags.Any(f => this.condition[f]),
            SignUpOpensAt = OpenTournament.SignUpOpensAt(new DateTimeOffset(now, TimeSpan.Zero)),
        };
    }

    // The Official's menu lists its entries as strings from [7]; [5] is the count.
    private static int SignUpIndexOf(AtkUnitBase* menu)
    {
        var count = AddonOps.IntValue(menu, 5);
        for (var i = 0; i < count && i < 16; i++)
        {
            if (AddonOps.StringValue(menu, 7 + i) == SignUpEntry)
                return i;
        }

        return -1;
    }

    // The window if ready for input; remembers when it appeared (for the settle time and the
    // once-per-appearance key).
    private AtkUnitBase* Track(string name, DateTime now)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        var addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        var ready = addon != null && addon->IsVisible && addon->IsReady;
        if (!ready)
        {
            this.shownAt.Remove(name);
            return null;
        }

        this.shownAt.TryAdd(name, now);
        return addon;
    }

    private AtkUnitBase* Addon(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
    }
}
