using System.Diagnostics;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;
using TrialHater.Core.Rules;

namespace Arena;

internal sealed record GameResult(int Outcome, int BlueCards, bool BlueFirst, int Decisions, double MaxSeconds, double TotalSeconds, int SureNarrowed, int Fallbacks, int ExploitOut, string? Error);

// One simulated match against an NPC, dealt from its deck data. Everything random (the NPC's hand
// and slot order, who starts, Three Open reveals, the Swap exchange, Chaos draws, the NPC's own
// randomness) comes from the game seed, in separate streams, so two policies meet the same deal.
// Sudden Death is not replayed: a draw is a draw.
internal static class Game
{
    public static GameResult Play(Scenario scenario, NpcDeck npc, CardCatalog catalog, PolicyOptions policy, IOpponent opponent, int seed)
    {
        var rules = scenario.RuleSet;
        var deal = new Random(seed);
        var draws = new Random(Mix(seed, 1));
        var npcRandom = new Random(Mix(seed, 2));
        var (blueCards, redCards, blueFirst, shown, foreign, lost) = Deal(scenario, npc, catalog, deal);

        var position = new Position(rules, new BoardCard?[9], [.. blueCards.Select(c => (CardStats?)c)], [.. redCards.Select(c => (CardStats?)c)], blueFirst ? Side.Blue : Side.Red);
        var engine = new Engine(position);
        var bluePlayed = new bool[5];
        var redPlayed = new bool[5];
        var redPlayedCards = new List<CardStats>();
        int decisions = 0, narrowed = 0, fallbacks = 0, exploitOut = 0;
        double maxSeconds = 0, totalSeconds = 0;
        var chaos = rules.Has(TriadRule.Chaos);
        var order = rules.Has(TriadRule.Order);

        while (!engine.IsFinished)
        {
            var blue = engine.BlueToMove;
            var played = blue ? bluePlayed : redPlayed;
            var remaining = Enumerable.Range(0, 5).Where(s => !played[s]).ToList();
            var forced = chaos ? remaining[draws.Next(remaining.Count)] : order ? remaining[0] : -1;
            Move move;
            if (blue)
            {
                var view = new MatchView(rules, Board(engine),
                    [.. Enumerable.Range(0, 5).Select(s => bluePlayed[s] ? HandSlot.Empty : HandSlot.Of(blueCards[s]))],
                    [.. Enumerable.Range(0, 5).Select(s => redPlayed[s] ? HandSlot.Empty : shown[s] ? HandSlot.Of(redCards[s]) : HandSlot.Hidden)],
                    Side.Blue, forced, npc.TriadId)
                {
                    RedPlayed = [.. redPlayedCards],
                    RedForeign = foreign,
                    RedLost = lost,
                };
                var clock = Stopwatch.StartNew();
                var decision = MovePolicy.Decide(view, npc, catalog, policy);
                var seconds = clock.Elapsed.TotalSeconds;
                decisions++;
                totalSeconds += seconds;
                maxSeconds = Math.Max(maxSeconds, seconds);
                if (decision.Reason.Contains("sure win:", StringComparison.Ordinal) || decision.Reason.Contains("sure draw:", StringComparison.Ordinal))
                    narrowed++;
                if (decision.Reason.Contains("FALLBACK", StringComparison.Ordinal))
                    fallbacks++;
                if (decision.Reason.Contains("exploit budget", StringComparison.Ordinal))
                    exploitOut++;
                if (decision.Move is not { } chosen)
                    return new GameResult(0, 0, blueFirst, decisions, maxSeconds, totalSeconds, narrowed, fallbacks, exploitOut, $"no move: {decision.Reason}");
                move = chosen;
            }
            else
            {
                var state = new Position(rules, Board(engine),
                    [.. Enumerable.Range(0, 5).Select(s => bluePlayed[s] ? null : (CardStats?)blueCards[s])],
                    [.. Enumerable.Range(0, 5).Select(s => redPlayed[s] ? null : (CardStats?)redCards[s])], Side.Red)
                {
                    ForcedSlot = forced,
                    RedSelection = chaos ? CardSelection.Random : order ? CardSelection.Ordered : CardSelection.Free,
                };
                move = opponent.Choose(engine, state, npcRandom);
                redPlayedCards.Add(redCards[move.HandSlot]);
            }

            played[move.HandSlot] = true;
            engine.Play(move.HandSlot, move.Cell);
        }

        return new GameResult(engine.BlueOutcome, engine.BlueScore, blueFirst, decisions, maxSeconds, totalSeconds, narrowed, fallbacks, exploitOut, null);
    }

