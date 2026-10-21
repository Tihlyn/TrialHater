using TrialHater.Core.Cards;
using TrialHater.Core.Deck;
using TrialHater.Core.Rules;
using TrialHater.Core.Tournament;
using Xunit;
using static TrialHater.Tests.Fixtures;

namespace TrialHater.Tests;

public class CacheTests
{
    // Memeroon (6 hands) under Chaos: simulated games only, so the searches stay quick.
    private const uint Memeroon = 2293762;
    private static readonly RuleSet Chaos = RuleSet.Of(TriadRule.Chaos);

    // The player's Deck 1 on 2026-10-20.
    private static readonly ushort[] DeckOne = [461, 384, 463, 444, 252];

    private static readonly DeckSearchOptions Options = new() { TopFive = 2, TopFour = 3, TopLow = 7, Finalists = 4, Results = 3 };

    private static NpcDeck Npc => Npcs.Get(Memeroon)!;

    // The weakest 1-star card is left out, to be obtained later.
    private static readonly CardInfo Weakest = Catalog.All.Where(c => c.Stars == 1).MinBy(c => c.Stats.Up + c.Stats.Right + c.Stats.Down + c.Stats.Left)!;

    private static List<CardInfo> Owned => Catalog.All.Where(c => (c.Stars <= 3 || c.Id % 7 == 0) && c != Weakest).OrderBy(c => c.Id).ToList();

    private static List<IReadOnlyList<CardInfo>> Saved => [DeckOne.Select(id => Catalog.Get(id)!).ToList()];

    private static DeckSearchResult Find(IReadOnlyCollection<CardInfo> owned, DeckCache? cache)
        => DeckOptimizer.Find(Npc, Chaos, owned, Saved, Catalog, Options, cache: cache);

    private static void AssertSameResult(DeckSearchResult expected, DeckSearchResult actual)
    {
        Assert.Equal(expected.Best.Select(s => DeckCache.DeckKey(s.Cards)), actual.Best.Select(s => DeckCache.DeckKey(s.Cards)));
        Assert.Equal(expected.Best.Select(s => s.Expected), actual.Best.Select(s => s.Expected));
        Assert.Equal(expected.Saved.Select(s => s.Expected), actual.Saved.Select(s => s.Expected));
    }

    [Fact]
    public void ASecondSearchIsReusedAndGivesTheSameResult()
    {
        var cache = new DeckCache(DeckCache.Fingerprint(Catalog));
        var first = Find(Owned, cache);
        Assert.Null(first.CachedAt);
        Assert.Equal(1, cache.Searches);

        var second = Find(Owned, cache);
        Assert.NotNull(second.CachedAt);
        AssertSameResult(first, second);

        // And what the cache holds is what a search without it finds.
        AssertSameResult(Find(Owned, null), second);
    }

    [Fact]
    public void ANewCardSearchesAgainOnlyIfTheSearchWouldTryIt()
    {
        var cache = new DeckCache(DeckCache.Fingerprint(Catalog));
        var first = Find(Owned, cache);

        // A card that does not make its rarity tier changes nothing: the search is reused.
        Assert.NotNull(Find([.. Owned, Weakest], cache).CachedAt);

        // Without the best deck's strongest card, then getting it back: the first search differs,
        // the second is the original one again.
        var star = first.Best[0].Cards.MaxBy(c => c.Stars)!;
        var without = Owned.Where(c => c.Id != star.Id).ToList();
        var lacking = Find(without, cache);
        Assert.Null(lacking.CachedAt);
        Assert.DoesNotContain(lacking.Best[0].Cards, c => c.Id == star.Id);
        var back = Find([.. without, star], cache);
        Assert.NotNull(back.CachedAt);
        AssertSameResult(first, back);
    }

    [Fact]
    public void ASavedDeckIsScoredOncePerNpcAndRules()
    {
        var cache = new DeckCache(DeckCache.Fingerprint(Catalog));
        var evaluation = new DeckEvaluationOptions();
        var ranked = DeckOptimizer.RankSaved(Npc, Chaos, Saved, Catalog, evaluation, cache: cache);
        Assert.Equal(1, cache.Scores);
        var again = DeckOptimizer.RankSaved(Npc, Chaos, Saved, Catalog, evaluation, cache: cache);
        Assert.Equal(1, cache.Scores);
        Assert.Equal(ranked[0].Expected, again[0].Expected);

        // Other rules are another score.
        DeckOptimizer.RankSaved(Npc, RuleSet.Of(TriadRule.Chaos, TriadRule.Reverse), Saved, Catalog, evaluation, cache: cache);
        Assert.Equal(2, cache.Scores);
    }

