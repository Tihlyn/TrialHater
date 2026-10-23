using System.Diagnostics;
using TrialHater.Core.Belief;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Search;

namespace TrialHater.Core.Policy;

public sealed record PolicyOptions
{
    // Exploitation is the expensive half; below this many placed cards the opening is chosen by
    // minimax alone (exploiting the opening costs seconds per hypothesis).
    public int ExploitMinPlaced { get; init; } = 1;

    public bool Exploit { get; init; } = true;

    public IOpponentModel Model { get; init; } = new GreedyOpponent(0.1);

    // Utility of a draw (win 1, loss -1). Sudden Death makes a draw worth a replay, not nothing.
    public double DrawValue { get; init; }

    // Time allowed for the exploitation half after the exact half finishes; on timeout the choice
    // falls back to minimax with a capture-count tie-break and says so.
    public TimeSpan ExploitBudget { get; init; } = TimeSpan.FromSeconds(3);

    // Hard limit on the exact half. The game's turn timer is 90 s; a Chaos opening needs ~10 s on
    // 16 threads, everything else well under one. On timeout the move falls back to captures.
    public TimeSpan SolveBudget { get; init; } = TimeSpan.FromSeconds(40);

    // 2^18 entries x 24 B per worker: same speed as 2^19 on the benchmark openings, half the memory.
    public int TableBits { get; init; } = 18;

    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 2);

    // The sure-win check (SureWin): time allowed and worker threads. On timeout the safe set is
    // left as the exact half found it. Zero turns the check off.
    public TimeSpan SureWinBudget { get; init; } = TimeSpan.FromSeconds(5);

    public int SureWinParallelism { get; init; } = 4;

    // When no move is a sure win, keep the moves that cannot lose whatever the hidden cards are
    // (a sure draw or better), if there are any, before maximising the expected value. Shares the
    // sure-win budget.
    public bool SureDrawCheck { get; init; } = true;

    // How far below the best expected minimax value a move may be and still be chosen for its
    // value against the opponent model (at the root and inside the exploitation search). 0 keeps
    // exploitation to exact ties; under Chaos values are averages and rarely tie. 2 or more lets
    // the model decide alone.
    public double ModelBand { get; init; }

    // Root values of early positions solved before (SolveCache), reused instead of solved again.
    public SolveCache? Cache { get; init; }

    // Play rather than stall when no hand fits what has been seen: solve over hands of any catalogue
    // cards instead (HandBelief.AnyHand), or failing that take the move that captures most now. The
    // reason starts with FALLBACK. The plugin sets it for Open Tournaments only; elsewhere NoBelief
    // means a wrong NPC or a mis-read card, and stopping is right. Temporary (2026-10-23) until
    // reports show why the belief fails in tournaments.
    public bool FallbackWithoutBelief { get; init; }
}

public sealed record MoveAssessment(Move Move, double ExpectedValue, double WorstValue, bool Safe, double? Exploit, int Captures);

public enum DecisionStatus
{
    Ready,
    NoMove,
    NoBelief,
    Cancelled,
}

public sealed record Decision(
    DecisionStatus Status,
    Move? Move,
    IReadOnlyList<MoveAssessment> Moves,
    int Hypotheses,
    string Belief,
    long Nodes,
    TimeSpan Elapsed,
    string Reason,
    string Fingerprint)
{
    public MoveAssessment? Chosen => this.Move is { } m ? this.Moves.FirstOrDefault(x => x.Move == m) : null;
}

// Chooses Blue's move for a MatchView: exact minimax per hypothesis, the moves with the best
// expected guaranteed value form the safe set, and the opponent model breaks ties inside it.
public static class MovePolicy
{
    private const double Tolerance = 1e-6;

