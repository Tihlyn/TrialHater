using TrialHater.Core.Cards;

namespace TrialHater.Core.Tournament;

// Opponents' drafted decks within one Open Tournament. A drafted deck is kept for the whole
// tournament, so every card an opponent showed in one match is in its hand when we meet it again
// (2026-10-19: Fox Dragus twice). Keyed by the name the board shows. Several tournaments run in one
// rules slot and the same NPCs draft new decks in each (Jonas, 2026-10-20 17:00 slot), so the
// plugin clears it when a new draft starts; the slot change is a fallback.
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
        var cards = seen.ToList();
        if (!this.decks.TryGetValue(name, out var deck))
            this.decks[name] = deck = new List<CardStats>();
        var unmatched = new List<CardStats>(deck);
        var contradicted = false;
        foreach (var card in cards)
        {
            if (unmatched.Remove(card))
                continue;
            if (deck.Count < 5)
                deck.Add(card);
            else
                contradicted = true;
        }

        // A full deck that does not hold what was just seen is another deck (a new tournament).
        if (contradicted)
        {
            deck.Clear();
            deck.AddRange(cards.Take(5));
        }

        return deck;
    }

    // A new tournament began (its draft): nobody's deck is known any more.
    public void Clear() => this.decks.Clear();

    private void Roll(DateTimeOffset now)
    {
        var start = OpenTournament.LatestStart(now);
        if (start == this.tournament)
            return;
        this.tournament = start;
        this.decks.Clear();
    }
}
