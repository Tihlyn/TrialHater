using TrialHater.Core.Cards;

namespace TrialHater.Core.Tournament;

// Opponents' drafted decks within one Open Tournament. A drafted deck is kept for the whole
// tournament, so every card an opponent showed in one match is in its hand when we meet it again
// (2026-10-19: Fox Dragus twice). Keyed by the name the board shows, and forgotten when the next
// tournament slot starts.
public sealed class TournamentDecks
{
    private readonly Dictionary<string, List<CardStats>> decks = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset tournament;

    public IReadOnlyList<CardStats> Get(string name, DateTimeOffset now)
    {
        this.Roll(now);
        return this.decks.TryGetValue(name, out var deck) ? deck : [];
    }

    // Adds the cards seen this match to what is known of the opponent's deck (as a multiset: a
    // card seen once in each of two matches is one card). Returns the known deck.
    public IReadOnlyList<CardStats> Record(string name, IEnumerable<CardStats> seen, DateTimeOffset now)
    {
        this.Roll(now);
        if (!this.decks.TryGetValue(name, out var deck))
            this.decks[name] = deck = new List<CardStats>();
        var unmatched = new List<CardStats>(deck);
        foreach (var card in seen)
        {
            if (!unmatched.Remove(card) && deck.Count < 5)
                deck.Add(card);
        }

        return deck;
    }

    private void Roll(DateTimeOffset now)
    {
        var start = OpenTournament.LatestStart(now);
        if (start == this.tournament)
            return;
        this.tournament = start;
        this.decks.Clear();
    }
}