    [Fact]
    public void TheCacheSurvivesItsFileButNotAnotherCardList()
    {
        var fingerprint = DeckCache.Fingerprint(Catalog);
        var cache = new DeckCache(fingerprint);
        var first = Find(Owned, cache);
        var json = cache.ToJson();

        var loaded = DeckCache.FromJson(json, fingerprint);
        Assert.Equal(cache.Searches, loaded.Searches);
        Assert.Equal(cache.Scores, loaded.Scores);
        var reused = Find(Owned, loaded);
        Assert.NotNull(reused.CachedAt);
        AssertSameResult(first, reused);

        var patched = DeckCache.FromJson(json, "another game version");
        Assert.Equal(0, patched.Searches);
        Assert.Equal(0, patched.Scores);
    }

    [Fact]
    public void ADraftResultIsVettedInTheTopHalfAndSurvivesItsFile()
    {
        var rules = RuleSet.Of(TriadRule.Draft, TriadRule.Plus);
        var at = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);
        var record = new DraftRecord();
        Assert.True(record.Add(new DraftResult(at, rules, [92, 256, 209, 348, 74], 0.41, true, 2, 5, 4)));
        Assert.True(record.Add(new DraftResult(at.AddHours(2), rules, [92, 187, 433, 13, 61], null, false, 6, 1, -3)));
        Assert.False(record.Add(new DraftResult(at, rules, [92, 256, 209, 348, 74], 0.41, true, 2, 5, 4)));

        Assert.True(record.Results[0].Vetted);
        Assert.False(record.Results[1].Vetted);
        Assert.Equal((1, 1), record.Card(rules, 92));
        Assert.Equal((0, 0), record.Card(RuleSet.Of(TriadRule.Draft), 92));
        Assert.Equal(1 + 1 + (1 - 1), record.Track(rules, [256, 209, 92]));

        var parsed = DraftRecord.Parse(new[] { DraftRecord.Header }.Concat(record.Results.Select(DraftRecord.Line)));
        Assert.Equal(record.Results, parsed.Results, (a, b) => a.Tournament == b.Tournament && a.Rules == b.Rules
            && a.Deck.SequenceEqual(b.Deck) && a.Expected == b.Expected && a.Advised == b.Advised && a.Rank == b.Rank
            && a.Points == b.Points && a.Captures == b.Captures);
    }

    [Fact]
    public void TheDraftRecordOnlyBreaksTies()
    {
        ushort[] offer = [92, 256, 48, 187, 176, 144, 209, 348, 433, 13, 385, 17, 74, 61, 419];
        var cards = offer.Select(id => Catalog.Get(id)!).ToList();
        var rules = RuleSet.Of(TriadRule.Draft);
        var observed = DraftDecks.Parse(File.ReadLines(DataPath("draft-decks.csv")), Catalog);
        var plain = DraftAdvisor.Advise(cards, rules, observed, Catalog);
        var best = plain.Ranked[0];

        // A deck clearly behind the best stays behind, however well its cards did: the record only
        // chooses among the decks within the margin.
        var far = plain.Ranked.Last();
        Assert.True(far.Expected < best.Expected - DraftAdvisor.TieMargin);
        var leaning = DraftAdvisor.Advise(cards, rules, observed, Catalog, record: Vetting(rules, far.Cards));
        Assert.NotEqual(far.Cards.Select(c => c.Id).Order(), leaning.Picks.Select(c => c.Id).Order());
        Assert.True(leaning.Ranked[0].Expected >= best.Expected - DraftAdvisor.TieMargin);

        // A deck within the margin is taken when its own cards did better.
        if (plain.Ranked.Skip(1).FirstOrDefault(s => s.Expected >= best.Expected - DraftAdvisor.TieMargin) is { } near)
        {
            var advised = DraftAdvisor.Advise(cards, rules, observed, Catalog, record: Vetting(rules, near.Cards.Where(c => !best.Cards.Contains(c))));
            Assert.Equal(near.Cards.Select(c => c.Id).Order(), advised.Picks.Select(c => c.Id).Order());
            Assert.Equal(near.Expected, advised.Ranked[0].Expected);
            Assert.Contains("past tournaments", advised.Status);
        }
    }

    // Three top-half tournaments with these cards.
    private static DraftRecord Vetting(RuleSet rules, IEnumerable<CardInfo> cards)
    {
        var record = new DraftRecord();
        var ids = cards.Select(c => c.Id).ToList();
        var deck = ids.Concat(Enumerable.Repeat((ushort)1, 5)).Take(5).ToList();
        for (var t = 0; t < 3; t++)
            record.Add(new DraftResult(new DateTimeOffset(2026, 10, 1 + t, 0, 0, 0, TimeSpan.Zero), rules, deck, null, true, 1, 6, 5));
        return record;
    }
}
