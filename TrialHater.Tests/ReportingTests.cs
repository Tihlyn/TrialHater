using TrialHater.Core.Reporting;
using Xunit;

namespace TrialHater.Tests;

public class ReportingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Log_excerpt_keeps_our_entries_with_their_stack_traces()
    {
        string[] log =
        [
            "2026-10-22 22:43:51.524 +02:00 [INF] [TrialHater] [Npc] Tsuzura (TripleTriad 2293828)",
            "2026-10-22 22:43:52.000 +02:00 [INF] [OtherPlugin] hello",
            "   at OtherPlugin.Thing()",
            "2026-10-22 22:43:53.000 +02:00 [ERR] [TrialHater] [Analysis] policy failed",
            "System.InvalidOperationException: boom",
            "   at TrialHater.Core.Policy.MovePolicy.Decide()",
            "2026-10-22 22:43:54.000 +02:00 [INF] [Dalamud] unrelated",
        ];

        var kept = LogExcerpt.Select(log, "TrialHater", 100);

        Assert.Equal([log[0], log[3], log[4], log[5]], kept);
        Assert.Equal([log[4], log[5]], LogExcerpt.Select(log, "TrialHater", 2));
    }

    [Fact]
    public void Tail_drops_the_line_cut_by_the_read_start()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["first line", "second line", "third"]);
            Assert.Equal(["second line", "third"], LogExcerpt.Tail(path, 10, 25));
            Assert.Equal(["third"], LogExcerpt.Tail(path, 1, 1000));
            Assert.Empty(LogExcerpt.Tail(path + ".missing", 10, 1000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_listed_character_is_pinned_to_its_first_key_and_world()
    {
        var list = TesterAllowlist.Parse("""{ "testers": [ { "name": "Iliya Loha" } ] }""");

        Assert.Equal(Admission.Unknown, list.Admit("Someone Else", "Phoenix", "aa", T0).Result);
        Assert.Equal(Admission.Pinned, list.Admit("iliya loha", "Phoenix", "aa", T0).Result);
        Assert.Equal(Admission.Admitted, list.Admit("Iliya Loha", "Phoenix", "aa", T0).Result);
        Assert.Equal(Admission.OtherKey, list.Admit("Iliya Loha", "Phoenix", "bb", T0).Result);
        Assert.Equal(Admission.OtherWorld, list.Admit("Iliya Loha", "Ragnarok", "aa", T0).Result);

        // The pin survives the file.
        var reloaded = TesterAllowlist.Parse(list.ToJson());
        Assert.Equal(Admission.OtherKey, reloaded.Admit("Iliya Loha", "Phoenix", "bb", T0).Result);
        Assert.Equal(Admission.Admitted, reloaded.Admit("Iliya Loha", "Phoenix", "aa", T0).Result);
    }

    [Fact]
    public void A_world_set_by_hand_is_required_before_pinning()
    {
        var list = TesterAllowlist.Parse("""{ "testers": [ { "name": "Ches Macchiato", "world": "Phoenix" } ] }""");

        Assert.Equal(Admission.OtherWorld, list.Admit("Ches Macchiato", "Ragnarok", "aa", T0).Result);
        Assert.Equal(Admission.Pinned, list.Admit("Ches Macchiato", "Phoenix", "aa", T0).Result);
    }

    [Fact]
    public void Reporters_wait_between_reports_and_have_a_daily_cap()
    {
        var limits = new ReportRateLimit();

        Assert.Null(limits.TryTake("a", T0));
        Assert.NotNull(limits.TryTake("a", T0.AddSeconds(10)));
        Assert.Null(limits.TryTake("b", T0.AddSeconds(10)));
        for (var i = 1; i < ReportRateLimit.MaxPerDay; i++)
            Assert.Null(limits.TryTake("a", T0.AddMinutes(i)));
        Assert.NotNull(limits.TryTake("a", T0.AddHours(23)));
        Assert.Null(limits.TryTake("a", T0.AddDays(1).AddMinutes(1)));
    }

    [Fact]
    public void An_address_with_too_many_refusals_is_blocked_for_an_hour()
    {
        var limits = new ReportRateLimit();
        for (var i = 0; i < ReportRateLimit.MaxFailures; i++)
        {
            Assert.False(limits.Blocked("1.2.3.4", T0.AddSeconds(i)));
            limits.Failed("1.2.3.4", T0.AddSeconds(i));
        }

        Assert.True(limits.Blocked("1.2.3.4", T0.AddMinutes(30)));
        Assert.False(limits.Blocked("5.6.7.8", T0.AddMinutes(30)));
        Assert.False(limits.Blocked("1.2.3.4", T0.AddHours(1).AddSeconds(1)));
    }

    [Fact]
    public void Reports_with_unsafe_file_names_are_refused()
    {
        static IssueReport With(params string[] names)
            => new(ReportCategory.Stalled, "headline", string.Empty, "v0.2.9", T0, names.Select(n => new ReportFile(n, "x")).ToList());

        Assert.Null(With("state.txt", "log.txt").Problem());
        Assert.NotNull(With("../escape.txt").Problem());
        Assert.NotNull(With("a/b.txt").Problem());
        Assert.NotNull(With(".hidden").Problem());
        Assert.NotNull(With("state.txt", "STATE.txt").Problem());
        Assert.NotNull((With("state.txt") with { Category = (ReportCategory)42 }).Problem());
    }
}
