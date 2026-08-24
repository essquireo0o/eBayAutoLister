using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The sold search steps outward one word at a time instead of giving up.
/// </summary>
/// <remarks>
/// The owner's example (2026-08-21): a live show put up "1884 CC Morgan Silver Dollar GSA Holder
/// Uncirculated Carson City" and the card read CAN'T PRICE IT. The whole name has never sold on
/// eBay word for word, and the single widening jumped straight to three words — stepping clean over
/// "1884 CC Morgan Silver Dollar", which is the exact title that coin sells under every week.
/// "The sold comps need to look harder … it should find the closest one."
/// </remarks>
public class CompsLadderTests
{
    private const string Coin = "1884 CC Morgan Silver Dollar GSA Holder Uncirculated Carson City";

    [Fact]
    public void The_ladder_offers_the_title_that_coin_actually_sells_under()
    {
        var rungs = LiveSearchQuery.Ladder(LiveSearchQuery.Build(Coin)).Select(r => r.Query).ToList();

        // The rung that matters is on the ladder, and it is reached before the vague ones.
        Assert.Contains("1884 CC Morgan Silver Dollar", rungs);
        Assert.True(rungs.IndexOf("1884 CC Morgan Silver Dollar") < rungs.IndexOf("1884 CC Morgan"),
            $"the closest rung must come first — got {string.Join(" | ", rungs)}");
    }

    [Fact]
    public void Live_fallback_queries_reach_the_similar_coin_core_without_one_call_per_word()
    {
        var queries = LiveSearchQuery.Build(
            "1955 Washington Quarter PCGS Genuine UNC Detail Wheel Mark").SimilarQueries;

        Assert.Contains("1955 Washington Quarter", queries);
        Assert.InRange(queries.Count, 1, 4);
        Assert.Equal(queries.Count, queries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void It_gives_up_one_word_at_a_time_broadest_last()
    {
        var rungs = LiveSearchQuery.Ladder(LiveSearchQuery.Build(Coin)).ToList();

        Assert.True(rungs.Count >= 3, $"expected a real ladder, got {rungs.Count} rung(s)");
        for (var i = 1; i < rungs.Count; i++)
            Assert.True(rungs[i].Query.Length < rungs[i - 1].Query.Length,
                $"rung {i} ({rungs[i].Query}) is not narrower than rung {i - 1} ({rungs[i - 1].Query})");

        // Never past the point where a search stops being a search and becomes a category.
        var last = rungs[^1].Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(last >= LiveSearchQuery.MinImportantWords, $"bottom rung is only {last} word(s)");
    }

    [Fact]
    public void Every_rung_is_the_sellers_own_words_in_their_own_order()
    {
        var typed = LiveSearchQuery.Build(Coin).Query;
        foreach (var rung in LiveSearchQuery.Ladder(LiveSearchQuery.Build(Coin)))
        {
            // A cut of the front of the query — nothing added, nothing reordered, so a price is
            // never quoted against words the seller did not say.
            Assert.StartsWith(rung.Query, typed, StringComparison.Ordinal);
            Assert.True(rung.Widened, "a rung that does not admit it widened cannot warn about it");
            Assert.NotEmpty(LiveSearchQuery.WidenedWarning(rung));
        }
    }

    [Fact]
    public void A_name_already_at_the_core_has_nothing_to_climb()
    {
        // Two identifying words are the floor, so there is no rung below them to offer.
        Assert.Empty(LiveSearchQuery.Ladder(LiveSearchQuery.Build("Antminer S9")));
    }

    [Fact]
    public void The_old_single_step_still_behaves_exactly_as_it_did()
    {
        // Widen is what the rest of the app and its tests already stand on; the ladder was added
        // beside it, not through it.
        var terms = LiveSearchQuery.Build(Coin);
        var wide = LiveSearchQuery.Widen(terms);

        Assert.NotNull(wide);
        Assert.Equal(LiveSearchQuery.WidenToWords,
            wide!.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
