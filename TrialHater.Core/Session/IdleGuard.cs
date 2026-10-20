namespace TrialHater.Core.Session;

// Keeps a duty from ejecting us for inactivity while auto play runs unattended (Open Tournament,
// Battlehall): read the game's idle counters, then deliver a short Control press to this
// process's game window. Never write counters: handling input may do more than resetting three
// floats. Ported from MahjongHater (Core/Operate/IdleGuard.cs; its docs/research/
// STALL_2026_09_22.md compares the upstream AntiAfkKick). The native seams (timer read, window,
// key) live in TrialHater/Game/IdleInput.cs.
public sealed class IdleGuard
{
    public readonly record struct Timers(float Afk, float ContentInput, float Input)
    {
        public float Highest => Math.Max(this.Afk, Math.Max(this.ContentInput, this.Input));
        public bool IsValid => float.IsFinite(this.Afk) && float.IsFinite(this.ContentInput)
                               && float.IsFinite(this.Input) && this.ContentInput >= 0 && this.Input >= 0;

        // Afk may be negative while AFK. Only counters above the trigger need to fall.
        public bool DroppedSince(Timers before) =>
            (before.Afk <= IdleThresholdSeconds || this.Afk < before.Afk)
            && (before.ContentInput <= IdleThresholdSeconds || this.ContentInput < before.ContentInput)
            && (before.Input <= IdleThresholdSeconds || this.Input < before.Input);

        public override string ToString() => $"afk={this.Afk:F1}s content={this.ContentInput:F1}s input={this.Input:F1}s";
    }

    public const float IdleThresholdSeconds = 30;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ObserveAfter = TimeSpan.FromSeconds(1);

    // How long past its due release a held key may keep automation waiting.
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(3);
    private readonly Action<string> log;
    private readonly Func<Timers?> read;
    private readonly Func<nint> findWindow;
    private readonly Func<nint, bool, bool> sendKey;
    private readonly Func<bool> controlHeld;
    private DateTime nextCheckUtc;
    private DateTime releaseAtUtc;
    private DateTime retryReleaseAtUtc;
    private DateTime observeAtUtc;
    private nint pressedWindow;
    private DateTime lastTickUtc;
    private Timers? beforePress;

    // Platform seams keep tests independent of native pointers and OS input.
    public IdleGuard(Action<string> log, Func<Timers?> read, Func<nint> findWindow,
        Func<nint, bool, bool> sendKey, Func<bool> controlHeld)
    {
        this.log = log;
        this.read = read;
        this.findWindow = findWindow;
        this.sendKey = sendKey;
        this.controlHeld = controlHeld;
    }

    public bool Enabled { get; set; } = true;
    public int NudgesThisSession { get; private set; }
    public float HighestSeenSeconds { get; private set; }
    public Timers? LastTimers { get; private set; }
    public bool? LastNudgeObservedReset { get; private set; }
    public bool IsKeyDown => this.pressedWindow != 0;

    // Whether automation should stand aside for the press. It should while the key is
    // genuinely held - a real modifier can change what a click means - but NOT forever: in
    // MahjongHater a release that could not be delivered blocked every automated action for the
    // rest of the session. Auto play speaks the addons' command channel (callbacks, ButtonClick),
    // which no modifier alters, so a stuck release stops blocking once it is plainly overdue and
    // only the release keeps retrying.
    // Uses the guard's own clock (the last Tick), not the wall clock: everything here runs on
    // the framework thread against the time that tick was given.
    public bool BlocksAutomation => this.IsKeyDown && this.lastTickUtc < this.releaseAtUtc + StuckAfter;
    public string Status { get; private set; } = "Off";

    // Framework thread. Call even when disabled so a pending release can finish. `unattended`:
    // something auto play could be kicked from is running (a match, a farming session, a duty).
    public void Tick(bool autoPlayEnabled, bool unattended, DateTime nowUtc)
    {
        this.lastTickUtc = nowUtc;
        var active = this.Enabled && autoPlayEnabled && unattended;
        if (this.IsKeyDown)
        {
            if (nowUtc < this.retryReleaseAtUtc)
                return;
            if (active && nowUtc < this.releaseAtUtc)
                return;
            if (!this.Release())
            {
                this.retryReleaseAtUtc = nowUtc + ObserveAfter;
                return;
            }
            this.observeAtUtc = nowUtc + ObserveAfter;
        }

        if (!active)
        {
            this.beforePress = null;
            this.nextCheckUtc = default;
            this.Status = !this.Enabled ? "Off" : "Idle (no unattended match)";
            return;
        }

        if (this.beforePress is { } before)
        {
            if (nowUtc < this.observeAtUtc)
                return;
            var after = this.ReadTimers();
            this.LastNudgeObservedReset = after is { } timers ? timers.DroppedSince(before) : null;
            this.Status = this.LastNudgeObservedReset switch
            {
                true => "Activity observed after Control press",
                false => "Control sent, but idle counters did not fall",
                _ => "Control sent; timer reading unavailable",
            };
            this.log($"[IdleGuard] {this.Status}: before [{before}], after [{after?.ToString() ?? "unavailable"}].");
            this.beforePress = null;
        }

        if (nowUtc < this.nextCheckUtc)
            return;
        this.nextCheckUtc = nowUtc + CheckEvery;
        var seen = this.ReadTimers();
        if (seen is null)
        {
            this.Status = "Unavailable (no valid input timer readings)";
            return;
        }
        if (seen.Value.Highest <= IdleThresholdSeconds)
        {
            this.Status = $"Armed: {seen.Value}";
            return;
        }
        if (this.controlHeld())
        {
            this.Status = "Waiting for the held Control key to be released";
            return;
        }
        var window = this.findWindow();
        if (window == 0)
        {
            this.Status = "Unavailable (no game window for this process)";
            return;
        }

        // A timeout can leave delivery uncertain: even a failed down needs an up.
        this.pressedWindow = window;
        this.retryReleaseAtUtc = default;
        this.releaseAtUtc = nowUtc + HoldFor;
        this.LastNudgeObservedReset = null;
        if (!this.sendKey(window, true))
        {
            this.Release();
            this.Status = "Control key-down delivery failed; release requested";
            this.log($"[IdleGuard] {this.Status} ({seen.Value}).");
            return;
        }
        this.beforePress = seen;
        this.observeAtUtc = DateTime.MaxValue;
        this.NudgesThisSession++;
        this.Status = $"Control press #{this.NudgesThisSession} sent; awaiting timer observation";
        this.log($"[IdleGuard] {this.Status} ({seen.Value}).");
    }

    // Also used on logout/unload. Native release falls back to an asynchronous message.
    public void Reset()
    {
        if (!this.Release())
            return;
        this.beforePress = null;
        this.nextCheckUtc = default;
        this.Status = this.Enabled ? "Idle (no unattended match)" : "Off";
    }

    private bool Release()
    {
        if (!this.IsKeyDown)
            return true;
        if (!this.sendKey(this.pressedWindow, false))
        {
            const string pending = "Control release pending; no further presses will be sent";
            if (this.Status != pending)
                this.log($"[IdleGuard] {pending}.");
            this.Status = pending;
            return false;
        }
        this.pressedWindow = 0;
        return true;
    }

    private Timers? ReadTimers()
    {
        this.LastTimers = this.read() is { IsValid: true } timers ? timers : null;
        if (this.LastTimers is { } seen)
            this.HighestSeenSeconds = Math.Max(this.HighestSeenSeconds, seen.Highest);
        return this.LastTimers;
    }
}
