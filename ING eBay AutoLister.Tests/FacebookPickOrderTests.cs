using System.Diagnostics;
using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The order of the "Today's picks" grid: <b>best return first.</b>
///
/// The seller's complaint was that the best deal was wherever Facebook happened to put it, so
/// finding it meant reading all 25 cards. The rule that fixes that lives in <c>app.js</c>, which
/// nothing in C# executes — so these tests take the comparator's real source out of the asset and
/// run it under node. Asserting on the text would pass just as happily with the comparison
/// inverted; this fails when the ranking is wrong, which is the only thing worth pinning.
/// </summary>
public class FacebookPickOrderTests
{
    // A pick the grid can rank: a sale price and at least one sold comp behind it. Anything
    // missing those two is the "no sold data" card, which is a case in its own right below.
    private static string Pick(string id, double roi, double profit, int comps = 8, double sale = 100) =>
        FormattableString.Invariant($"{{\"id\":\"{id}\",\"row\":{{\"itemId\":\"{id}\",\"roiPercent\":{roi},\"netProfit\":{profit},\"ebayExpectedSale\":{sale},\"soldCompCount\":{comps}}}}}");

    [Fact]
    public void TheBiggestReturnComesFirst()
    {
        Assert.Equal(
            ["huge", "good", "thin"],
            Order(Pick("thin", 8, 4), Pick("huge", 240, 120), Pick("good", 65, 30)));
    }

    /// <summary>
    /// A deal that loses money has to sit under every deal that makes money, however small the
    /// winner is. This is the one ordering mistake a seller would act on.
    /// </summary>
    [Fact]
    public void ALosingDealSortsBelowEveryWinningOne()
    {
        Assert.Equal(
            ["barely", "flat", "loss", "disaster"],
            Order(Pick("loss", -15, -9), Pick("barely", 3, 2), Pick("disaster", -80, -140), Pick("flat", 0, 0)));
    }

    /// <summary>
    /// No matching sold history is no opinion, not a bad deal. Those cards keep their place on the
    /// grid — they are still real listings nearby — but they go to the end, because nothing can be
    /// said about their return.
    /// </summary>
    [Fact]
    public void CardsWithNoSoldDataSinkToTheBottomWithoutDisappearing()
    {
        var noComps = """{"id":"no-comps","row":{"itemId":"no-comps","ebayExpectedSale":90,"soldCompCount":0}}""";
        var noSale = """{"id":"no-sale","row":{"itemId":"no-sale","soldCompCount":12}}""";
        var never = """{"id":"never-priced","row":null}""";

        var order = Order(noComps, Pick("loses", -40, -25), never, Pick("wins", 90, 45), noSale);

        // Every ranked card first — including the one that loses money — then the unrankable ones.
        Assert.Equal(["wins", "loses", "no-comps", "never-priced", "no-sale"], order);
        Assert.Equal(5, order.Length); // nothing was dropped on the way
    }

    // Same ROI is a real tie on a grid full of round numbers; the bigger dollar profit breaks it.
    [Fact]
    public void ProfitBreaksATieOnRoi()
    {
        Assert.Equal(
            ["fifty-on-200", "fifty-on-20"],
            Order(Pick("fifty-on-20", 50, 10), Pick("fifty-on-200", 50, 100)));
    }

    // A priced card that never got an ROI figure still has a profit, so it ranks on that — under
    // everything that does have an ROI rather than being thrown out.
    [Fact]
    public void APricedCardWithNoRoiFallsBackToItsProfit()
    {
        var noRoiBig = """{"id":"no-roi-big","row":{"itemId":"no-roi-big","netProfit":80,"ebayExpectedSale":200,"soldCompCount":5}}""";
        var noRoiSmall = """{"id":"no-roi-small","row":{"itemId":"no-roi-small","netProfit":5,"ebayExpectedSale":40,"soldCompCount":5}}""";

        Assert.Equal(
            ["has-roi", "no-roi-big", "no-roi-small"],
            Order(noRoiSmall, noRoiBig, Pick("has-roi", 12, 6)));
    }

    /// <summary>
    /// Equally-good picks must not shuffle every time the panel refreshes, so Facebook's own order
    /// is the last tiebreak. The comparator returns 0 for these; the grid adds the index.
    /// </summary>
    [Fact]
    public void EquallyGoodPicksKeepFacebooksOrder()
    {
        Assert.Equal(
            ["first", "second", "third"],
            Order(Pick("first", 40, 20), Pick("second", 40, 20), Pick("third", 40, 20)));
    }

    // ── Running the shipped comparator ─────────────────────────────────────────

    /// <summary>
    /// Sorts the given picks with the comparator exactly as <c>app.js</c> defines it, and returns
    /// the ids in the order the grid would put the cards in.
    /// </summary>
    private static string[] Order(params string[] picks)
    {
        var driver =
            ComparatorSource() + "\n" +
            // The one line of app.js this has to restate: reorderFacebookPicks falls back to the
            // card's original position so a 0 from the comparator means "leave them as they were".
            "const input = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));\n" +
            "const ordered = input.map((e, index) => ({ e, index }))\n" +
            "  .sort((x, y) => compareFacebookPicks(x.e.row, y.e.row) || x.index - y.index);\n" +
            "console.log(JSON.stringify(ordered.map(o => o.e.id)));\n";

        var stamp = Guid.NewGuid().ToString("N");
        var scriptFile = Path.Combine(Path.GetTempPath(), $"fb_pick_order_{stamp}.cjs");
        var dataFile = Path.Combine(Path.GetTempPath(), $"fb_pick_order_{stamp}.json");
        File.WriteAllText(scriptFile, driver);
        File.WriteAllText(dataFile, "[" + string.Join(",", picks) + "]");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo(NodeRuntime.NodeExe)
            {
                ArgumentList = { scriptFile, dataFile },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(proc);

            var stdout = proc!.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(30000), "the comparator did not finish");
            Assert.True(proc.ExitCode == 0, $"the pick comparator threw:\n{stderr}");

            return JsonSerializer.Deserialize<string[]>(stdout.Trim())!;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
            try { File.Delete(dataFile); } catch { }
        }
    }

    /// <summary>The comparator's own text, lifted out of the shipped asset.</summary>
    private static string ComparatorSource()
    {
        var js = ReadAsset("app.js");

        var start = js.IndexOf("function compareFacebookPicks(", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js no longer has a compareFacebookPicks function — the picks grid's ordering rule moved");

        // Functions in this file sit two spaces in, so the first brace back at that column closes it.
        var end = js.IndexOf("\n  }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of compareFacebookPicks");
        return js[start..(end + 4)];
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
