using System.Reflection;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TrialHater.Core.Cards;
using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;

namespace TrialHater.Game;

// Open Tournament draft advice: when the draft window shows its 15 cards, rank every deck they
// allow under the tournament's rules (DraftAdvisor, on a worker) for the main window to show. Also
// records every player's drafted deck from the tournament results window (tournament_decks.csv),
// which, with the decks shipped in the plugin (research/data/draft-decks.csv), is the advisor's
// picture of the field. Framework thread, except the advice itself.
public sealed unsafe class DraftService : IDisposable
{
    private const string DraftAddon = "TripleTriadPickUpDeckSelect";
    private const string ResultAddon = "TripleTriadTournamentResult";
    private const string ConfirmAddon = "TripleTriadDeckConfirmation";
    private const string ReportAddon = "TripleTriadTournamentMLReport";
    private const uint ReportOkNode = 7;
    private static readonly TimeSpan ReportDelay = TimeSpan.FromSeconds(2);

    // TripleTriadDeckConfirmation: the 5 picks as (name, 87000 + id, ...) in stride 5 from [0].
    private const int ConfirmIdBase = 1;
    private const int ConfirmStride = 5;

    // TripleTriadPickUpDeckSelect: the 15 offered cards as 87000 + id in stride 5 from [3].
    private const int OfferBase = 3;
    private const int OfferStride = 5;
    private const int OfferIconBase = 87000;

    // TripleTriadTournamentResult: per player (rank, name, points, captures, 5 x 88000 + id, 2 flags)
    // in stride 11 from [2].
    private const int ResultBase = 2;
    private const int ResultStride = 11;
    private const int ResultIconBase = 88000;

    private readonly Dalamud.Plugin.Services.IGameGui gameGui;
    private readonly string recordPath;
    private readonly Action<string> log;
    private readonly HashSet<string> recorded = new();
    private CancellationTokenSource? cancel;
    private string offerKey = string.Empty;
    private List<CardStats[]>? observed;
    private bool recordedLoaded;
    private DateTimeOffset? reportSeen;
    private bool reportPressed;

    public DraftService(Dalamud.Plugin.Services.IGameGui gameGui, string configFolder, Action<string> log)
    {
        this.gameGui = gameGui;
        this.recordPath = Path.Combine(configFolder, "tournament_decks.csv");
        this.log = log;
    }

    public bool Running { get; private set; }

    public DraftAdvice? Advice { get; private set; }

    public RuleSet Rules { get; private set; }

    public IReadOnlyList<CardInfo> Offer { get; private set; } = [];

    public string Status { get; private set; } = string.Empty;

    // Changes with every new draft offer: a new tournament began.
    public int OfferGeneration { get; private set; }

    // Rounds whose report window the plugin confirmed (auto play only).
    public int ReportsConfirmed { get; private set; }

    // While the draft's deck confirmation is up: the picked cards that differ from the advice
    // (empty when the deck is the advised one), or null when there is nothing to compare.
    public IReadOnlyList<(CardInfo Picked, CardInfo Advised)>? ConfirmDiff { get; private set; }

    public void Tick(GameData data, DateTimeOffset now, bool autoPlay)
    {
        this.RecordResults(now);
        if (autoPlay)
            this.ConfirmReport(now);
        var draft = this.Ready(DraftAddon);
        this.CompareConfirmation(data, draft != null);
        if (draft == null)
            return;
        var ids = Enumerable.Range(0, 15).Select(k => AddonOps.IntValue(draft, OfferBase + (OfferStride * k)) - OfferIconBase).ToList();
        if (ids.Any(id => id <= 0))
            return;
        var key = string.Join(",", ids.Order());
        if (key == this.offerKey)
            return;
        this.offerKey = key;
        this.OfferGeneration++;
        var offer = ids.Select(id => data.Cards.Get((ushort)id)).OfType<CardInfo>().ToList();
        if (offer.Count != 15)
        {
            this.Status = "the offer has cards missing from the card list";
            return;
        }

        var rules = CurrentRules(data, now);
        this.Start(offer, rules, data.Cards);
    }

    public void Dispose() => this.cancel?.Cancel();

    // The deck about to be confirmed against the advice (2026-10-20: a 3* row left on its
    // preselected card went unnoticed until the results). Only while the draft window is up: the
    // same confirmation window shows other players' decks on the results screen.
    private void CompareConfirmation(GameData data, bool drafting)
    {
        var confirm = drafting ? this.Ready(ConfirmAddon) : null;
        if (confirm == null || this.Advice is not { Picks.Count: 5 } advice)
        {
            this.ConfirmDiff = null;
            return;
        }

        var picked = Enumerable.Range(0, 5).Select(k => data.Cards.Get((ushort)(AddonOps.IntValue(confirm, ConfirmIdBase + (ConfirmStride * k)) - OfferIconBase))).ToList();
        if (picked.Any(c => c is null))
        {
            this.ConfirmDiff = null;
            return;
        }

        this.ConfirmDiff = picked.Select(p => (Picked: p!, Advised: advice.Picks.First(a => a.Stars == p!.Stars)))
            .Where(x => x.Picked.Id != x.Advised.Id).ToList();
    }

