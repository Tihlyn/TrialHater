using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

namespace TrialHater.Game;

// Picks the saved deck a farming session plays: the saved decks are ranked against the session
// NPC on a worker (DeckOptimizer.RankSaved), and the session waits at the deck window until that
// is done, falling back to the window's own confirm button if it fails or takes too long.
public sealed class SessionDeckChooser : IDisposable
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly Action<string> log;
    private CancellationTokenSource? cancel;
    private DateTime startedAt;

    public SessionDeckChooser(Action<string> log)
    {
        this.log = log;
    }

    // Row in the deck window (= saved deck slot) and its card ids, or -1 for the default.
    public int Row { get; private set; } = -1;

    public IReadOnlyList<ushort> Cards { get; private set; } = [];

    public bool Running { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    // Whether the ranking used the sheet rules only (no match against the NPC recorded yet), so a
    // first match's regional rules can change the choice.
    public bool SheetRulesOnly { get; private set; }

    // Non-null while the session should wait at the deck window.
    public string? Pending
    {
        get
        {
            if (!this.Running)
                return null;
            if (DateTime.UtcNow - this.startedAt < GiveUpAfter)
                return "ranking the saved decks against this NPC";
            this.Cancel();
            this.Summary = "ranking took too long; using the default deck";
            this.log($"[Deck] {this.Summary}");
            return null;
        }
    }

    public void Start(NpcDeck npc, RuleSet rules, bool sheetRulesOnly, IReadOnlyList<SavedDeck> saved, CardCatalog catalog)
    {
        this.Cancel();
        this.Row = -1;
        this.Cards = [];
        this.SheetRulesOnly = sheetRulesOnly;
        if (saved.Count == 0)
        {
            this.Summary = "no complete saved deck; using the default deck";
            return;
        }

        if (saved.Count == 1)
        {
            this.Choose(saved[0], $"{saved[0].Name} (the only saved deck)");
            return;
        }

        var source = new CancellationTokenSource();
        this.cancel = source;
        this.startedAt = DateTime.UtcNow;
        this.Running = true;
        this.Summary = "ranking the saved decks";
        var token = source.Token;
        Task.Run(() =>
        {
            try
            {
                var scores = DeckOptimizer.RankSaved(npc, rules, saved.Select(d => d.Cards).ToList(), catalog, new DeckEvaluationOptions(), token);
                if (token.IsCancellationRequested)
                    return;
                if (scores.Count != saved.Count)
                {
                    this.Summary = "the deck does not matter here (or no NPC deck data); using the default deck";
                    return;
                }

                // Best expected result; then perfect-play margin; then the lower slot.
                var best = Enumerable.Range(0, saved.Count)
                    .OrderByDescending(i => scores[i].Expected)
                    .ThenByDescending(i => (scores[i].PerfectWin ?? 0) - (scores[i].PerfectLoss ?? 0))
                    .ThenBy(i => saved[i].Index)
                    .First();
                var table = string.Join(", ", Enumerable.Range(0, saved.Count).Select(i => $"{saved[i].Name} {scores[i].Expected:+0.00;-0.00;0.00}"));
                this.Choose(saved[best], $"{saved[best].Name} for {npc.Name} [{rules}] ({table})");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.Summary = $"ranking failed ({ex.Message}); using the default deck";
                this.log($"[Deck] {this.Summary}");
            }
            finally
            {
                if (ReferenceEquals(this.cancel, source))
                    this.Running = false;
            }
        }, token);
    }

    // Take a deck decided elsewhere (the session's deck building), without ranking.
    public void Use(SavedDeck deck, string why, bool sheetRulesOnly)
    {
        this.Cancel();
        this.SheetRulesOnly = sheetRulesOnly;
        this.Choose(deck, why);
    }

    public void Cancel()
    {
        this.cancel?.Cancel();
        this.cancel = null;
        this.Running = false;
    }

    public void Dispose() => this.Cancel();

    private void Choose(SavedDeck deck, string why)
    {
        this.Cards = deck.Cards.Select(c => c.Id).ToList();
        this.Row = deck.Index;
        this.Summary = $"session deck: {why}";
        this.log($"[Deck] {this.Summary}");
    }
}
