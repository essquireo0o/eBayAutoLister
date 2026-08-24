namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// When nothing sold under an item's name, the pricing panel gives an estimate instead of a blank.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24, owner: "the sold comps suck — they should get an estimate." On the AI Listing screen
/// the Sold Price Research panel returned early whenever the lookup found no rows, leaving two
/// search links and no number, on the one screen where a price is actually committed. "No sold
/// data" is a true statement about this app's database and a useless one about the market: the
/// seller still has to choose a figure, and with nothing on screen they choose it out of the air.
/// </para>
/// <para>
/// The model tier that fills this gap already existed and was already trusted enough to run the
/// Opportunity Finder's third pricing pass. It had simply never been wired to this panel.
/// </para>
/// <para>
/// What these tests protect is the honesty of it, not its presence. An estimate must never be
/// mistakable for sold history: the four comp statistics stay empty because average/median/min/max
/// are claims about real sales and there are none, the range is shown rather than a single
/// confident number, and the words say what it rests on. A seller who cannot tell the two apart at
/// a glance will quote the wrong one to a buyer.
/// </para>
/// </remarks>
public class SoldCompsEstimateFallbackTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void An_empty_lookup_asks_for_an_estimate_instead_of_returning_a_blank()
    {
        var branch = Slice(Js, "if (!res.ok || !data.count) {", "return;");
        Assert.Contains("nlEstimateWithoutComps(itemName", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_estimate_never_fills_the_sold_comp_statistics()
    {
        // avg / median / min / max / count describe real sales. Filling them from a model's range
        // would invent four facts to avoid one blank, and they are what a seller quotes.
        var body = Slice(Js, "async function nlEstimateWithoutComps(", "async function nlSoldCompsConnect(");

        foreach (var stat in new[] { "nl-sold-comps-stat-avg", "nl-sold-comps-stat-median",
                                     "nl-sold-comps-stat-min", "nl-sold-comps-stat-max",
                                     "nl-sold-comps-stat-count" })
            Assert.DoesNotContain(stat, body, StringComparison.Ordinal);

        // And it never reveals the stats row that holds them.
        Assert.DoesNotContain("stats.classList.remove('hidden')", body, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_on_its_face_that_it_is_not_sold_data()
    {
        var body = Slice(Js, "async function nlEstimateWithoutComps(", "async function nlSoldCompsConnect(");

        Assert.Contains("not sold data", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no sold comps behind this", body, StringComparison.OrdinalIgnoreCase);
        // A range, not a single number that reads as measured.
        Assert.Contains("nl-comp-estimate-range", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpriceable_item_goes_back_to_the_honest_blank()
    {
        // The model may omit anything it genuinely cannot price, and a zero or negative range is
        // not a price. Showing "$0 – $0" would be worse than showing nothing.
        var body = Slice(Js, "async function nlEstimateWithoutComps(", "async function nlSoldCompsConnect(");
        Assert.Contains("est.mid > 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_estimate_cannot_turn_a_quiet_blank_into_an_error()
    {
        // This is a bonus on a panel that was already showing nothing. If the call fails, the
        // seller should see what they saw before, not a red line about a request they never made.
        var body = Slice(Js, "async function nlEstimateWithoutComps(", "async function nlSoldCompsConnect(");
        Assert.Contains("catch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_estimate_block_does_not_look_like_the_sold_rows_above_it()
    {
        Assert.Contains(".nl-comp-estimate {", Css, StringComparison.Ordinal);
        // Dashed rather than the panel's solid gold: distinguishable at a glance, not on reading.
        var rule = Slice(Css, ".nl-comp-estimate {", "}");
        Assert.Contains("dashed", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_both_changed_assets()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 164);
        AssetStamp.AtLeast(Html, "style.css?v=", 139);
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in the asset.");
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\".");
        return text[a..b];
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository root above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
