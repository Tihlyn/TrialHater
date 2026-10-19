using TrialHater.Core.Engine;
using TrialHater.Core.Rules;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

// Board cells:   0 1 2      Card text "URDL": up, right, down, left (A = 10).
//                3 4 5
//                6 7 8
public class RuleTests
{
    private static readonly CardStatsHand Filler = new();

    [Fact]
    public void HigherSideCaptures()
    {
        var p = Pos(RuleSet.Empty, Board(null, R("1113")), Hand("1511"), Filler.Red);
        Assert.Equal("BB.......", OwnersAfter(p, 0, 0));
    }

    [Fact]
    public void EqualSidesDoNotCapture()
    {
        var p = Pos(RuleSet.Empty, Board(null, R("1115")), Hand("1511"), Filler.Red);
        Assert.Equal("BR.......", OwnersAfter(p, 0, 0));
    }

    [Fact]
    public void OwnCardsAreNeverFlipped()
    {
        var p = Pos(RuleSet.Empty, Board(null, B("1113")), Hand("1511"), Filler.Red);
        Assert.Equal("BB.......", OwnersAfter(p, 0, 0));
    }

    [Fact]
    public void SameNeedsTwoMatchingSides()
    {
        // Up 4 = 4 and left 6 = 6: both flip. Right 2 vs 9 does not.
        var board = Board(null, R("1141"), null, R("1611"), null, R("1119"));
        var blue = Hand("4296");
        Assert.Equal(".B.BBR...", OwnersAfter(Pos(RuleSet.Of(TriadRule.Same), board, blue, Filler.Red), 0, 4));
        Assert.Equal(".R.RBR...", OwnersAfter(Pos(RuleSet.Empty, board, blue, Filler.Red), 0, 4));
    }

    [Fact]
    public void SameCountsAFriendlyCardAsAMatch()
    {
        var board = Board(null, B("1141"), null, R("1611"));
        Assert.Equal(".B.BB....", OwnersAfter(Pos(RuleSet.Of(TriadRule.Same), board, Hand("4296"), Filler.Red), 0, 4));
    }

    [Fact]
    public void SameWithOneMatchDoesNothing()
    {
        var board = Board(null, R("1141"), null, R("1711"));
        Assert.Equal(".R.RB....", OwnersAfter(Pos(RuleSet.Of(TriadRule.Same), board, Hand("4296"), Filler.Red), 0, 4));
    }

    [Fact]
    public void PlusMatchesSums()
    {
        // 3 + 4 = 7 above and 2 + 5 = 7 to the left; neither would capture plainly.
        var board = Board(null, R("1141"), null, R("1511"));
        Assert.Equal(".B.BB....", OwnersAfter(Pos(RuleSet.Of(TriadRule.Plus), board, Hand("3112"), Filler.Red), 0, 4));
        Assert.Equal(".R.RB....", OwnersAfter(Pos(RuleSet.Empty, board, Hand("3112"), Filler.Red), 0, 4));
    }

    [Fact]
    public void ComboChainsFromSameCaptures()
    {
        // Same flips cells 1 and 3; cell 1 (right 9) then takes cell 2 (left 2) by combo; cell 0
        // holds because 9 against 9 is a tie.
        var board = Board(R("1911"), R("1949"), R("1112"), R("1611"));
        Assert.Equal("RBBBB....", OwnersAfter(Pos(RuleSet.Of(TriadRule.Same), board, Hand("4116"), Filler.Red), 0, 4));
    }

    [Fact]
    public void PlainCapturesDoNotStartACombo()
    {
        // Cell 1 is taken plainly (9 > 1); its right 9 would beat cell 2's left 2, but a plain
        // capture never starts a combo.
        var board = Board(null, R("1911"), R("1112"));
        Assert.Equal("BBR......", OwnersAfter(Pos(RuleSet.Of(TriadRule.Same, TriadRule.Plus), board, Hand("1911"), Filler.Red), 0, 0));
    }

    [Fact]
    public void ComboIgnoresReverse()
    {
        // As ComboChainsFromSameCaptures, under Reverse: the combo still compares plainly (9 > 2).
        var board = Board(R("1911"), R("1949"), R("1112"), R("1611"));
        var rules = RuleSet.Of(TriadRule.Same, TriadRule.Reverse);
        Assert.Equal("RBBBB....", OwnersAfter(Pos(rules, board, Hand("4116"), Filler.Red), 0, 4));
    }

    [Fact]
    public void ReverseCapturesWithTheLowerSide()
    {
        var board = Board(null, R("1115"));
        Assert.Equal("BB.......", OwnersAfter(Pos(RuleSet.Of(TriadRule.Reverse), board, Hand("1211"), Filler.Red), 0, 0));
        Assert.Equal("BR.......", OwnersAfter(Pos(RuleSet.Of(TriadRule.Reverse), board, Hand("1911"), Filler.Red), 0, 0));
    }

    [Fact]
    public void FallenAceLetsAOneTakeAnAce()
    {
        var board = Board(null, R("111A"));
        Assert.Equal("BB.......", OwnersAfter(Pos(RuleSet.Of(TriadRule.FallenAce), board, Hand("1111"), Filler.Red), 0, 0));
        Assert.Equal("BR.......", OwnersAfter(Pos(RuleSet.Empty, board, Hand("1111"), Filler.Red), 0, 0));
    }

