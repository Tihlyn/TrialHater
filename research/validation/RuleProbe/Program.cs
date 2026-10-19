using AutoTripleTriadGrind.Core.Triad.Logic;
using AutoTripleTriadGrind.Core.Triad.Logic.Rules;
using AutoTripleTriadGrind.Core.Triad.Data;
using AutoTripleTriadGrind.Tests;
using System.Text.Json;

TestCards.Install();
var sim = new TriadSimulation();
sim.Initialize([TriadRules.Create(TriadRuleId.SuddenDeath)]);
ushort[] hand = [TestCards.AllFives, TestCards.AllFives, TestCards.AllFives, TestCards.AllFives, TestCards.AllFives];
var state = sim.StartGame(new TriadDeck(hand), new TriadDeck(hand), TriadGameStatus.InProgressBlue);
var moves = 0;
while (!state.IsFinished && moves < 100)
{
    var cell = Array.FindIndex(state.Board, card => card.IsEmpty);
    var deck = state.TurnDeck;
    if (!sim.PlaceCard(state, deck.FirstAvailableCard(), deck, state.TurnOwner, cell))
        throw new Exception("Synthetic placement failed");
    moves++;
}
var a = new TriadSolverResult(40,35,100);
var b = new TriadSolverResult(45,50,100);
Console.WriteLine(JsonSerializer.Serialize(new {
    fixture = "Synthetic equal-sided cards to force consecutive draws; not a legal player deck",
    moves, restarts = state.NumRestarts, terminal = state.Status.ToString(),
    official_guide_expected_restarts = 5,
    sudden_death_matches_guide = state.NumRestarts == 5,
    score_probe = new { a_win = a.WinChance, b_win = b.WinChance, a_score = a.Score, b_score = b.Score,
                       lower_win_probability_preferred = a.IsBetterThan(b) }
}, new JsonSerializerOptions { WriteIndented = true }));
