using TrialHater.Core.Policy;

namespace TrialHater.Game;

// Plays our turn from a fresh decision. Every guard that fails leaves the turn alone and says why
// in Status; nothing is ever sent twice for one turn unless the first send was visibly ignored.
//
// Sequence per turn: wait until the board lets us act and the configured delay has passed, take
// the decision only if its fingerprint is the current view's, check the slot and cell against the
// live frame, dispatch once, then wait for a card to land (acknowledgement). If nothing lands in
// AckTimeout the dispatch is retried once; a second silence stops auto play.
public sealed class AutoPlayer
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    private readonly Action<string> log;
    private (int Match, int Round, int Turn) dispatchedTurn = (-1, -1, -1);
    private int boardCountAtDispatch;
    private DateTime dispatchedAt;
    private int retries;
    private DateTime? turnSeenAt;
    private (int, int, int) turnSeenKey = (-1, -1, -1);

    public AutoPlayer(Action<string> log)
    {
        this.log = log;
    }

    public bool Enabled { get; set; }

    public string Status { get; private set; } = "off";

    public string LastDispatch { get; private set; } = string.Empty;

    public int Placed { get; private set; }

    // Called every frame; `dispatch` performs the placement and is called at most once per turn
    // (twice if the first send was visibly ignored).
    public void Tick(MatchTracker tracker, Decision? decision, int delayMs, DateTime now, Func<int, int, DispatchResult> dispatch)
    {
        if (!this.Enabled)
        {
            this.Status = "off";
            return;
        }

        if (!tracker.InMatch || tracker.Last is not { } frame || tracker.View is not { } view)
        {
            this.Status = "waiting for a match";
            return;
        }

        var key = (tracker.MatchGeneration, tracker.Round, tracker.TurnGeneration);

        // An earlier dispatch: acknowledged once a card lands.
        if (this.dispatchedTurn == key)
        {
            if (frame.OnBoard > this.boardCountAtDispatch)
                return;
            if (now - this.dispatchedAt < AckTimeout)
            {
                this.Status = "waiting for the card to land";
                return;
            }

            if (this.retries >= 1)
            {
                this.Enabled = false;
                this.Status = "stopped: the game ignored the placement twice";
                this.log($"[Auto] {this.Status} ({this.LastDispatch})");
                return;
            }

            this.retries++;
            this.dispatchedTurn = (-1, -1, -1);
            this.log($"[Auto] no card landed {AckTimeout.TotalSeconds:0}s after {this.LastDispatch}; retrying once");
        }

        if (!frame.CanAct)
        {
            this.Status = "opponent's turn";
            return;
        }

        if (this.turnSeenKey != key)
        {
            this.turnSeenKey = key;
            this.turnSeenAt = now;
            this.retries = 0;
        }

        if (now - this.turnSeenAt < TimeSpan.FromMilliseconds(delayMs))
        {
            this.Status = "our turn: letting the board settle";
            return;
        }

        if (decision is null || decision.Fingerprint != view.Fingerprint())
        {
            this.Status = "our turn: solving";
            return;
        }

        if (decision.Status != DecisionStatus.Ready || decision.Move is not { } move)
        {
            this.Status = $"our turn: no playable decision ({decision.Status}: {decision.Reason})";
            return;
        }

        var slot = frame.Blue[move.HandSlot];
        if (!slot.HasCard || slot.Hidden)
        {
            this.Status = $"refused: slot {move.HandSlot} holds no card";
            return;
        }

        if (frame.Board[move.Cell].HasCard)
        {
            this.Status = $"refused: cell {move.Cell} is taken";
            return;
        }

        if (frame.Command == BoardFrame.CommandPlayerActsForced && view.ForcedSlot < 0)
        {
            this.Status = $"refused: the game forces a card but its slot is unreadable ({frame.Arg2})";
            return;
        }

        if (view.ForcedSlot >= 0 && move.HandSlot != view.ForcedSlot)
        {
            this.Status = $"refused: the game forces slot {view.ForcedSlot}";
            return;
        }

        var result = dispatch(move.HandSlot, move.Cell);
        this.LastDispatch = result.Detail;
        if (!result.Sent)
        {
            this.Status = $"refused: {result.Detail}";
            return;
        }

        this.dispatchedTurn = key;
        this.boardCountAtDispatch = frame.OnBoard;
        this.dispatchedAt = now;
        this.Placed++;
        this.Status = $"played {move}";
        this.log($"[Auto] {result.Detail} ({decision.Reason}; {decision.Hypotheses} hand(s), {decision.Elapsed.TotalMilliseconds:0} ms)");
    }
}
