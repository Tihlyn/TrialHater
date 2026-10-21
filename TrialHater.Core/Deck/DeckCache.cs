using System.Text.Json;
using TrialHater.Core.Cards;

namespace TrialHater.Core.Deck;

// Deck search results kept between encounters, for one character's collection (the plugin keeps
// one cache per character). Every evaluation is deterministic (DeckEvaluator seeds its games from
// the deck), so a cached result is the result a new search would give for the same inputs:
// - a search is keyed by the NPC, its deck data, the evaluation rules, the search options and the
//   owned cards the search draws its candidates from (the top cards of each rarity tier). A new
//   card that makes one of those tiers is a new key, so a better deck from it is found; cards that
//   would not be tried do not cost a search.
// - a deck's score (finalists and saved decks) is keyed the same way plus its cards in order, so a
//   saved deck is scored once per NPC and rules, whatever else changes.
// Thread-safe: searches run on workers.
public sealed class DeckCache
{
    // Bump when the evaluation or the search changes in a way that changes results.
    public const int Version = 1;

    private const int MaxSearches = 400;
    private const int MaxScores = 4000;

    private readonly object gate = new();
    private readonly string dataFingerprint;
    private readonly Dictionary<string, SearchEntry> searches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScoreEntry> scores = new(StringComparer.Ordinal);
    private int changes;

    public DeckCache(string dataFingerprint)
    {
        this.dataFingerprint = dataFingerprint;
    }

    public int Searches
    {
        get
        {
            lock (this.gate)
                return this.searches.Count;
        }
    }

    public int Scores
    {
        get
        {
            lock (this.gate)
                return this.scores.Count;
        }
    }

    // Changes since the last ToJson: the owner saves when this moves.
    public int Changes => Volatile.Read(ref this.changes);

    // The searches kept, newest first, for display.
    public IReadOnlyList<(string Npc, string Rules, double Expected, DateTimeOffset At)> Entries
    {
        get
        {
            lock (this.gate)
            {
                return this.searches.Values.OrderByDescending(e => e.At)
                    .Select(e => (e.Npc, e.Rules, e.Best.FirstOrDefault()?.Expected ?? 0, e.At)).ToList();
            }
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.searches.Clear();
            this.scores.Clear();
            this.changes++;
        }
    }

    // The NPC's deck data, evaluation rules and search options: the part of every key that says
    // what the decks are scored against.
    public static string Context(NpcDeck npc, ushort evalRulesMask, string options)
        => $"v{Version}|{npc.TriadId}|{Hash(string.Join(",", npc.Fixed) + "/" + string.Join(",", npc.Variable))}|{evalRulesMask}|{Hash(options)}";

    public static string DeckKey(IEnumerable<CardInfo> cards) => string.Join(",", cards.Select(c => c.Id));

    public DeckSearchResult? Search(string context, string candidates, CardCatalog catalog)
    {
        lock (this.gate)
        {
            if (!this.searches.TryGetValue(context + "#" + candidates, out var entry))
                return null;
            var best = entry.Best.Select(s => s.ToScore(catalog)).ToList();
            if (best.Any(s => s is null))
                return null;
            entry.Used = DateTimeOffset.UtcNow;
            return new DeckSearchResult(best.Select(b => b!).ToList(), [], entry.Status, entry.At);
        }
    }

    public void PutSearch(string context, string candidates, string npc, string rules, IReadOnlyList<DeckScore> best, string status)
    {
        lock (this.gate)
        {
            var now = DateTimeOffset.UtcNow;
            this.searches[context + "#" + candidates] = new SearchEntry
            {
                Npc = npc,
                Rules = rules,
                Status = status,
                At = now,
                Used = now,
                Best = best.Select(StoredScore.From).ToList(),
            };
            Trim(this.searches, MaxSearches, e => e.Used);
            this.changes++;
        }
    }

    public DeckScore? Score(string context, IReadOnlyList<CardInfo> deck, CardCatalog catalog)
    {
        lock (this.gate)
        {
            if (!this.scores.TryGetValue(context + "#" + DeckKey(deck), out var entry))
                return null;
            entry.Used = DateTimeOffset.UtcNow;
            return entry.Score.ToScore(catalog);
        }
    }

