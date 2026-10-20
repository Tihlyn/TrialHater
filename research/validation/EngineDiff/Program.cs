using AutoTripleTriadGrind.Core.Triad.Data;
using AutoTripleTriadGrind.Core.Triad.Logic;
using AutoTripleTriadGrind.Core.Triad.Logic.Rules;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;
using TriadCardType = AutoTripleTriadGrind.Core.Triad.Data.TriadCardType;
using TriadRule = TrialHater.Core.Rules.TriadRule;

// Usage: EngineDiff <cards.csv> [gamesPerRuleSet]
// Plays random games move by move in both engines and compares ownership after every placement.
// Deliberate departure (docs/RULES.md): our combo uses the placement comparison, Reverse and Fallen
// Ace included; ATTG's does not. Rule sets where that can matter (Same or Plus with Reverse or
// Fallen Ace) are reported but not counted as failures.
var catalog = CardCatalog.FromCsv(args.Length > 0 ? args[0] : "../../data/cards.csv");
var games = args.Length > 1 ? int.Parse(args[1]) : 5000;
var table = new TriadCard[catalog.All.Max(c => c.Id) + 1];
foreach (var c in catalog.All)
    table[c.Id] = new TriadCard(c.Id, c.Stats.Up, c.Stats.Right, c.Stats.Down, c.Stats.Left, (TriadCardType)(byte)c.Stats.Type, c.Stars, c.Id, 0);
TriadCards.Use(table);
var cards = catalog.All.ToList();

TriadRule[] capture = [TriadRule.Same, TriadRule.Plus, TriadRule.Reverse, TriadRule.FallenAce, TriadRule.Ascension, TriadRule.Descension];
var sets = new List<TriadRule[]>();
for (var mask = 0; mask < 1 << capture.Length; mask++)
{
    var set = capture.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
    if (set.Contains(TriadRule.Ascension) && set.Contains(TriadRule.Descension))
        continue;
    sets.Add(set);
}

var rng = new Random(20261019);
var totalMoves = 0;
var failures = 0;
foreach (var set in sets)
{
    var departs = (set.Contains(TriadRule.Same) || set.Contains(TriadRule.Plus)) && (set.Contains(TriadRule.Reverse) || set.Contains(TriadRule.FallenAce));
    var mismatch = 0;
    for (var g = 0; g < games; g++)
    {
        // Typed cards are over-represented so Ascension/Descension actually trigger.
        var deck = PickDeck(rng, cards, typedBias: set.Contains(TriadRule.Ascension) || set.Contains(TriadRule.Descension));
        var blueFirst = rng.Next(2) == 0;
        var ours = new Engine(new Position(RuleSet.Of(set), new BoardCard?[9],
            deck.Take(5).Select(c => (CardStats?)c.Stats).ToArray(), deck.Skip(5).Select(c => (CardStats?)c.Stats).ToArray(),
            blueFirst ? Side.Blue : Side.Red));
        var sim = new TriadSimulation();
        sim.Initialize(set.Select(r => TriadRules.Create((TriadRuleId)(byte)r)).ToList());
        var state = sim.StartGame(new TriadDeck(deck.Take(5).Select(c => c.Id).ToArray()), new TriadDeck(deck.Skip(5).Select(c => c.Id).ToArray()),
            blueFirst ? TriadGameStatus.InProgressBlue : TriadGameStatus.InProgressRed);
        while (!ours.IsFinished)
        {
            var hand = ours.MoverHand;
            var slots = Enumerable.Range(0, 5).Where(s => (hand & (1 << s)) != 0).ToArray();
            var empty = Enumerable.Range(0, 9).Where(c => ours.CellInstance(c) == Engine.Empty).ToArray();
            var slot = slots[rng.Next(slots.Length)];
            var cell = empty[rng.Next(empty.Length)];
            var owner = ours.BlueToMove ? TriadOwner.Blue : TriadOwner.Red;
            sim.PlaceCard(state, slot, state.TurnDeck, owner, cell);
            ours.Play(slot, cell);
            totalMoves++;
            var same = Enumerable.Range(0, 9).All(c => ours.CellInstance(c) == Engine.Empty
                ? state.Board[c].IsEmpty
                : !state.Board[c].IsEmpty && ours.IsBlue(c) == (state.Board[c].Owner == TriadOwner.Blue));
            if (!same)
            {
                mismatch++;
                if (mismatch <= 2 && !departs)
                    Console.WriteLine($"  [{string.Join(",", set)}] game {g} move {ours.Placed}: ours {string.Concat(Enumerable.Range(0, 9).Select(c => ours.CellInstance(c) == Engine.Empty ? '.' : ours.IsBlue(c) ? 'B' : 'R'))} attg {string.Concat(state.Board.Select(b => b.IsEmpty ? '.' : b.Owner == TriadOwner.Blue ? 'B' : 'R'))}");
                break;
            }
        }
    }

    if (!departs)
        failures += mismatch;
    Console.WriteLine($"{(set.Length == 0 ? "Plain" : string.Join("+", set)),-40} games {games,6}  mismatched games {mismatch}{(departs ? " (expected: combo departure)" : "")}");
}

Console.WriteLine($"rule sets {sets.Count}, placements {totalMoves}, mismatched games {failures}");
return failures == 0 ? 0 : 1;

static CardInfo[] PickDeck(Random rng, List<CardInfo> cards, bool typedBias)
{
    var pool = typedBias ? cards.Where(c => c.Stats.Type != CardType.None || rng.Next(3) == 0).ToList() : cards;
    return pool.OrderBy(_ => rng.Next()).Take(10).ToArray();
}

