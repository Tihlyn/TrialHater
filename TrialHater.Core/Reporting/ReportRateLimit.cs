namespace TrialHater.Core.Reporting;

// The relay's brakes. Per reporter: one report per MinInterval and MaxPerDay in any 24 hours.
// Per address: after MaxFailures refused requests within FailureWindow, everything from it is
// refused until the oldest failure leaves the window. Thread-safe.
public sealed class ReportRateLimit
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(1);
    public const int MaxPerDay = 20;
    public const int MaxFailures = 10;

    // Past this many tracked addresses or reporters, expired entries are dropped.
    private const int PruneAbove = 1000;

    private readonly Dictionary<string, Queue<DateTimeOffset>> sent = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> failures = new();
    private readonly object gate = new();

    public bool Blocked(string address, DateTimeOffset now)
    {
        lock (this.gate)
            return this.failures.TryGetValue(address, out var times) && Expire(times, now - FailureWindow) >= MaxFailures;
    }

    public void Failed(string address, DateTimeOffset now)
    {
        lock (this.gate)
            Record(this.failures, address, now, FailureWindow);
    }

    // Takes a slot for `reporter`, or says why it has to wait.
    public string? TryTake(string reporter, DateTimeOffset now)
    {
        lock (this.gate)
        {
            if (this.sent.TryGetValue(reporter, out var times))
            {
                var count = Expire(times, now - TimeSpan.FromDays(1));
                if (count > 0 && now - times.Last() < MinInterval)
                    return $"one report per {MinInterval.TotalSeconds:0} seconds";
                if (count >= MaxPerDay)
                    return $"{MaxPerDay} reports in 24 hours already";
            }

            Record(this.sent, reporter, now, TimeSpan.FromDays(1));
            return null;
        }
    }

    private static void Record(Dictionary<string, Queue<DateTimeOffset>> map, string key, DateTimeOffset now, TimeSpan window)
    {
        if (map.Count > PruneAbove)
        {
            foreach (var stale in map.Where(e => Expire(e.Value, now - window) == 0).Select(e => e.Key).ToList())
                map.Remove(stale);
        }

        if (!map.TryGetValue(key, out var times))
            map[key] = times = new Queue<DateTimeOffset>();
        times.Enqueue(now);
    }

    // Drops the times before `since`; returns how many remain.
    private static int Expire(Queue<DateTimeOffset> times, DateTimeOffset since)
    {
        while (times.Count > 0 && times.Peek() < since)
            times.Dequeue();
        return times.Count;
    }
}
