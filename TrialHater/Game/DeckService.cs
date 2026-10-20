using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;

namespace TrialHater.Game;

// Runs the deck optimiser for one NPC on a worker, one search at a time; a new search cancels the
// previous one. The UI reads the properties (each one a single reference write from the worker).
public sealed class DeckService : IDisposable
{
    private readonly Action<Exception, string> error;
    private CancellationTokenSource? cancel;

    public DeckService(Action<Exception, string> error)
    {
        this.error = error;
    }

    public bool Running { get; private set; }

    public string Progress { get; private set; } = string.Empty;

    public string? Error { get; private set; }

    public NpcDeck? Npc { get; private set; }

    public RuleSet Rules { get; private set; }

    public string RulesSource { get; private set; } = string.Empty;

    public IReadOnlyList<SavedDeck> Saved { get; private set; } = [];

    public DeckSearchResult? Result { get; private set; }

    public void Start(NpcDeck npc, RuleSet rules, string rulesSource, IReadOnlyCollection<CardInfo> owned, IReadOnlyList<SavedDeck> saved, CardCatalog catalog)
    {
        this.Cancel();
        var source = new CancellationTokenSource();
        this.cancel = source;
        this.Npc = npc;
        this.Rules = rules;
        this.RulesSource = rulesSource;
        this.Saved = saved;
        this.Result = null;
        this.Error = null;
        this.Progress = $"starting ({owned.Count} owned cards)";
        this.Running = true;
        var token = source.Token;
        Task.Run(() =>
        {
            try
            {
                var result = DeckOptimizer.Find(npc, rules, owned, saved.Select(d => d.Cards).ToList(), catalog, new DeckSearchOptions(),
                    p => this.Progress = p, token);
                if (!token.IsCancellationRequested)
                {
                    this.Result = result;
                    this.Progress = "done";
                }
            }
            catch (OperationCanceledException)
            {
                this.Progress = "cancelled";
            }
            catch (Exception ex)
            {
                this.Error = ex.Message;
                this.error(ex, "[Deck] search failed");
            }
            finally
            {
                if (ReferenceEquals(this.cancel, source))
                    this.Running = false;
            }
        }, token);
    }

    public void Cancel()
    {
        this.cancel?.Cancel();
        this.cancel = null;
        this.Running = false;
    }

    public void Dispose() => this.Cancel();
}
