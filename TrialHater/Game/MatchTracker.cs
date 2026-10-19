using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Rules;

namespace TrialHater.Game;

// Turns successive BoardFrames into MatchViews and keeps what the screen no longer shows: which
// cards the NPC has played, the Sudden Death round, Swap exchanges. Framework thread only.
public sealed class MatchTracker
{
    private readonly List<CardStats> redPlayed = new();
    private readonly List<string> notes = new();
    private BoardFrame? previous;
    private RuleSet rules = RuleSet.Empty;
    private bool swapResolved;
    private List<CardStats> redForeign = new();
    private List<CardStats> redLost = new();

    public int MatchGeneration { get; private set; }

    public int Round { get; private set; }

    // Changes whenever a card lands or the turn passes: an action belongs to exactly one value.
    public int TurnGeneration { get; private set; }

    public bool InMatch { get; private set; }

    public uint NpcTriadId { get; set; }

    public RuleSet Rules => this.rules;

    public BoardFrame? Last => this.previous;

    public MatchView? View { get; private set; }

    // Set when the tracker had to guess (frames skipped a placement, an unparsed rule...).
    public string? Warning { get; private set; }

    public IReadOnlyList<string> Notes => this.notes;

    public IReadOnlyList<CardStats> RedPlayed => this.redPlayed;

    public event Action<string>? Event;

    // Every NPC placement with the view it was made from (opponent-model fitting).
    public event Action<MatchView, CardStats, int>? RedMoved;

    // Once per match, when the result window opens (or a full board closes without one, as in
    // tournaments): Blue's final card count (6+ wins).
    public event Action<int>? MatchEnded;

    // The opponent's name as the board shows it (AtkValues[4]), kept after the board closes.
    public string? Opponent { get; private set; }

    public void Reset()
    {
        this.InMatch = false;
        this.Ended = false;
        this.previous = null;
        this.View = null;
        this.redPlayed.Clear();
        this.rules = RuleSet.Empty;
        this.Round = 0;
        this.NpcTriadId = 0;
        this.Opponent = null;
        this.Warning = null;
        this.swapResolved = false;
        this.redForeign = new();
        this.redLost = new();
    }

    // True once the result window has been seen for the current match.
    public bool Ended { get; private set; }

    // `savedDecks` (stats of the player's saved decks) is only used to work out a Swap.
    // `resultVisible`: the result window is up, so the next deal is a new match (a rematch can
    // reuse the open board), not a Sudden Death round.
    public void Update(BoardFrame? frame, bool resultVisible, GameData data, Func<IReadOnlyList<CardStats[]>> savedDecks)
    {
        if (resultVisible && this.InMatch && !this.Ended)
            this.End("result window open");

        if (frame is null || !frame.Visible)
        {
            // Tournament matches have no result window: the board just closes on the final position.
            if (this.InMatch && !this.Ended && this.previous is { Visible: true, OnBoard: 9 })
                this.End("board closed on a full board");
            if (this.InMatch)
                this.Note("board closed");
            this.InMatch = false;
            this.previous = frame;
            this.View = null;
            return;
        }

        var handsDealt = frame.BlueInHand + frame.RedInHand > 0;
        var redealt = this.previous is { } last && last.OnBoard > 0 && frame.OnBoard == 0 && handsDealt;
        if (this.InMatch && redealt && (this.Ended || !this.rules.Has(TriadRule.SuddenDeath)))
            this.InMatch = false;
        if (!this.InMatch && handsDealt && !resultVisible)
            this.StartMatch(frame);
        if (!this.InMatch)
        {
            this.previous = frame;
            return;
        }

        this.ParseRules(frame, data);
        if (frame.OpponentName is { Length: > 0 } opponent)
            this.Opponent = opponent;
        if (this.previous is { } before && before.Visible)
            this.TrackChanges(before, frame);

        if (!this.swapResolved && this.rules.Has(TriadRule.Swap) && frame.OnBoard == 0 && frame.BlueInHand == 5)
            this.ResolveSwap(frame, savedDecks());

        this.previous = frame;
        this.View = this.BuildView(frame);
    }

    private void End(string why)
    {
        this.Ended = true;
        var final = this.previous;
        var score = final is null ? -1 : final.Board.Count(s => s.HasCard && s.Owner == 1) + final.BlueInHand;
        this.Note($"{why}: {score}/10 cards ({(score > 5 ? "win" : score == 5 ? "draw" : "loss")})");
        if (score >= 0)
            this.MatchEnded?.Invoke(score);
    }

    private void StartMatch(BoardFrame frame)
    {
        var npc = this.NpcTriadId;
        this.Reset();
        this.NpcTriadId = npc;
        this.InMatch = true;
        this.MatchGeneration++;
        this.TurnGeneration++;
        this.notes.Clear();
        this.Note($"match #{this.MatchGeneration} started (npc {npc})");
    }

