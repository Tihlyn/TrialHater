using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Tournament;

namespace TrialHater.Game;

// What the plugin keeps per character, since characters on one account own different cards:
// the deck cache (deck_cache.json) and our Open Tournament drafts with their results
// (draft_results.csv), under characters/<content id>/ in the plugin's config folder. Logged out,
// or before the card sheets are in, there is none. Framework thread; the cache itself is
// thread-safe for the searches that use it.
public sealed class CharacterData : IDisposable
{
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private readonly string root;
    private readonly Action<string> log;
    private ulong contentId;
    private string folder = string.Empty;
    private int savedChanges;
    private DateTime lastSave;

    public CharacterData(string configFolder, Action<string> log)
    {
        this.root = Path.Combine(configFolder, "characters");
        this.log = log;
    }

    public string Name { get; private set; } = string.Empty;

    public DeckCache? Cache { get; private set; }

    public DraftRecord? Drafts { get; private set; }

    // `contentId` 0 = logged out.
    public void Update(ulong id, string name, CardCatalog cards, DateTime now)
    {
        if (id != this.contentId)
            this.Switch(id, name, cards);
        if (id != 0 && name.Length > 0)
            this.Name = name;
        if (this.Cache is { } cache && cache.Changes != this.savedChanges && now - this.lastSave >= SaveEvery)
        {
            this.lastSave = now;
            this.SaveCache();
        }
    }

    // Appends one tournament's result; false when it was recorded already.
    public bool AddDraft(DraftResult result)
    {
        if (this.Drafts is not { } drafts || !drafts.Add(result))
            return false;
        var path = Path.Combine(this.folder, "draft_results.csv");
        try
        {
            string[] header = File.Exists(path) ? [] : [DraftRecord.Header];
            File.AppendAllLines(path, header.Append(DraftRecord.Line(result)));
        }
        catch (Exception ex)
        {
            this.log($"[Character] could not write {path}: {ex.Message}");
        }

        return true;
    }

    public void ClearCache()
    {
        this.Cache?.Clear();
        this.SaveCache();
    }

    public void Dispose() => this.SaveCache();

    private void Switch(ulong id, string name, CardCatalog cards)
    {
        this.SaveCache();
        this.contentId = id;
        this.Name = name;
        this.Cache = null;
        this.Drafts = null;
        if (id == 0)
            return;

        this.folder = Path.Combine(this.root, id.ToString("X16"));
        var fingerprint = DeckCache.Fingerprint(cards);
        try
        {
            Directory.CreateDirectory(this.folder);
            File.WriteAllText(Path.Combine(this.folder, "character.txt"), name);
            var cachePath = Path.Combine(this.folder, "deck_cache.json");
            this.Cache = File.Exists(cachePath) ? DeckCache.FromJson(File.ReadAllText(cachePath), fingerprint) : new DeckCache(fingerprint);
            var draftPath = Path.Combine(this.folder, "draft_results.csv");
            this.Drafts = File.Exists(draftPath) ? DraftRecord.Parse(File.ReadLines(draftPath)) : new DraftRecord();
        }
        catch (Exception ex)
        {
            // A broken file starts over rather than keeping the plugin from working.
            this.log($"[Character] could not read {this.folder}: {ex.Message}; starting empty");
            this.Cache ??= new DeckCache(fingerprint);
            this.Drafts ??= new DraftRecord();
        }

        this.savedChanges = this.Cache.Changes;
        this.log($"[Character] {name}: deck cache {this.Cache.Searches} search(es), {this.Cache.Scores} deck score(s); {this.Drafts.Results.Count} tournament(s) recorded");
    }

    private void SaveCache()
    {
        if (this.Cache is not { } cache || this.folder.Length == 0)
            return;
        var changes = cache.Changes;
        if (changes == this.savedChanges)
            return;
        var path = Path.Combine(this.folder, "deck_cache.json");
        try
        {
            File.WriteAllText(path + ".tmp", cache.ToJson());
            File.Move(path + ".tmp", path, true);
            this.savedChanges = changes;
        }
        catch (Exception ex)
        {
            this.log($"[Character] could not write {path}: {ex.Message}");
        }
    }
}
