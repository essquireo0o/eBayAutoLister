namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot pricer's fallback when stored history comes up empty: a bounded exact-to-similar
/// OpenWebNinja lookup ladder, then the same ask again. Before this, a niche lot on a live show read
/// CAN'T PRICE IT while the machinery to answer it sat one call away — the seller's question
/// ("why isn't this using the live scraper?") was better than the screen's answer.
/// </summary>
public class WhatsNotLiveCompsFallbackAssetTests
{
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void A_no_data_card_asks_eBay_live_and_then_asks_again()
    {
        // The branch lives on the card's own verdict, and it spends the SAME lookup machinery
        // the listing screen and scanner board use — one budget, one cache, one kill switch.
        Assert.Contains("body.call === 'no_data'", Js);
        Assert.Contains("await runLiveLookup(queries[i], 'wn')", Js);
        Assert.Contains("try { await wnPriceItem(); } finally { wnLiveFallbackSpent = false; }", Js);
    }

    [Fact]
    public void The_retry_happens_once_per_press_not_in_a_loop()
    {
        // The ladder itself is capped. The guard is a module flag, NOT a parameter: wnPriceItem is
        // wired straight into click handlers, and a parameter would arrive holding the click Event
        // — truthy — so the fallback would never fire from the button at all.
        Assert.Contains("async function wnPriceItem()", Js);
        Assert.Contains("if (!wnLiveFallbackSpent && body.call === 'no_data')", Js);
        Assert.Contains(".slice(0, 5)", Js);
    }

    [Fact]
    public void A_lookup_that_could_not_help_says_why_in_its_own_words()
    {
        // "Couldn't ask" (switched off, budget spent) and "asked — there are no sales" are
        // opposite conclusions off the same zero rows. The lookup's own message is the one that
        // knows which happened, so it is the one on the card.
        Assert.Contains("const why = lastRun?.message || 'The exact and similar live searches found nothing new.';", Js);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
