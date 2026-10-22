using TrialHater.Core.Search;

namespace TrialHater.Game;

// The solve cache (solved early positions, reused when they come back) and its file,
// solve_cache.json in the plugin's config folder. Positions do not depend on the character, so
// there is one for all. Read and written on workers: the file reaches a few MB. Tick runs on the
// framework thread.
public sealed class SolveCacheFile : IDisposable
{
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(60);

    private readonly string path;
    private readonly Action<string> log;
    private readonly object gate = new();
    private SolveCache cache = new();
    private Task saving = Task.CompletedTask;
    private volatile int savedChanges;
    private DateTime lastSave;
    private volatile bool loaded;

    public SolveCacheFile(string configFolder, Action<string> log)
    {
        this.path = Path.Combine(configFolder, "solve_cache.json");
        this.log = log;
        Task.Run(this.Load);
    }

    // Empty until the file is read (a second or so after load).
    public SolveCache Cache
    {
        get
        {
            lock (this.gate)
                return this.cache;
        }
    }

    public void Tick(DateTime now)
    {
        var current = this.Cache;
        if (!this.loaded || current.Changes == this.savedChanges || now - this.lastSave < SaveEvery || !this.saving.IsCompleted)
            return;
        this.lastSave = now;
        this.saving = Task.Run(() => this.Save(current));
    }

    public void Dispose()
    {
        this.saving.Wait();
        if (this.loaded)
            this.Save(this.Cache);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(this.path))
            {
                var read = SolveCache.FromJson(File.ReadAllText(this.path));
                lock (this.gate)
                    this.cache = read;
                this.savedChanges = read.Changes;
                this.log($"[SolveCache] {read.Count} solved position(s) loaded");
            }
        }
        catch (Exception ex)
        {
            // A broken file starts over rather than keeping the plugin from working.
            this.log($"[SolveCache] could not read {this.path}: {ex.Message}; starting empty");
        }

        this.loaded = true;
    }

    private void Save(SolveCache current)
    {
        var changes = current.Changes;
        if (changes == this.savedChanges)
            return;
        try
        {
            File.WriteAllText(this.path + ".tmp", current.ToJson());
            File.Move(this.path + ".tmp", this.path, true);
            this.savedChanges = changes;
        }
        catch (Exception ex)
        {
            this.log($"[SolveCache] could not write {this.path}: {ex.Message}");
        }
    }
}
