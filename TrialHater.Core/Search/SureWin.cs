using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using GameEngine = TrialHater.Core.Engine.Engine;

namespace TrialHater.Core.Search;

// Moves that win whatever the NPC's hidden cards are. The exact solver scores each hand
// hypothesis as if that hand were visible, so a move can be a win in every hypothesis and still
// lose: each hand may need a different reply later, and we have to choose without knowing which
// one the NPC holds (strategy fusion; a recorded Prideful Stag loss, docs/RESEARCH.md). Here our
// move is one move for every hypothesis still consistent with what we have seen, and the NPC, who
// sees everything, picks the hypothesis and its reply. Hypotheses split when the NPC plays a card
// only some of them hold.
//
// Threshold only (win, or draw or better), so cutoffs come early; the searched tree shrinks fast
// with cards on the board (recorded decisions: about 1 s median for an opening, milliseconds from
// the third card on).
public static class SureWin
{
    // One bit per hypothesis in the memo key.
    public const int MaxHypotheses = 64;

    // Whether the search applies: several hypotheses, and no card drawn at random (Chaos), which
    // would need chance nodes. An Ordered NPC's hidden slot is the NPC's pick here: a guarantee
    // has to hold whichever hidden card comes next.
    public static bool Applies(IReadOnlyList<Position> positions)
        => positions.Count is > 1 and <= MaxHypotheses
            && positions[0].BlueSelection != CardSelection.Random
            && positions[0].RedSelection != CardSelection.Random;

    // The candidates whose outcome for us is at least `atLeast` (1 a win, 0 a draw) against every
    // hypothesis and every NPC reply. Throws OperationCanceledException when cancelled (the
    // caller's budget).
    public static HashSet<Move> Moves(IReadOnlyList<Position> positions, IEnumerable<Move> candidates, int workers, CancellationToken cancel, int atLeast = 1)
    {
        var wins = new System.Collections.Concurrent.ConcurrentBag<Move>();
        Parallel.ForEach(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = cancel },
            () => new Search(positions, atLeast, cancel),
            (move, _, search) =>
            {
                if (search.Wins(move))
                    wins.Add(move);
                return search;
            },
            _ => { });
        return [.. wins];
    }

    // One worker's engines (one per hypothesis) and memo; not thread-safe.
    private sealed class Search
    {
        private readonly Hand[] root;
        private readonly Dictionary<(ulong Key, ulong Mask), bool> memo = [];
        private readonly int atLeast;
        private readonly CancellationToken cancel;
        private long nodes;

        public Search(IReadOnlyList<Position> positions, int atLeast, CancellationToken cancel)
        {
            this.root = positions.Select((p, i) => new Hand(new GameEngine(p), p.RedHand, i)).ToArray();
            this.atLeast = atLeast;
            this.cancel = cancel;
        }

        public bool Wins(Move move)
        {
            var undos = new int[this.root.Length];
            for (var i = 0; i < this.root.Length; i++)
                undos[i] = this.root[i].Engine.Play(move.HandSlot, move.Cell);
            try
            {
                return this.Wins(this.root);
            }
            finally
            {
                for (var i = 0; i < this.root.Length; i++)
                    this.root[i].Engine.Undo(move.HandSlot, move.Cell, undos[i]);
            }
        }

        // Every engine in `set` shows the same board and our same hand; only Red's hand differs.
        private bool Wins(Hand[] set)
        {
            if ((++this.nodes & 1023) == 0)
                this.cancel.ThrowIfCancellationRequested();
            var first = set[0].Engine;
            if (first.IsFinished)
                return first.BlueOutcome >= this.atLeast;

            var mask = 0UL;
            foreach (var hand in set)
                mask |= 1UL << hand.Index;
            if (this.memo.TryGetValue((first.Key, mask), out var known))
                return known;

            var result = first.BlueToMove ? this.OurTurn(set) : this.TheirTurn(set);
            this.memo[(first.Key, mask)] = result;
            return result;
        }

        // One move for the whole set: we cannot tell its hypotheses apart.
        private bool OurTurn(Hand[] set)
        {
            var first = set[0].Engine;
            var hand = first.SelectableHand();
            var empty = first.EmptyMask;
            var undos = new int[set.Length];
            for (var slot = 0; slot < Position.HandSize; slot++)
            {
                if ((hand & (1 << slot)) == 0)
                    continue;
                for (var cell = 0; cell < Position.Cells; cell++)
                {
                    if ((empty & (1 << cell)) == 0)
                        continue;
                    for (var i = 0; i < set.Length; i++)
                        undos[i] = set[i].Engine.Play(slot, cell);
                    var win = this.Wins(set);
                    for (var i = 0; i < set.Length; i++)
                        set[i].Engine.Undo(slot, cell, undos[i]);
                    if (win)
                        return true;
                }
            }

            return false;
        }

        // Every play of every hypothesis must still lose for the NPC. A play is what we would see
        // (the card and its cell), so it keeps only the hypotheses holding that card.
        private bool TheirTurn(Hand[] set)
        {
            var empty = set[0].Engine.EmptyMask;
            var plays = new Dictionary<(CardStats Card, int Cell), List<(Hand Hand, int Slot)>>();
            foreach (var hand in set)
            {
                var selectable = hand.Engine.SelectableHand();
                for (var slot = 0; slot < Position.HandSize; slot++)
                {
                    if ((selectable & (1 << slot)) == 0 || hand.Red[slot] is not { } card)
                        continue;
                    for (var cell = 0; cell < Position.Cells; cell++)
                    {
                        if ((empty & (1 << cell)) == 0)
                            continue;
                        if (!plays.TryGetValue((card, cell), out var list))
                            plays[(card, cell)] = list = [];
                        // A hypothesis holding the card twice plays it the same way from either slot.
                        if (list.Count == 0 || list[^1].Hand.Index != hand.Index)
                            list.Add((hand, slot));
                    }
                }
            }

            foreach (var ((_, cell), list) in plays)
            {
                var undos = new int[list.Count];
                for (var i = 0; i < list.Count; i++)
                    undos[i] = list[i].Hand.Engine.Play(list[i].Slot, cell);
                var win = this.Wins(list.Select(x => x.Hand).ToArray());
                for (var i = 0; i < list.Count; i++)
                    list[i].Hand.Engine.Undo(list[i].Slot, cell, undos[i]);
                if (!win)
                    return false;
            }

            return true;
        }
    }

    private sealed record Hand(GameEngine Engine, CardStats?[] Red, int Index);
}
