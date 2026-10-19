using TrialHater.Core.Cards;
using TrialHater.Core.Match;
using TrialHater.Core.Policy;

namespace TrialHater.Game;

// Runs MovePolicy off the framework thread. One request per distinct view fingerprint; a newer
// view cancels the older search. The framework thread reads Latest and must compare its
// Fingerprint with the current view before acting on it.
public sealed class AnalysisService : IDisposable
{
    private readonly Action<Exception, string> logError;
    private CancellationTokenSource? running;
    private string? runningFingerprint;
    private volatile Decision? latest;

    public AnalysisService(Action<Exception, string> logError)
    {
        this.logError = logError;
    }

    public Decision? Latest => this.latest;

    public bool Busy => this.runningFingerprint is not null;

    public void Request(MatchView view, NpcDeck? npc, CardCatalog cards, PolicyOptions options)
    {
        var fingerprint = view.Fingerprint();
        if (fingerprint == this.runningFingerprint || fingerprint == this.latest?.Fingerprint)
            return;

        this.running?.Cancel();
        var cts = new CancellationTokenSource();
        this.running = cts;
        this.runningFingerprint = fingerprint;
        Task.Run(() =>
        {
            try
            {
                var decision = MovePolicy.Decide(view, npc, cards, options, cts.Token);
                if (!cts.IsCancellationRequested)
                    this.latest = decision;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logError(ex, "[Analysis] policy failed");
            }
            finally
            {
                if (ReferenceEquals(this.running, cts))
                {
                    this.runningFingerprint = null;
                    this.running = null;
                }

                cts.Dispose();
            }
        });
    }

    public void Clear()
    {
        this.running?.Cancel();
        this.running = null;
        this.runningFingerprint = null;
        this.latest = null;
    }

    public void Dispose() => this.Clear();
}
