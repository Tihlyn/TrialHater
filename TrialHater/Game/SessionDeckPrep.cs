using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

namespace TrialHater.Game;

// Before a farming session talks to its NPC: search the best deck from the collection against it
// (DeckOptimizer.Find), write it into the allowed deck slot when it is new and clearly better than
// every saved deck, and hand the session's deck chooser the deck to pick. The session waits while
// this runs (Pending). Framework thread, except the search itself.
public sealed class SessionDeckPrep : IDisposable
{
    // A new deck is written only if it beats the best saved deck by this much expected result per
    // game (win +1 / loss -1): the evaluation is a few dozen simulated games per deal, so smaller
    // differences are noise and not worth rewriting a slot for.
    public const double WriteMargin = 0.10;

    private readonly Action<string> log;
    private CancellationTokenSource? cancel;
    private Task<DeckSearchResult>? search;
    private IReadOnlyList<SavedDeck> saved = [];
    private DeckScore? writing;
    private int slot = -1;
    private string npcName = string.Empty;
    private string progress = string.Empty;
    private Action? fallback;

    public SessionDeckPrep(Action<string> log)
    {
        this.log = log;
    }

    public bool Running => this.search is not null || this.writing is not null;

    public string Summary { get; private set; } = string.Empty;

    // Why the session should wait, or null.
    public string? Pending => this.search is not null ? $"building the best deck against {this.npcName}: {this.progress}"
        : this.writing is not null ? $"writing the new deck into Deck {this.slot + 1}"
        : null;

    // `fallback` ranks the saved decks the usual way, if building fails.
    public void Start(NpcDeck npc, RuleSet rules, IReadOnlyCollection<CardInfo> owned, IReadOnlyList<SavedDeck> savedDecks,
        CardCatalog catalog, DeckCache? cache, int writeSlot, Action fallback)
    {
        this.Cancel();
        this.fallback = fallback;
        var source = new CancellationTokenSource();
        this.cancel = source;
        this.saved = savedDecks;
        this.slot = writeSlot;
        this.npcName = npc.Name;
        this.progress = "starting";
        this.Summary = string.Empty;
        var token = source.Token;
        this.search = Task.Run(() => DeckOptimizer.Find(npc, rules, owned, savedDecks.Select(d => d.Cards).ToList(), catalog,
            new DeckSearchOptions(), p => this.progress = p, token, cache), token);
        this.log($"[Deck] session: building the best deck against {npc.Name} [{rules}]");
    }

    public void Cancel()
    {
        this.cancel?.Cancel();
        this.cancel = null;
        this.search = null;
        this.writing = null;
    }

    public void Dispose() => this.Cancel();

    public void Tick(DeckWriter writer, SessionDeckChooser chooser)
    {
        if (this.search is { IsCompleted: true } done)
        {
            this.search = null;
            if (done.IsCompletedSuccessfully)
                this.Decide(done.Result, writer, chooser);
            else
                this.Fail($"the deck search failed ({done.Exception?.GetBaseException().Message ?? "cancelled"})");
            return;
        }

        if (this.writing is { } deck && !writer.Active)
        {
            this.writing = null;
            if (writer.Written is { } written && written.Slot == this.slot && written.Cards.SequenceEqual(deck.Cards.Select(c => c.Id)))
            {
                this.Summary = $"wrote Deck {this.slot + 1} ({deck.Expected:+0.00;-0.00;0.00}) and plays it";
                chooser.Use(new SavedDeck(this.slot, $"Deck {this.slot + 1}", deck.Cards), $"Deck {this.slot + 1}, just built ({deck.Expected:+0.00;-0.00;0.00})");
            }
            else
            {
                this.Fail($"writing Deck {this.slot + 1} did not finish ({writer.Status})");
            }
        }
    }

    private void Decide(DeckSearchResult result, DeckWriter writer, SessionDeckChooser chooser)
    {
        if (result.CachedAt is { } at)
            this.log($"[Deck] session: reused the search of {at.ToLocalTime():g} (deck cache: no new card would change it)");
        var bestSaved = Enumerable.Range(0, Math.Min(this.saved.Count, result.Saved.Count))
            .OrderByDescending(i => result.Saved[i].Expected)
            .ThenBy(i => this.saved[i].Index)
            .Select(i => (Deck: this.saved[i], Score: result.Saved[i]))
            .FirstOrDefault();
        if (result.Best.Count == 0)
        {
            // Random, or no deck data: nothing to build; the chooser resets and decides as usual.
            this.Summary = result.Status;
            this.log($"[Deck] session: {result.Status}");
            this.fallback?.Invoke();
            return;
        }

        var best = result.Best[0];
        var alreadySaved = this.saved.FirstOrDefault(d => d.Cards.Select(c => c.Id).Order().SequenceEqual(best.Cards.Select(c => c.Id).Order()));
        if (alreadySaved is not null || (bestSaved.Deck is not null && best.Expected - bestSaved.Score.Expected < WriteMargin))
        {
            var (deck, score) = alreadySaved is not null
                ? (alreadySaved, result.Saved[this.saved.ToList().IndexOf(alreadySaved)])
                : bestSaved;
            this.Summary = alreadySaved is not null
                ? $"the best deck is already saved as {deck.Name}"
                : $"no new deck clearly better than {deck.Name} (best found {best.Expected:+0.00;-0.00;0.00} vs {score.Expected:+0.00;-0.00;0.00})";
            chooser.Use(deck, $"{deck.Name} ({score.Expected:+0.00;-0.00;0.00}), {this.Summary}");
            return;
        }

        this.writing = best;
        this.Summary = $"writing a new deck into Deck {this.slot + 1}";
        this.log($"[Deck] session: best deck {best.Expected:+0.00;-0.00;0.00} beats the saved decks"
                 + (bestSaved.Deck is null ? string.Empty : $" (best saved {bestSaved.Deck.Name} {bestSaved.Score.Expected:+0.00;-0.00;0.00})")
                 + $"; writing it into Deck {this.slot + 1}");
        writer.Start(this.slot, best.Cards);
    }

    // The session goes on with the saved decks, ranked as without deck building.
    private void Fail(string why)
    {
        this.Summary = $"{why}; picking among the saved decks";
        this.log($"[Deck] session: {this.Summary}");
        this.fallback?.Invoke();
    }
}