    // After each tournament match a report ("Round 2", "DRAW", "Points +1 Captures +0") waits for
    // its OK (TripleTriadTournamentMLReport, button node 7 -> [-1]; 2026-10-20). Pressed once per
    // appearance, after it has been up ReportDelay, through the button's own event.
    private void ConfirmReport(DateTimeOffset now)
    {
        var report = this.Ready(ReportAddon);
        if (report == null)
        {
            this.reportSeen = null;
            this.reportPressed = false;
            return;
        }

        this.reportSeen ??= now;
        if (this.reportPressed || now - this.reportSeen < ReportDelay)
            return;
        var result = AddonOps.ActivateButton(report, ReportOkNode);
        this.reportPressed = result.Sent;
        if (result.Sent)
        {
            this.ReportsConfirmed++;
            this.log($"[Tournament] {AddonOps.StringValue(report, 0)} report ({AddonOps.StringValue(report, 2)}, {AddonOps.StringValue(report, 4)}): OK pressed");
        }
    }

    // The rules of the tournament slot `now` falls in (tournaments run all through a slot).
    private static RuleSet CurrentRules(GameData data, DateTimeOffset now)
    {
        if (data.TournamentRules.Count == 0)
            return RuleSet.Of(TriadRule.Draft);
        return OpenTournament.Upcoming(data.TournamentRules, OpenTournament.LatestStart(now), 1).First().Rules;
    }

    private void Start(IReadOnlyList<CardInfo> offer, RuleSet rules, CardCatalog cards)
    {
        this.cancel?.Cancel();
        var source = new CancellationTokenSource();
        this.cancel = source;
        this.Offer = offer;
        this.Rules = rules;
        this.Advice = null;
        this.Running = true;
        this.Status = "ranking the 243 decks";
        var observedDecks = this.Observed(cards);
        var token = source.Token;
        Task.Run(() =>
        {
            try
            {
                var advice = DraftAdvisor.Advise(offer, rules, observedDecks, cards, token);
                if (token.IsCancellationRequested)
                    return;
                this.Advice = advice;
                this.Status = advice.Status;
                this.log($"[Draft] best deck {advice.Ranked.FirstOrDefault()?.Expected:+0.00;-0.00;0.00}: {string.Join(", ", advice.Picks.Select(c => $"{c.Stars}* {c.Name}"))} ({advice.Status})");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.Status = $"draft advice failed: {ex.Message}";
                this.log($"[Draft] {this.Status}");
            }
            finally
            {
                if (ReferenceEquals(this.cancel, source))
                    this.Running = false;
            }
        }, token);
    }

    // The decks shipped with the plugin plus the ones recorded here, read once.
    private List<CardStats[]> Observed(CardCatalog cards)
    {
        if (this.observed is { } known)
            return known;
        var lines = new List<string>();
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("draft-decks.csv"))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                    lines.Add(line);
            }
        }

        try
        {
            if (File.Exists(this.recordPath))
                lines.AddRange(File.ReadAllLines(this.recordPath));
        }
        catch (Exception ex)
        {
            this.log($"[Draft] could not read {this.recordPath}: {ex.Message}");
        }

        this.observed = DraftDecks.Parse(lines, cards);
        return this.observed;
    }

    // Every player's deck from the results window, once per tournament and player.
    private void RecordResults(DateTimeOffset now)
    {
        var result = this.Ready(ResultAddon);
        if (result == null || result->AtkValuesCount < ResultBase + (ResultStride * 8))
            return;
        this.LoadRecorded();
        var tournament = OpenTournament.LatestStart(now);
        var lines = new List<string>();
        for (var p = 0; p < 8; p++)
        {
            var row = ResultBase + (ResultStride * p);
            var name = AddonOps.StringValue(result, row + 1);
            var ids = Enumerable.Range(0, 5).Select(i => AddonOps.IntValue(result, row + 4 + i) - ResultIconBase).ToList();
            if (name.Length == 0 || ids.Any(id => id is <= 0 or > ushort.MaxValue))
                continue;
            var key = $"{name.Replace(',', ' ')}|{string.Join(",", ids)}";
            if (this.recorded.Add(key))
                lines.Add(DraftDecks.Line(tournament, name, ids.Select(id => (ushort)id)));
        }

        if (lines.Count == 0)
            return;
        try
        {
            string[] header = File.Exists(this.recordPath) ? [] : [DraftDecks.Header];
            File.AppendAllLines(this.recordPath, header.Concat(lines));
            this.observed = null;
            this.log($"[Draft] recorded {lines.Count} drafted deck(s) from the tournament results");
        }
        catch (Exception ex)
        {
            this.log($"[Draft] could not write {this.recordPath}: {ex.Message}");
        }
    }

    // Decks already in the file, so a results window still up after a reload is not added twice.
    private void LoadRecorded()
    {
        if (this.recordedLoaded)
            return;
        this.recordedLoaded = true;
        try
        {
            if (!File.Exists(this.recordPath))
                return;
            foreach (var fields in File.ReadLines(this.recordPath).Skip(1).Select(l => l.Split(',')).Where(f => f.Length >= 3))
                this.recorded.Add($"{fields[1]}|{fields[2].Replace(';', ',')}");
        }
        catch (Exception ex)
        {
            this.log($"[Draft] could not read {this.recordPath}: {ex.Message}");
        }
    }

    private AtkUnitBase* Ready(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        var addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }
}