    public void PutScore(string context, DeckScore score)
    {
        lock (this.gate)
        {
            this.scores[context + "#" + DeckKey(score.Cards)] = new ScoreEntry { Score = StoredScore.From(score), Used = DateTimeOffset.UtcNow };
            Trim(this.scores, MaxScores, e => e.Used);
            this.changes++;
        }
    }

    public string ToJson()
    {
        lock (this.gate)
        {
            var file = new CacheFile
            {
                Version = Version,
                Data = this.dataFingerprint,
                Searches = new Dictionary<string, SearchEntry>(this.searches),
                Scores = new Dictionary<string, ScoreEntry>(this.scores),
            };
            return JsonSerializer.Serialize(file);
        }
    }

    // An empty cache when the file is from another cache version or another game-data version
    // (a patch that changes cards or NPC decks).
    public static DeckCache FromJson(string json, string dataFingerprint)
    {
        var cache = new DeckCache(dataFingerprint);
        var file = JsonSerializer.Deserialize<CacheFile>(json);
        if (file is null || file.Version != Version || file.Data != dataFingerprint)
            return cache;
        foreach (var (key, entry) in file.Searches)
            cache.searches[key] = entry;
        foreach (var (key, entry) in file.Scores)
            cache.scores[key] = entry;
        return cache;
    }

    // The card list's identity: ids, rarity and sides of every card.
    public static string Fingerprint(CardCatalog catalog)
        => Hash(string.Join(";", catalog.All.OrderBy(c => c.Id).Select(c => $"{c.Id}:{c.Stars}:{c.Stats}")));

    // FNV-1a 64, as hex: stable across runs (string.GetHashCode is not).
    private static string Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16");
    }

    // Drops the least recently used tenth once over the limit.
    private static void Trim<T>(Dictionary<string, T> entries, int max, Func<T, DateTimeOffset> used)
    {
        if (entries.Count <= max)
            return;
        foreach (var key in entries.OrderBy(e => used(e.Value)).Take(entries.Count - (max * 9 / 10)).Select(e => e.Key).ToList())
            entries.Remove(key);
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }

        public string Data { get; set; } = string.Empty;

        public Dictionary<string, SearchEntry> Searches { get; set; } = new();

        public Dictionary<string, ScoreEntry> Scores { get; set; } = new();
    }

    private sealed class SearchEntry
    {
        public string Npc { get; set; } = string.Empty;

        public string Rules { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTimeOffset At { get; set; }

        public DateTimeOffset Used { get; set; }

        public List<StoredScore> Best { get; set; } = [];
    }

    private sealed class ScoreEntry
    {
        public StoredScore Score { get; set; } = new();

        public DateTimeOffset Used { get; set; }
    }

    private sealed class StoredScore
    {
        public ushort[] Cards { get; set; } = [];

        public int Games { get; set; }

        public double Win { get; set; }

        public double Draw { get; set; }

        public double Loss { get; set; }

        public bool Exact { get; set; }

        public double? PerfectWin { get; set; }

        public double? PerfectDraw { get; set; }

        public double? PerfectLoss { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public double Expected => this.Win - this.Loss;

        public static StoredScore From(DeckScore s) => new()
        {
            Cards = s.Cards.Select(c => c.Id).ToArray(),
            Games = s.Games,
            Win = s.Win,
            Draw = s.Draw,
            Loss = s.Loss,
            Exact = s.Exact,
            PerfectWin = s.PerfectWin,
            PerfectDraw = s.PerfectDraw,
            PerfectLoss = s.PerfectLoss,
        };

        // Null when a card is no longer in the card list.
        public DeckScore? ToScore(CardCatalog catalog)
        {
            var cards = this.Cards.Select(catalog.Get).ToList();
            if (cards.Any(c => c is null))
                return null;
            return new DeckScore(cards.Select(c => c!).ToList(), this.Games, this.Win, this.Draw, this.Loss, this.Exact, this.PerfectWin, this.PerfectDraw, this.PerfectLoss);
        }
    }
}
