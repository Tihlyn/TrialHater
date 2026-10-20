using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Cards;

namespace TrialHater.Game;

// Writes a deck into one saved deck slot through the Gold Saucer window's deck editor
// (docs/MODEL_MAPPING.md, session 11). Each frame it reads the windows and takes the one next step
// toward "slot k holds these cards, saved", so a step that did not take is simply retried. One
// retry per step; a step the game ignores twice, or the whole write taking too long, stops it with
// a reason. Framework thread only.
public sealed unsafe class DeckWriter
{
    // GSInfoEditDeck: the 5 deck slots in stride 17 from [525], card id first.
    private const int EditSlotBase = 525;
    private const int EditSlotStride = 17;

    // GSInfoCardDeck: per deck (valid, name, 5 x 88000 + id) in stride 7 from [1].
    private const int DeckRowBase = 1;
    private const int DeckRowStride = 7;
    private const int CardIconBase = 88000;

    private const string RemoveEntry = "Remove from Deck";

    private static readonly TimeSpan ActionGap = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(90);

    private readonly Dalamud.Plugin.Services.IGameGui gameGui;
    private readonly Action<string> log;
    private ushort[] target = [];
    private int slot;
    private DateTime startedAt;
    private DateTime lastActionAt;
    private DateTime waitingSince;
    private string waitingFor = string.Empty;
    private string lastKey = string.Empty;
    private int attempts;
    private bool openedWindow;
    private bool saved;
    private int menuSlot = -1;

