using TrialHater.Core.Engine;
using TrialHater.Core.Rules;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

// Matches recorded in game (plugin model probe + Cartographer), replayed through the engine. The
// owners after every placement must equal what the board showed before the next one.
public class LiveReplayTests
{
    // 2026-10-19, Ruhtwyda of the Three Hearts (TripleTriad 2293781), Ascension + Sudden Death.
    // Card text is URDL after correcting the addon's swapped left/right fields (docs/MODEL_MAPPING.md).
    // NPC hand, revealed over the match: Alphinaud & Alisaie, Minfilia, Nero tol Scaeva, Lahabrea,
    // Godbert Manderville - its 3 fixed cards and 2 of its variable pool.
    [Fact]
    public void Ruhtwyda_Ascension_2026_10_19()
    {
        var blue = Hand("5468", "7528", "8744", "7479", "7A91");
        var red = Hand("9339/Scion", "9835/Scion", "4187/Garlean", "4948", "6656");
        var engine = new Engine(Pos(RuleSet.Of(TriadRule.Ascension, TriadRule.SuddenDeath), EmptyBoard(), blue, red, Side.Blue));
        (int Slot, int Cell, string Owners)[] moves =
        [
            (1, 6, "......B.."),
            (2, 7, "......RR."),
            (0, 3, "...B..RR."),
            (1, 4, "...RR.RR."),
            (4, 8, "...RR.RRB"),
            (4, 0, "R..RR.RRB"),
            (3, 1, "BB.RR.RRB"),
            (0, 5, "BB.RRRRRB"),
            (2, 2, "BBBRRRRRB"),
        ];

        foreach (var (slot, cell, owners) in moves)
        {
            engine.Play(slot, cell);
            Assert.Equal(owners, Owners(engine));
        }

        Assert.Equal(-1, engine.BlueOutcome);
    }

    // 2026-10-19, Malevolent Weasel (TripleTriad 2293898), Three Open + Plus + Chaos. Every blue
    // play used the slot the board's command 6 forced. NPC hand: Ramuh and Thancred face down,
    // Hildibrand, Y'shtola and Ollier shown by Three Open.
    [Fact]
    public void MalevolentWeasel_PlusChaos_2026_10_19()
    {
        var blue = Hand("5468", "7528", "8744", "7479", "7A91");
        var red = Hand("8188/Primal", "2387/Scion", "18A8", "7814/Scion", "8563");
        var engine = new Engine(Pos(RuleSet.Of(TriadRule.ThreeOpen, TriadRule.Plus, TriadRule.Chaos), EmptyBoard(), blue, red, Side.Blue));
        (int Slot, int Cell, string Owners)[] moves =
        [
            (4, 6, "......B.."),
            (2, 3, "...R..R.."),
            (1, 0, "B..B..R.."),
            (1, 2, "B.RB..R.."),
            (0, 1, "BBRB..R.."),
            (4, 4, "BRRBR.R.."),
            (3, 5, "BRRBBBR.."),
            (0, 8, "BRRBBRR.R"),
            (2, 7, "BRRBBRBBR"),
        ];

        foreach (var (slot, cell, owners) in moves)
        {
            engine.Play(slot, cell);
            Assert.Equal(owners, Owners(engine));
        }

        Assert.Equal(0, engine.BlueOutcome);
    }

    // Regression: the first decision of the lost 2026-10-19 Chaos rematch (hindsight: every NPC card
    // known). Rounding at a Star1 bound left an empty search window and cell 5 scored -infinity.
    [Fact]
    public void MalevolentWeasel_ChaosValuesAreFinite()
    {
        var blue = Hand("5468", "7528", "8744", "7479", "7A91");
        var red = Hand("8188/Primal", "2387/Scion", "7814/Scion", "18A8", "8563");
        var position = Pos(RuleSet.Of(TriadRule.ThreeOpen, TriadRule.Plus, TriadRule.Chaos), EmptyBoard(), blue, red, Side.Blue) with { ForcedSlot = 3 };
        var moves = new Core.Search.Solver(position, 20).ScoreRootMoves();
        Assert.Equal(9, moves.Count);
        Assert.All(moves, m => Assert.InRange(m.Value, -1.0, 1.0));
        // Brute-force-checked solver (SolverTests.ChaosMatchesNaiveUnderTableCollisions): cell 2 is
        // the best at -460/14400, the move played; cell 5 is not -infinity. (Before exact
        // arithmetic it read -0.035, and the value moved with the table size.)
        Assert.Equal(-460 / 14400.0, moves.Single(m => m.Move.Cell == 2).Value, 12);
        Assert.Equal(moves.Max(m => m.Value), moves.Single(m => m.Move.Cell == 2).Value, 12);
        foreach (var bits in new[] { 12, 16 })
            Assert.Equal(moves, new Core.Search.Solver(position, bits).ScoreRootMoves());
    }

    // The first decision of that match as the plugin sees it: Three Open shows three of the four
    // fixed cards, so the hidden two are Ramuh plus one of three variable cards; the board's
    // command 6 forces slot 4.
    [Fact]
    public void MalevolentWeasel_FirstDecisionUsesTheForcedSlot()
    {
        var npc = Npcs.Get(2293898)!;
        var blue = new[] { "5468", "7528", "8744", "7479", "7A91" }.Select(c => Core.Match.HandSlot.Of(C(c))).ToArray();
        var red = new[] { Core.Match.HandSlot.Hidden, Core.Match.HandSlot.Hidden, Core.Match.HandSlot.Of(C("18A8")), Core.Match.HandSlot.Of(C("7814/Scion")), Core.Match.HandSlot.Of(C("8563")) };
        var view = new Core.Match.MatchView(RuleSet.Of(TriadRule.ThreeOpen, TriadRule.Plus, TriadRule.Chaos), EmptyBoard(), blue, red, Side.Blue, ForcedSlot: 4, NpcTriadId: npc.TriadId);
        var decision = Core.Policy.MovePolicy.Decide(view, npc, Catalog, new Core.Policy.PolicyOptions());
        Assert.Equal(Core.Policy.DecisionStatus.Ready, decision.Status);
        Assert.Equal(3, decision.Hypotheses);
        Assert.Equal(4, decision.Move!.Value.HandSlot);
        Assert.All(decision.Moves, m => Assert.Equal(4, m.Move.HandSlot));
    }
}
