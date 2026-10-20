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

    public void Tick(GameData data, DateTimeOffset now)
    {
        this.RecordResults(now);
        var draft = this.Ready(DraftAddon);
        if (draft == null)
            return;
        var ids = Enumerable.Range(0, 15).Select(k => AddonOps.IntValue(draft, OfferBase + (OfferStride * k)) - OfferIconBase).ToList();
        if (ids.Any(id => id <= 0))
            return;
        var key = string.Join(",", ids.Order());
        if (key == this.offerKey)
            return;
        this.offerKey = key;
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