    public DeckWriter(Dalamud.Plugin.Services.IGameGui gameGui, Action<string> log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public bool Active { get; private set; }

    public string Status { get; private set; } = string.Empty;

    // The deck slot and cards of the last finished write (verified in the deck list), or null.
    public (int Slot, IReadOnlyList<ushort> Cards)? Written { get; private set; }

    public void Start(int deckSlot, IReadOnlyList<CardInfo> deck)
    {
        this.target = deck.Select(c => c.Id).ToArray();
        this.slot = deckSlot;
        this.startedAt = DateTime.UtcNow;
        this.lastActionAt = default;
        this.lastKey = string.Empty;
        this.attempts = 0;
        this.openedWindow = false;
        this.saved = false;
        this.menuSlot = -1;
        this.waitingFor = string.Empty;
        this.Active = true;
        this.Written = null;
        this.Status = $"writing Deck {deckSlot + 1}";
        this.log($"[DeckWriter] writing Deck {deckSlot + 1}: {string.Join(", ", deck.Select(c => $"{c.Name} ({c.Id})"))}");
    }

    public void Stop(string why)
    {
        if (!this.Active)
            return;
        this.Active = false;
        this.Status = why;
        this.log($"[DeckWriter] {why}");
    }

    public void Tick(DateTime now)
    {
        if (!this.Active)
            return;
        if (now - this.startedAt > GiveUpAfter)
        {
            this.Stop($"stopped: no result after {GiveUpAfter.TotalSeconds:0} s ({this.Status})");
            return;
        }

        if (now - this.lastActionAt < ActionGap)
            return;

        var info = this.Ready("GoldSaucerInfo");
        var decks = this.Ready("GSInfoCardDeck");
        var editor = this.Ready("GSInfoEditDeck");
        var menu = this.Ready("ContextMenu");

        if (editor != null)
        {
            this.Edit(now, editor, menu);
            return;
        }

        if (decks != null)
        {
            if (!this.saved && this.RowShowsTarget(decks))
            {
                this.saved = true;
                this.log($"[DeckWriter] Deck {this.slot + 1} already holds this deck");
            }

            if (!this.saved)
            {
                this.Act(now, $"edit {this.slot}", "opening the deck editor", () => AddonOps.Callback(decks, false, 5, this.slot));
                return;
            }

            // The capture closed it with [-1] then the window's own [-2]: send [-1] and close.
            if (this.openedWindow && info != null)
            {
                this.Act(now, "close window", "closing the Gold Saucer window", () => AddonOps.Callback(info, true, -1));
                return;
            }

            this.Finish();
            return;
        }

        if (this.saved)
        {
            // Editor closed and the window gone (we closed it): done.
            this.Finish();
            return;
        }

        if (info != null)
        {
            this.Wait(now, "the Triple Triad deck tab",
                "the Gold Saucer window is open but not on the Triple Triad deck tab: switch to it and press again");
            return;
        }

        if (!this.openedWindow)
        {
            this.Act(now, "open window", "opening the Gold Saucer window", OpenGoldSaucer);
            this.openedWindow = true;
            return;
        }

        this.Wait(now, "the Gold Saucer window", "the Gold Saucer window did not open: open its Triple Triad deck tab and press again");
    }

    // In the editor: fix the first wrong slot, then save, then close.
    private void Edit(DateTime now, AtkUnitBase* editor, AtkUnitBase* menu)
    {
        var current = Enumerable.Range(0, 5).Select(s => AddonOps.IntValue(editor, EditSlotBase + (EditSlotStride * s))).ToArray();
        if (this.saved)
        {
            // The capture closed it with [17] then the window's own [-2]: send [17] and close.
            this.Act(now, "close editor", "closing the editor", () => AddonOps.Callback(editor, true, 17));
            return;
        }

        if (current.SequenceEqual(this.target.Select(id => (int)id)))
        {
            // Saving shows the deck in the deck list at once; that is the confirmation.
            var decks = this.Ready("GSInfoCardDeck");
            if (decks != null && this.RowShowsTarget(decks))
            {
                this.saved = true;
                this.log($"[DeckWriter] Deck {this.slot + 1} saved");
                return;
            }

            this.Act(now, "save", "saving", () => AddonOps.Callback(editor, false, 10));
            return;
        }

        for (var s = 0; s < 5; s++)
        {
            if (current[s] == this.target[s])
                continue;

            // The editor refuses a card already in the deck: free its other slot first.
            var elsewhere = Array.IndexOf(current, (int)this.target[s]);
            if (elsewhere >= 0)
            {
                this.Remove(now, editor, menu, elsewhere);
                return;
            }

            var place = s;
            this.Act(now, $"place {s} {this.target[s]}", $"placing card {this.target[s]} in slot {s + 1}",
                () => AddonOps.Callback(editor, false, 11, place, this.target[place]));
            return;
        }
    }

    // Slot menu ([13, s]), then its "Remove from Deck" entry ([0, 0, 0]).
    private void Remove(DateTime now, AtkUnitBase* editor, AtkUnitBase* menu, int s)
    {
        if (menu != null && this.menuSlot == s)
        {
            var entry = AddonOps.StringValue(menu, 8);
            if (entry != RemoveEntry)
            {
                this.Stop($"stopped: the slot menu offers '{entry}', not '{RemoveEntry}'");
                return;
            }

            this.Act(now, $"remove {s}", $"removing slot {s + 1}", () => AddonOps.Callback(menu, false, 0, 0, 0));
            return;
        }

        this.menuSlot = s;
        this.Act(now, $"menu {s}", $"opening the menu of slot {s + 1}", () => AddonOps.Callback(editor, false, 13, s));
    }

    private bool RowShowsTarget(AtkUnitBase* decks)
    {
        var start = DeckRowBase + (DeckRowStride * this.slot) + 2;
        return Enumerable.Range(0, 5).All(i => AddonOps.IntValue(decks, start + i) - CardIconBase == this.target[i]);
    }

    private void Finish()
    {
        this.Written = (this.slot, this.target);
        this.Stop($"done: Deck {this.slot + 1} written and saved");
    }

    // One action per key; repeated once after RetryAfter if the window still asks for it.
    private void Act(DateTime now, string key, string what, Func<DispatchResult> action)
    {
        this.waitingFor = string.Empty;
        if (key == this.lastKey)
        {
            if (now - this.lastActionAt < RetryAfter)
            {
                this.Status = $"{what}: waiting for the game";
                return;
            }

            if (this.attempts >= 2)
            {
                this.Stop($"stopped: the game ignored '{what}' twice");
                return;
            }
        }
        else
        {
            this.lastKey = key;
            this.attempts = 0;
        }

        var result = action();
        this.lastActionAt = now;
        if (!result.Sent)
        {
            this.Status = $"{what}: refused ({result.Detail})";
            return;
        }

        this.attempts++;
        this.Status = what;
        this.log($"[DeckWriter] {what}: {result.Detail}{(this.attempts > 1 ? " (retry)" : string.Empty)}");
    }

    private void Wait(DateTime now, string what, string failure)
    {
        if (this.waitingFor != what)
        {
            this.waitingFor = what;
            this.waitingSince = now;
        }

        if (now - this.waitingSince > WindowWait)
        {
            this.Stop($"stopped: {failure}");
            return;
        }

        this.Status = $"waiting for {what}";
    }

    private static DispatchResult OpenGoldSaucer()
    {
        var agents = AgentModule.Instance();
        var agent = agents == null ? null : agents->GetAgentByInternalId(AgentId.GoldSaucer);
        if (agent == null)
            return DispatchResult.Refused("no Gold Saucer agent");
        agent->Show();
        return new DispatchResult(true, "AgentGoldSaucer.Show");
    }

    private AtkUnitBase* Ready(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        var addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }
}