    // The deal of one game: both hands (after Swap), who starts, what we see of the NPC's hand.
    private static (CardStats[] Blue, CardStats[] Red, bool BlueFirst, bool[] Shown, CardStats[] Foreign, CardStats[] Lost) Deal(
        Scenario scenario, NpcDeck npc, CardCatalog catalog, Random deal)
    {
        var rules = scenario.RuleSet;

        // The NPC's hand: every fixed card plus random variable picks, in a random slot order.
        var redCards = npc.Fixed.Select(id => catalog.Get(id)!.Stats)
            .Concat(npc.Variable.OrderBy(_ => deal.Next()).Take(npc.VariablePicks).Select(id => catalog.Get(id)!.Stats))
            .OrderBy(_ => deal.Next()).ToArray();
        var blueCards = scenario.HandStats;
        var blueFirst = deal.Next(2) == 0;

        // What we can see of the NPC's hand.
        var shown = new bool[5];
        if (rules.Has(TriadRule.AllOpen))
            Array.Fill(shown, true);
        else if (rules.Has(TriadRule.ThreeOpen))
            foreach (var slot in Enumerable.Range(0, 5).OrderBy(_ => deal.Next()).Take(3))
                shown[slot] = true;

        // Swap: one card each way before the first turn; ours shows face up in the NPC's hand.
        CardStats[] foreign = [], lost = [];
        if (rules.Has(TriadRule.Swap))
        {
            var ours = deal.Next(5);
            var theirs = deal.Next(5);
            (blueCards[ours], redCards[theirs]) = (redCards[theirs], blueCards[ours]);
            foreign = [redCards[theirs]];
            lost = [blueCards[ours]];
            shown[theirs] = true;
        }

        return (blueCards, redCards, blueFirst, shown, foreign, lost);
    }

    // Our first decision of a game's deal when we open (bench): the empty board, our hand, the NPC's
    // hand as we would see it, our forced slot under Chaos or Order.
    public static MatchView OpeningView(Scenario scenario, NpcDeck npc, CardCatalog catalog, int seed, int chaosSlot)
    {
        var rules = scenario.RuleSet;
        var (blueCards, redCards, _, shown, foreign, lost) = Deal(scenario, npc, catalog, new Random(seed));
        var forced = rules.Has(TriadRule.Chaos) ? chaosSlot : rules.Has(TriadRule.Order) ? 0 : -1;
        return new MatchView(rules, new BoardCard?[9],
            [.. blueCards.Select(HandSlot.Of)],
            [.. Enumerable.Range(0, 5).Select(s => shown[s] ? HandSlot.Of(redCards[s]) : HandSlot.Hidden)],
            Side.Blue, forced, npc.TriadId)
        {
            RedForeign = foreign,
            RedLost = lost,
        };
    }

    public static int Mix(int a, int b)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static BoardCard?[] Board(Engine engine)
        => [.. Enumerable.Range(0, 9).Select(c => engine.CellInstance(c) == Engine.Empty
            ? (BoardCard?)null
            : new BoardCard(engine.CardOf(engine.CellInstance(c)), engine.IsBlue(c) ? Side.Blue : Side.Red))];
}