    [Fact]
    public void FallenAceUnderReverseLetsAnAceTakeAOne()
    {
        var board = Board(null, R("1111"));
        Assert.Equal("BB.......", OwnersAfter(Pos(RuleSet.Of(TriadRule.FallenAce, TriadRule.Reverse), board, Hand("1A11"), Filler.Red), 0, 0));
        Assert.Equal("BR.......", OwnersAfter(Pos(RuleSet.Of(TriadRule.Reverse), board, Hand("1A11"), Filler.Red), 0, 0));
    }

    [Fact]
    public void AscensionCountsTypedCardsAlreadyOnTheBoard()
    {
        // Right 4 against left 5. Two Primals already down: 4 + 2 = 6 > 5.
        var two = Board(null, R("1115"), null, null, null, null, B("1111/Primal"), null, B("1111/Primal"));
        var one = Board(null, R("1115"), null, null, null, null, null, null, B("1111/Primal"));
        var rules = RuleSet.Of(TriadRule.Ascension);
        Assert.Equal("BB....B.B", OwnersAfter(Pos(rules, two, Hand("1411/Primal"), Filler.Red), 0, 0));
        Assert.Equal("BR......B", OwnersAfter(Pos(rules, one, Hand("1411/Primal"), Filler.Red), 0, 0));
    }

    [Fact]
    public void AscensionRaisesBothOwnersCards()
    {
        // The red Primal is lifted by the same +2: 6 against 7, no capture.
        var board = Board(null, R("1115/Primal"), null, null, null, null, B("1111/Primal"));
        Assert.Equal("BR....B..", OwnersAfter(Pos(RuleSet.Of(TriadRule.Ascension), board, Hand("1411/Primal"), Filler.Red), 0, 0));
    }

    [Fact]
    public void DescensionLowersTypedCards()
    {
        // Red Primal left 6, untyped right 5: one Primal down (itself) makes it 5 - a tie; a second makes it 4.
        var rules = RuleSet.Of(TriadRule.Descension);
        Assert.Equal("BR.......", OwnersAfter(Pos(rules, Board(null, R("1116/Primal")), Hand("1511"), Filler.Red), 0, 0));
        Assert.Equal("BB......B", OwnersAfter(Pos(rules, Board(null, R("1116/Primal"), null, null, null, null, null, null, B("1111/Primal")), Hand("1511"), Filler.Red), 0, 0));
    }

    [Fact]
    public void TypeShiftClampsToOneAndAce()
    {
        var up = new Engine(Pos(RuleSet.Of(TriadRule.Ascension), Board(B("1111/Primal"), B("1111/Primal")), Hand("AAAA/Primal"), Filler.Red));
        Assert.Equal(10, up.Value(0, 0));
        var down = new Engine(Pos(RuleSet.Of(TriadRule.Descension), Board(B("1111/Primal"), B("1111/Primal")), Hand("1111/Primal"), Filler.Red));
        Assert.Equal(1, down.Value(0, 0));
    }

    [Fact]
    public void OrderOnlyAllowsTheLeftmostCard()
    {
        var p = Pos(RuleSet.Of(TriadRule.Order), EmptyBoard(), Hand(null, "5555", "6666"), Filler.Red);
        Assert.All(p.LegalMoves(), m => Assert.Equal(1, m.HandSlot));
        Assert.Equal(9, p.LegalMoves().Count());
    }

    [Fact]
    public void ForcedSlotRestrictsTheRoot()
    {
        var p = Pos(RuleSet.Of(TriadRule.Chaos), EmptyBoard(), Hand("1111", "5555", "6666"), Filler.Red) with { ForcedSlot = 2 };
        Assert.All(p.LegalMoves(), m => Assert.Equal(2, m.HandSlot));
    }

    [Fact]
    public void TheCardLeftInHandCountsForItsOwner()
    {
        // Blue opened, so Blue keeps one card: 4 board cards + 1 in hand = 5 of 10, a draw.
        var board = Board(B("1111"), R("1111"), B("1111"), R("1111"), B("1111"), R("1111"), B("1111"), R("1111"), R("1111"));
        var engine = new Engine(Pos(RuleSet.Empty, board, Hand("1111"), Hand(), Side.Red));
        Assert.True(engine.IsFinished);
        Assert.Equal(5, engine.BlueScore);
        Assert.Equal(0, engine.BlueOutcome);
    }

    [Fact]
    public void UndoRestoresTheExactState()
    {
        var board = Board(R("1911"), R("1949"), R("1112"), R("1611"));
        var engine = new Engine(Pos(RuleSet.Of(TriadRule.Same, TriadRule.Ascension), board, Hand("4116/Scion", "2222"), Filler.Red));
        var key = engine.Key;
        var owners = Owners(engine);
        var undo = engine.Play(0, 4);
        Assert.NotEqual(owners, Owners(engine));
        engine.Undo(0, 4, undo);
        Assert.Equal(key, engine.Key);
        Assert.Equal(owners, Owners(engine));
        Assert.Equal(0b11, engine.BlueHand);
        Assert.True(engine.BlueToMove);
    }

    // Red's hand only has to exist for these fixtures; it is never played.
    private sealed class CardStatsHand
    {
        public Core.Cards.CardStats?[] Red { get; } = Hand("1111", "1111", "1111", "1111");
    }
}
