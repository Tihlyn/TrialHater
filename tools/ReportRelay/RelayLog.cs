namespace TrialHater.ReportRelay;

// relay.log in the data folder: every report filed or refused, one line each. Rolls over to
// relay.old.log at 10 MiB.
internal sealed class RelayLog
{
    private const long RollAt = 10 * 1024 * 1024;

    private readonly string path;
    private readonly object gate = new();

    public RelayLog(string path)
    {
        this.path = path;
    }

    public void Write(string text)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {text}";
        Console.WriteLine(line);
        lock (this.gate)
        {
            try
            {
                if (File.Exists(this.path) && new FileInfo(this.path).Length > RollAt)
                    File.Move(this.path, Path.ChangeExtension(this.path, ".old.log"), overwrite: true);
                File.AppendAllText(this.path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // The console line stands; a full disk must not take the relay down.
            }
        }
    }
}
