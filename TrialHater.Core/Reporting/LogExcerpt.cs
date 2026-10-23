using System.Text.RegularExpressions;

namespace TrialHater.Core.Reporting;

// The plugin's share of dalamud.log, which every plugin writes to: the entries that mention
// `tag`, with the lines that continue them (an exception's stack trace carries no timestamp).
public static class LogExcerpt
{
    // "2026-10-22 22:43:51.524 +02:00 [INF] ..." starts an entry.
    private static readonly Regex EntryStart = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", RegexOptions.CultureInvariant);

    // The last `max` lines of the entries that mention `tag`, oldest first.
    public static List<string> Select(IEnumerable<string> lines, string tag, int max)
    {
        var kept = new Queue<string>();
        var keeping = false;
        foreach (var line in lines)
        {
            if (EntryStart.IsMatch(line))
                keeping = line.Contains(tag, StringComparison.Ordinal);
            if (!keeping)
                continue;
            kept.Enqueue(line);
            if (kept.Count > max)
                kept.Dequeue();
        }

        return kept.ToList();
    }

    // The last `maxLines` lines of a file another process may be writing, reading at most its last
    // `maxBytes`. A line cut by the read start is dropped. Empty when the file is missing.
    public static List<string> Tail(string path, int maxLines, long maxBytes)
    {
        if (!File.Exists(path))
            return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        if (start > 0)
            reader.ReadLine();
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > maxLines)
                lines.Dequeue();
        }

        return lines.ToList();
    }
}