    private void ParseRules(BoardFrame frame, GameData data)
    {
        var found = RuleSet.Empty;
        var roulette = false;
        foreach (var text in frame.Texts)
        {
            foreach (var rule in data.ParseRuleText(text))
            {
                found = found.With(rule);
                roulette |= rule == TriadRule.Roulette;
            }
        }

        // The rule list is drawn once the board is up and does not change during a match, except
        // Roulette resolving. Keep the union so a frame with the panel mid-animation loses nothing.
        var merged = RuleSet.Of(this.rules.Rules.Concat(found.Rules));
        if (!roulette && merged.Has(TriadRule.Roulette) && found.Mask != 0)
            merged = merged.Without(TriadRule.Roulette);
        if (merged != this.rules)
        {
            this.rules = merged;
            this.Note($"rules: {merged}");
        }
    }

    private void TrackChanges(BoardFrame before, BoardFrame now)
    {
        if (now.CanAct != before.CanAct)
            this.TurnGeneration++;

        // Sudden Death: a drawn board clears and both hands are dealt again.
        if (before.OnBoard == 9 && now.OnBoard < before.OnBoard)
        {
            this.Round++;
            this.redPlayed.Clear();
            this.TurnGeneration++;
            this.Note($"board cleared: Sudden Death round {this.Round}");
            return;
        }

        var added = now.OnBoard - before.OnBoard;
        if (added <= 0)
            return;
        this.TurnGeneration++;
        if (added > 1)
            this.Warning = $"{added} cards landed between two frames; the NPC's played cards may be incomplete";

        for (var cell = 0; cell < 9; cell++)
        {
            if (before.Board[cell].HasCard || !now.Board[cell].HasCard)
                continue;
            var redPlaced = now.RedInHand < before.RedInHand;
            var bluePlaced = now.BlueInHand < before.BlueInHand;
            if (redPlaced && !bluePlaced)
            {
                // Prefer the hand slot's face (the board may show a type-shifted value).
                var stats = RedSlotThatLeft(before, now) ?? Stats(now.Board[cell]);
                if (this.View is { } from)
                    this.RedMoved?.Invoke(from, stats, cell);
                this.redPlayed.Add(stats);
                this.Note($"red played {stats} at {cell}");
            }
            else if (bluePlaced)
            {
                this.Note($"we played {Stats(now.Board[cell])} at {cell}");
            }
        }
    }

    private static CardStats? RedSlotThatLeft(BoardFrame before, BoardFrame now)
    {
        for (var i = 0; i < 5; i++)
            if (before.Red[i].HasCard && !now.Red[i].HasCard && !before.Red[i].Hidden)
                return Stats(before.Red[i]);
        return null;
    }

    // Swap trades one random card each way before the first move. Our hand then differs from the
    // saved deck we picked by exactly one card: that one is the NPC's (seen, no longer in its
    // hand), and our missing card is now in the NPC's hand.
    private void ResolveSwap(BoardFrame frame, IReadOnlyList<CardStats[]> decks)
    {
        this.swapResolved = true;
        var hand = frame.Blue.Where(s => s.HasCard).Select(Stats).ToList();
        foreach (var deck in decks)
        {
            var missing = new List<CardStats>(deck);
            var extra = new List<CardStats>();
            foreach (var card in hand)
            {
                if (!missing.Remove(card))
                    extra.Add(card);
            }

            if (missing.Count == 1 && extra.Count == 1)
            {
                this.redForeign = missing;
                this.redLost = extra;
                this.Note($"swap: gave {missing[0]}, received {extra[0]}");
                return;
            }
        }

        this.Warning = "Swap is on but no saved deck matches the hand; the NPC's hand reading may be off";
    }

    private MatchView BuildView(BoardFrame frame)
    {
        var board = new BoardCard?[9];
        for (var i = 0; i < 9; i++)
        {
            var slot = frame.Board[i];
            if (slot.HasCard)
                board[i] = new BoardCard(Stats(slot), slot.Owner == 1 ? Side.Blue : Side.Red);
        }

        var blue = frame.Blue.Select(s => s.HasCard && !s.Hidden ? HandSlot.Of(Stats(s)) : HandSlot.Empty).ToArray();
        var red = frame.Red.Select(s => !s.HasCard ? HandSlot.Empty : s.Hidden ? HandSlot.Hidden : HandSlot.Of(Stats(s))).ToArray();
        return new MatchView(this.rules, board, blue, red, frame.CanAct ? Side.Blue : Side.Red, frame.CanAct ? frame.ForcedSlot : -1, this.NpcTriadId, this.Round)
        {
            RedPlayed = [.. this.redPlayed],
            RedForeign = this.redForeign,
            RedLost = this.redLost,
        };
    }

    public static CardStats Stats(SlotRaw slot) => new(slot.Up, slot.Right, slot.Down, slot.Left, (CardType)Math.Min(slot.Type, (byte)4));

    private void Note(string text)
    {
        this.notes.Add(text);
        if (this.notes.Count > 200)
            this.notes.RemoveAt(0);
        this.Event?.Invoke(text);
    }
}