    public static Decision Decide(MatchView view, NpcDeck? npc, CardCatalog catalog, PolicyOptions options, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var fingerprint = view.Fingerprint();
        if (view.ToMove != Side.Blue)
            return new Decision(DecisionStatus.NoMove, null, [], 0, string.Empty, 0, clock.Elapsed, "not our turn", fingerprint);
        if (view.Rules.HasUnresolvedRoulette)
            return new Decision(DecisionStatus.NoMove, null, [], 0, string.Empty, 0, clock.Elapsed, "Roulette has not resolved its rule yet", fingerprint);

        var belief = HandBelief.Build(view, npc, catalog);
        var fallback = string.Empty;
        if (!belief.Usable && options.FallbackWithoutBelief)
        {
            fallback = $"FALLBACK: no belief ({belief.Status}); ";
            belief = HandBelief.AnyHand(view, catalog);
            if (!belief.Usable)
                return MostCaptures(view, $"{fallback}{belief.Status}", clock, fingerprint);
        }

        if (!belief.Usable)
            return new Decision(DecisionStatus.NoBelief, null, [], 0, belief.Status, 0, clock.Elapsed, belief.Status, fingerprint);

        var hypotheses = belief.Hypotheses;
        var positions = hypotheses.Select(h => view.ToPosition(h.RedHand)).ToArray();
        var legal = positions[0].LegalMoves().ToList();
        if (legal.Count == 0)
            return new Decision(DecisionStatus.NoMove, null, [], hypotheses.Count, belief.Status, 0, clock.Elapsed, "no legal move", fingerprint);

        // Exact half: every root move under every hypothesis. The root is split into chunks so few
        // hypotheses still fill every worker (a Chaos opening with 3 hands used 3 of 16 cores);
        // moves in one chunk share a solver table.
        var captures = CaptureCounts(positions[0], legal);
        var results = new System.Collections.Concurrent.ConcurrentBag<(int H, MoveValue Value)>();

        // Hypotheses whose position was solved before take its stored (exact) values.
        var toSolve = new List<int>();
        for (var h = 0; h < positions.Length; h++)
        {
            if (options.Cache is { } cache && cache.TryGet(positions[h], out var known) && legal.All(m => known.Any(k => k.Move == m)))
            {
                foreach (var value in known)
                    results.Add((h, value));
            }
            else
            {
                toSolve.Add(h);
            }
        }

        var chunksPerHypothesis = Math.Clamp((int)Math.Ceiling(options.MaxParallelism / (double)Math.Max(1, toSolve.Count)), 1, legal.Count);
        var work = toSolve
            .SelectMany(h => legal.Select((m, i) => (h, m, i)).GroupBy(x => x.i % chunksPerHypothesis).Select(g => (H: h, Moves: g.Select(x => x.m).ToList())))
            .ToList();
        long nodes = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(options.SolveBudget);
        try
        {
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = budget.Token }, item =>
            {
                var solver = new Solver(positions[item.H], options.TableBits, budget.Token);
                try
                {
                    foreach (var value in solver.ScoreMoves(item.Moves))
                        results.Add((item.H, value));
                }
                finally
                {
                    Interlocked.Add(ref nodes, solver.Nodes);
                }
            });
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // Out of time on the exact half: say so and play the move that captures most now.
            var mostCaptures = legal.OrderByDescending(m => captures[m]).ThenBy(m => m.HandSlot).ThenBy(m => m.Cell).First();
            var partial = legal.Select(m => new MoveAssessment(m, double.NaN, double.NaN, true, null, captures[m])).OrderByDescending(a => a.Captures).ToList();
            return new Decision(DecisionStatus.Ready, mostCaptures, partial, hypotheses.Count, belief.Status, nodes, clock.Elapsed,
                $"{fallback}FALLBACK: exact search exceeded {options.SolveBudget.TotalSeconds:0}s; most immediate captures", fingerprint);
        }
        catch (OperationCanceledException)
        {
            return new Decision(DecisionStatus.Cancelled, null, [], hypotheses.Count, belief.Status, nodes, clock.Elapsed, "cancelled", fingerprint);
        }

        var values = new Dictionary<Move, double>[positions.Length];
        for (var h = 0; h < values.Length; h++)
            values[h] = new Dictionary<Move, double>(legal.Count);
        foreach (var (h, value) in results)
            values[h][value.Move] = value.Value;
        foreach (var h in toSolve)
            options.Cache?.Put(positions[h], [.. legal.Select(m => new MoveValue(m, values[h][m]))]);
        var cacheNote = options.Cache is not null && SolveCache.Applies(positions[0]) && toSolve.Count < positions.Length
            ? $"{positions.Length - toSolve.Count} of {positions.Length} hand(s) solved before; "
            : string.Empty;

        var expected = legal.ToDictionary(m => m, m => Enumerable.Range(0, positions.Length).Sum(h => hypotheses[h].Weight * values[h][m]));
        var worst = legal.ToDictionary(m => m, m => Enumerable.Range(0, positions.Length).Min(h => values[h][m]));

        // A win in every hypothesis is not a win without knowing which hand the NPC holds: one move
        // has to serve every hand we cannot tell apart, and the NPC knows its own (SureWin). Keep
        // the moves with the best such guarantee (sure wins, else sure draws or better, else all),
        // then the best expected value among them.
        var top = legal;
        var sureNote = string.Empty;
        var perHandWins = legal.Where(m => worst[m] >= 1 - Tolerance).ToList();
        var perHandNoLoss = options.SureDrawCheck ? legal.Where(m => worst[m] >= -Tolerance).ToList() : [];
        var checkWins = perHandWins.Count > 1 || (perHandWins.Count == 1 && perHandNoLoss.Count > 1);
        if ((checkWins || perHandNoLoss.Count > 1) && options.SureWinBudget > TimeSpan.Zero && SureWin.Applies(positions))
        {
            using var sureBudget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            sureBudget.CancelAfter(options.SureWinBudget);
            var workers = Math.Min(options.SureWinParallelism, options.MaxParallelism);
            try
            {
                var sure = checkWins ? SureWin.Moves(positions, perHandWins, workers, sureBudget.Token) : [];
                if (sure.Count > 0)
                {
                    top = [.. legal.Where(sure.Contains)];
                    sureNote = $"sure win: {sure.Count} of {perHandWins.Count} per-hand wins hold against every hand; ";
                }
                else
                {
                    sureNote = checkWins ? "no move wins against every hand unseen; " : string.Empty;
                    var noLoss = perHandNoLoss.Count > 1 ? SureWin.Moves(positions, perHandNoLoss, workers, sureBudget.Token, atLeast: 0) : [];
                    if (noLoss.Count > 0)
                    {
                        top = [.. legal.Where(noLoss.Contains)];
                        sureNote += $"sure draw: {noLoss.Count} of {perHandNoLoss.Count} per-hand non-losses hold against every hand; ";
                    }
                    else if (perHandNoLoss.Count > 1)
                    {
                        sureNote += "no move avoids a loss against every hand unseen; ";
                    }
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                top = legal;
                sureNote = $"sure-win check ran out ({options.SureWinBudget.TotalSeconds:0.#}s); ";
            }
            catch (OperationCanceledException)
            {
                return new Decision(DecisionStatus.Cancelled, null, [], hypotheses.Count, belief.Status, nodes, clock.Elapsed, "cancelled", fingerprint);
            }
        }

        var bestExpected = top.Max(m => expected[m]);
        var safe = top.Where(m => expected[m] >= bestExpected - Math.Max(Tolerance, options.ModelBand)).ToList();

        // Exploitation half, inside the safe set only.
        Dictionary<Move, double>? exploit = null;
        string reason;
        var placed = positions[0].Placed;
        if (safe.Count == 1)
        {
            reason = "only one move keeps the best guaranteed value";
        }
        else if (!options.Exploit)
        {
            reason = "exploitation off; tie broken by immediate captures";
        }
        else if (placed < options.ExploitMinPlaced)
        {
            reason = $"opening ({placed} placed): minimax, tie broken by immediate captures";
        }
        else
        {
            exploit = Exploit(positions, hypotheses, safe, options, cancel, ref nodes, out var timedOut);
            reason = timedOut
                ? $"exploit budget ({options.ExploitBudget.TotalSeconds:0.#}s) ran out; tie broken by immediate captures"
                : options.ModelBand > Tolerance
                    ? $"best of {safe.Count} moves within {options.ModelBand:0.##} of the best expected value against {options.Model.Name}"
                    : $"best of {safe.Count} safe moves against {options.Model.Name}";
            if (timedOut)
                exploit = null;
        }

        if (cancel.IsCancellationRequested)
            return new Decision(DecisionStatus.Cancelled, null, [], hypotheses.Count, belief.Status, nodes, clock.Elapsed, "cancelled", fingerprint);

        reason = fallback + cacheNote + sureNote + reason;
        var chosen = safe
            .OrderByDescending(m => exploit?.GetValueOrDefault(m, double.NegativeInfinity) ?? 0)
            .ThenByDescending(m => expected[m])
            .ThenByDescending(m => worst[m])
            .ThenByDescending(m => captures[m])
            .ThenBy(m => m.HandSlot)
            .ThenBy(m => m.Cell)
            .First();

        var assessments = legal
            .Select(m => new MoveAssessment(m, expected[m], worst[m], safe.Contains(m), exploit?.GetValueOrDefault(m), captures[m]))
            .OrderByDescending(a => a.Safe)
            .ThenByDescending(a => a.Exploit ?? double.NegativeInfinity)
            .ThenByDescending(a => a.ExpectedValue)
            .ToList();
        return new Decision(DecisionStatus.Ready, chosen, assessments, hypotheses.Count, belief.Status, nodes, clock.Elapsed, reason, fingerprint);
    }

    private static Dictionary<Move, double> Exploit(
        Position[] positions, IReadOnlyList<Hypothesis> hypotheses, List<Move> safe, PolicyOptions options,
        CancellationToken cancel, ref long nodes, out bool timedOut)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(options.ExploitBudget);
        var perHypothesis = new Dictionary<Move, double>[positions.Length];
        long used = 0;
        timedOut = false;
        try
        {
            Parallel.For(0, positions.Length, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = budget.Token }, h =>
            {
                var solver = new Solver(positions[h], options.TableBits, budget.Token);
                var evaluator = new ExploitEvaluator(solver, options.Model, options.DrawValue, budget.Token, options.ModelBand);
                perHypothesis[h] = safe.ToDictionary(m => m, evaluator.AfterMove);
                Interlocked.Add(ref used, solver.Nodes);
            });
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancel.IsCancellationRequested;
        }

        nodes += used;
        if (timedOut || perHypothesis.Any(p => p is null))
        {
            timedOut = true;
            return [];
        }

        return safe.ToDictionary(m => m, m => Enumerable.Range(0, positions.Length).Sum(h => hypotheses[h].Weight * perHypothesis[h][m]));
    }

    // The fallback's last resort: no hand for the hidden cards at all, so the move that captures most
    // now (the hidden slots stay empty: Blue's captures do not depend on them).
    private static Decision MostCaptures(MatchView view, string why, Stopwatch clock, string fingerprint)
    {
        var position = view.ToPosition(view.Red.Select(s => s.State == SlotState.Visible ? s.Card : (CardStats?)null).ToArray());
        var legal = position.LegalMoves().ToList();
        if (legal.Count == 0)
            return new Decision(DecisionStatus.NoMove, null, [], 0, why, 0, clock.Elapsed, "no legal move", fingerprint);
        var captures = CaptureCounts(position, legal);
        var move = legal.OrderByDescending(m => captures[m]).ThenBy(m => m.HandSlot).ThenBy(m => m.Cell).First();
        var assessments = legal.Select(m => new MoveAssessment(m, double.NaN, double.NaN, true, null, captures[m])).OrderByDescending(a => a.Captures).ToList();
        return new Decision(DecisionStatus.Ready, move, assessments, 0, why, 0, clock.Elapsed, $"{why}; most immediate captures", fingerprint);
    }

    // Cards Blue would own right after each move: the last tie-break, and shown in the UI.
    private static Dictionary<Move, int> CaptureCounts(Position position, List<Move> moves)
    {
        var engine = new Engine.Engine(position);
        var before = System.Numerics.BitOperations.PopCount((uint)engine.OwnerMask);
        var result = new Dictionary<Move, int>(moves.Count);
        foreach (var move in moves)
        {
            var undo = engine.Play(move.HandSlot, move.Cell);
            result[move] = System.Numerics.BitOperations.PopCount((uint)engine.OwnerMask) - before - 1;
            engine.Undo(move.HandSlot, move.Cell, undo);
        }

        return result;
    }
}
