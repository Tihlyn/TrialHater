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
        var chunksPerHypothesis = Math.Clamp((int)Math.Ceiling(options.MaxParallelism / (double)positions.Length), 1, legal.Count);
        var work = Enumerable.Range(0, positions.Length)
            .SelectMany(h => legal.Select((m, i) => (h, m, i)).GroupBy(x => x.i % chunksPerHypothesis).Select(g => (H: h, Moves: g.Select(x => x.m).ToList())))
            .ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<(int H, MoveValue Value)>();
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
            var fallback = legal.OrderByDescending(m => captures[m]).ThenBy(m => m.HandSlot).ThenBy(m => m.Cell).First();
            var partial = legal.Select(m => new MoveAssessment(m, double.NaN, double.NaN, true, null, captures[m])).OrderByDescending(a => a.Captures).ToList();
            return new Decision(DecisionStatus.Ready, fallback, partial, hypotheses.Count, belief.Status, nodes, clock.Elapsed,
                $"FALLBACK: exact search exceeded {options.SolveBudget.TotalSeconds:0}s; most immediate captures", fingerprint);
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

        var expected = legal.ToDictionary(m => m, m => Enumerable.Range(0, positions.Length).Sum(h => hypotheses[h].Weight * values[h][m]));
        var worst = legal.ToDictionary(m => m, m => Enumerable.Range(0, positions.Length).Min(h => values[h][m]));
        var bestExpected = expected.Values.Max();
        var safe = legal.Where(m => expected[m] >= bestExpected - Tolerance).ToList();

        // A win in every hypothesis is not a win without knowing which hand the NPC holds: when
        // some of these moves win against every hand (SureWin), keep only those.
        var sureNote = string.Empty;
        var perHandWins = legal.Where(m => worst[m] >= 1 - Tolerance).ToList();
        if (perHandWins.Count > 1 && options.SureWinBudget > TimeSpan.Zero && SureWin.Applies(positions))
        {
            using var sureBudget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            sureBudget.CancelAfter(options.SureWinBudget);
            try
            {
                var sure = SureWin.Moves(positions, perHandWins, Math.Min(options.SureWinParallelism, options.MaxParallelism), sureBudget.Token);
                if (sure.Count > 0 && sure.Count < safe.Count)
                    safe = safe.Where(sure.Contains).ToList();
                sureNote = sure.Count == 0
                    ? "no move wins against every hand unseen; "
                    : $"sure win: {sure.Count} of {perHandWins.Count} per-hand wins hold against every hand; ";
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                sureNote = $"sure-win check ran out ({options.SureWinBudget.TotalSeconds:0.#}s); ";
            }
            catch (OperationCanceledException)
            {
                return new Decision(DecisionStatus.Cancelled, null, [], hypotheses.Count, belief.Status, nodes, clock.Elapsed, "cancelled", fingerprint);
            }
        }

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
                : $"best of {safe.Count} safe moves against {options.Model.Name}";
            if (timedOut)
                exploit = null;
        }

        if (cancel.IsCancellationRequested)
            return new Decision(DecisionStatus.Cancelled, null, [], hypotheses.Count, belief.Status, nodes, clock.Elapsed, "cancelled", fingerprint);

        reason = sureNote + reason;
        var chosen = safe
            .OrderByDescending(m => exploit?.GetValueOrDefault(m, double.NegativeInfinity) ?? 0)
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
                var evaluator = new ExploitEvaluator(solver, options.Model, options.DrawValue, budget.Token);
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
