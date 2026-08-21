using System.Diagnostics;
using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Every Facebook Marketplace result in the Opportunity Finder is priced against eBay sold comps
/// by the SAME stored-plus-live path the eBay scanner's rows take, and every card says what that
/// evidence was.
/// </summary>
/// <remarks>
/// <para>
/// The defect these pin against: the eBay scanner fetched live sold comps for its search term
/// before scanning and then deepened its top estimates row by row, while the Facebook picks the
/// Opportunity Finder loads on open were priced once against whatever the stored database held —
/// nothing, for a drill or a couch — and the card said "no sold data" with no way to ask again.
/// </para>
/// <para>
/// Nothing in C# executes <c>app.js</c> and nothing can run the 10,000-line <c>Program.cs</c>, so
/// the wiring is pinned by reading the source, and the one rule that decides what a card says is
/// lifted out of the shipped asset and run under node — asserting on its text would pass with the
/// grade inverted. <see cref="LiveCompsPassTests"/> covers the pass itself.
/// </para>
/// </remarks>
public class FacebookSoldCompsPathTests
{
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    // ── The server: one pipeline, both halves ──────────────────────────────────

    [Fact]
    public void The_scan_pipeline_runs_the_live_half_between_stored_comps_and_the_Terapeak_pass()
    {
        var pipeline = Between(Program, "static async Task<LocalArbitrageResult> FindLocalArbitrageAsync(", "LocalSupplyAttribution.Apply(result.Sources");

        var stored = pipeline.IndexOf("Pass 1: sold-comps database", StringComparison.Ordinal);
        var live = pipeline.IndexOf("LiveCompsPass.SelectTargets(", StringComparison.Ordinal);
        var terapeak = pipeline.IndexOf("Pass 2: spend the scrape budget", StringComparison.Ordinal);

        Assert.True(stored >= 0, "the stored-comps pass is gone from FindLocalArbitrageAsync");
        Assert.True(live > stored, "the live sold-comps pass must run AFTER stored comps have been read — it only looks up what they left thin");
        Assert.True(terapeak > live, "the live sold-comps pass must run BEFORE the Terapeak pass, which spends its budget on the preliminary profit");

        // The lookup is the browser's own, and the re-read is the pass-1 pricing — never a second path.
        Assert.Contains("live.FetchAsync", pipeline, StringComparison.Ordinal);
        Assert.Contains("foreach (var key in pass.Refreshed)", pipeline, StringComparison.Ordinal);
        Assert.Contains("PriceAsync(byKeyLive[key], allowScrape: false)", pipeline, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/local/price-these")]   // the Facebook picks the Opportunity Finder loads on open
    [InlineData("/api/local/arbitrage")]     // the local board with Facebook ticked
    [InlineData("/api/facebook/arbitrage")]  // the original Facebook-only route
    public void Every_route_that_prices_Facebook_rows_asks_for_the_live_half(string route)
    {
        var handler = Handler(route);

        Assert.Contains("LiveCompsLookup live", handler, StringComparison.Ordinal);
        Assert.Contains("live: live, liveBudget:", handler, StringComparison.Ordinal);
        // The board's own default (three products, like its auto-deepen pass), capped at a day's allowance.
        Assert.Contains("LiveCompsPass.DefaultBudget", handler, StringComparison.Ordinal);
        Assert.Contains("LiveCompsPass.MaxBudget", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void The_eBay_scanner_is_costed_exactly_as_it_was()
    {
        // Its browser already fetched live comps for the search term before calling the scan, and
        // the board deepens its top rows afterwards. Adding a server-side pass there would spend a
        // second allowance on the same products — so it is the one scan left at the default of zero.
        var handler = Handler("/api/ebay/scan");

        Assert.DoesNotContain("liveBudget", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveCompsLookup", handler, StringComparison.Ordinal);
        Assert.Contains("LiveCompsLookup? live = null, int liveBudget = 0", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_carries_what_the_live_half_did()
    {
        var model = ReadSource(Path.Combine("Models", "LocalArbitrageModels.cs"));
        Assert.Contains("public int LiveLookupsUsed", model, StringComparison.Ordinal);
        Assert.Contains("public int LiveLookupsRefreshed", model, StringComparison.Ordinal);
        Assert.Contains("public string LiveLookupNote", model, StringComparison.Ordinal);
    }

    // ── The browser: the picks, the board, the cards ────────────────────────────

    [Fact]
    public void Opening_the_Opportunity_Finder_loads_the_Facebook_picks_and_prices_them_with_the_live_half()
    {
        Assert.Contains("loadFacebookPicks(false)", FunctionBody("showOpportunitySection"), StringComparison.Ordinal);
        Assert.Contains("priceFacebookPicks(data)", FunctionBody("loadFacebookPicks"), StringComparison.Ordinal);

        var pricing = FunctionBody("priceFacebookPicks");
        Assert.Contains("/api/local/price-these?", pricing, StringComparison.Ordinal);
        Assert.Contains("liveBudget=3", pricing, StringComparison.Ordinal);
        Assert.Contains("liveLookupsRefreshed", pricing, StringComparison.Ordinal);
        Assert.Contains("liveLookupNote", pricing, StringComparison.Ordinal);

        // The board is drawn ONCE, and only when the prices are in (2026-08-21, the owner:
        // "don't load until all items have comps"). It used to appear the moment Facebook
        // answered and then sit for up to four minutes with forty blank cards while the comps
        // ran — which reads as a broken panel rather than a busy one — and then rearrange
        // itself under the cursor when the numbers landed.
        var load = FunctionBody("loadFacebookPicks");
        Assert.Contains("const rows = await priceFacebookPicks(data);", load, StringComparison.Ordinal);
        Assert.Contains("renderFacebookPicks(data.items, rows);", load, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.innerHTML = data.items.map", load, StringComparison.Ordinal);

        // Every card is still filled from the one function that reads the grade — it just runs
        // where the board is drawn now rather than where the prices arrive.
        var render = FunctionBody("renderFacebookPicks");
        Assert.Contains("facebookPickMoneyHtml(row)", render, StringComparison.Ordinal);
        Assert.Contains("compareFacebookPicks(x.row, y.row)", render, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pick_card_shows_the_sold_evidence_the_way_an_eBay_row_does()
    {
        var card = FunctionBody("facebookPickMoneyHtml");

        // The comp count comes from the board's own cell — "19 sold comps priced it of 20" — not a
        // second wording that would drift from the rows'.
        Assert.Contains("compsCell(row)", card, StringComparison.Ordinal);
        // The resale price, the grade word and the database that answered.
        Assert.Contains("sells ~${money(ev.sale)}", card, StringComparison.Ordinal);
        Assert.Contains("fb-pick-ev-${esc(ev.tier)}", card, StringComparison.Ordinal);
        Assert.Contains("ARB_SOURCES[row.resaleSource]", card, StringComparison.Ordinal);
        // And the board's per-row live lookup, on the card.
        Assert.Contains("fb-pick-live-btn", card, StringComparison.Ordinal);

        var evidence = FunctionBody("facebookPickEvidence");
        Assert.Contains("row.evidenceTier", evidence, StringComparison.Ordinal);
        Assert.Contains("row.soldCompCount", evidence, StringComparison.Ordinal);
        Assert.Contains("row.pricedCompCount", evidence, StringComparison.Ordinal);
        Assert.Contains("row.ebayExpectedSale", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pick_card_can_be_repriced_by_the_boards_own_per_row_path()
    {
        var reprice = FunctionBody("repriceFacebookPick");

        // The live lookup first (it files the fresh rows), then the single-row re-cost that reads them.
        var lookup = reprice.IndexOf("runLiveLookup(query", StringComparison.Ordinal);
        var recost = reprice.IndexOf("/api/opportunities/reprice-row", StringComparison.Ordinal);
        Assert.True(lookup >= 0 && recost > lookup, "the card must fetch live sold prices BEFORE asking the server to re-cost the row from them");
        Assert.Contains("dealForReprice(row, query)", reprice, StringComparison.Ordinal);
        Assert.Contains("facebookPickMoneyHtml(fresh)", reprice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_board_fetches_the_terms_live_sold_prices_before_scanning_like_the_eBay_scanner()
    {
        var local = FunctionBody("runLocalArbitrage");
        var ebay = FunctionBody("runEbayScan");

        var lookup = local.IndexOf("runLiveLookup(query, 'ls'", StringComparison.Ordinal);
        var scan = local.IndexOf("/api/local/arbitrage?", StringComparison.Ordinal);
        Assert.True(lookup >= 0, "the local board no longer fetches live sold prices for the search term");
        Assert.True(scan > lookup, "the live lookup must finish before the scan prices anything against it");

        // Same step, same helper, same options as the scanner it is mirroring.
        Assert.Contains("runLiveLookup(query, 'es', { keepOpen: true })", ebay, StringComparison.Ordinal);
        Assert.Contains("{ keepOpen: true }", local, StringComparison.Ordinal);

        // And a bar of its own to show it on — the scanner's markup, under the local panel.
        Assert.Contains("id=\"ls-live\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"ls-live-bar\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"ls-live-stage\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_summary_says_what_the_live_half_did()
    {
        var render = FunctionBody("renderArbitrage");
        Assert.Contains("liveLookupsRefreshed", render, StringComparison.Ordinal);
        Assert.Contains("liveLookupNote", render, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assets_were_restamped()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 152);
        AssetStamp.AtLeast(Html, "style.css?v=", 132);
    }

    // ── The card's evidence rule, run as shipped ────────────────────────────────

    [Fact]
    public void A_Facebook_row_priced_off_thin_comps_is_an_estimate_with_its_comps_counted()
    {
        var ev = Evidence("""{"source":"facebook","evidenceTier":"low","soldCompCount":20,"pricedCompCount":19,"ebayExpectedSale":199.99,"netProfit":-728.12,"evidenceNote":"Estimate — 19 sold comps, but their prices and dates are too scattered to trust the exact figure."}""");

        Assert.True(ev.GetProperty("priceable").GetBoolean());
        Assert.True(ev.GetProperty("guess").GetBoolean());
        Assert.Equal("Estimate", ev.GetProperty("word").GetString());
        Assert.Equal(20, ev.GetProperty("found").GetInt32());
        Assert.Equal(19, ev.GetProperty("priced").GetInt32());
        Assert.Equal(199.99, ev.GetProperty("sale").GetDouble());
        // The note comes back through node's stdout, whose code page mangles the em dash — so the
        // words are asserted, not the punctuation.
        var why = ev.GetProperty("why").GetString()!;
        Assert.StartsWith("Estimate", why);
        Assert.Contains("19 sold comps", why, StringComparison.Ordinal);
        // Not yet confident, so the card offers the live lookup.
        Assert.True(ev.GetProperty("canLive").GetBoolean());
    }

    [Fact]
    public void A_Facebook_row_backed_by_matching_comps_is_backed_and_has_nothing_left_to_fetch()
    {
        var ev = Evidence("""{"source":"facebook","evidenceTier":"confident","soldCompCount":8,"pricedCompCount":8,"ebayExpectedSale":200,"netProfit":123.1}""");

        Assert.Equal("Backed", ev.GetProperty("word").GetString());
        Assert.False(ev.GetProperty("guess").GetBoolean());
        Assert.False(ev.GetProperty("canLive").GetBoolean());
    }

    [Fact]
    public void A_Facebook_row_with_no_comps_says_so_and_offers_the_live_lookup()
    {
        // This is the card the whole change is about: "no sold data" used to be the end of it. The
        // row arrives exactly as the server sends it — with the comps provider's "manual / no sold
        // history" valuation, which is NOT a refusal and must not hide the lookup.
        var ev = Evidence("""{"source":"facebook","evidenceTier":"none","soldCompCount":0,"pricedCompCount":0,"verdict":"no_data","valuation":{"status":"manual","providerId":"ebay_comps","sourceLabel":"no sold history","confidence":"none","hasPrice":false}}""");

        Assert.False(ev.GetProperty("priceable").GetBoolean());
        Assert.Equal("No sold data", ev.GetProperty("word").GetString());
        Assert.True(ev.GetProperty("canLive").GetBoolean());
    }

    [Fact]
    public void The_boards_own_reprice_rule_tells_no_comps_apart_from_refused()
    {
        // One rule for the board's rows and the picks' cards (canReprice), run as shipped.
        Assert.True(CanReprice("""{"source":"facebook","valuation":{"status":"manual","providerId":"ebay_comps"}}"""));
        Assert.True(CanReprice("""{"source":"ebay","valuation":{"status":"comps","providerId":"ebay_comps"}}"""));
        Assert.True(CanReprice("""{"source":"craigslist"}"""));
        Assert.False(CanReprice("""{"source":"craigslist","valuation":{"status":"manual","providerId":"vehicle_book"}}"""));
        Assert.False(CanReprice("""{"source":"facebook","liquidation":{"lotSize":4}}"""));
        Assert.False(CanReprice("""{"source":"facebook","freebie":{"urgency":"today"}}"""));
    }

    [Fact]
    public void Comps_for_a_different_product_are_an_estimate_however_many_there_are()
    {
        var ev = Evidence("""{"source":"facebook","evidenceTier":"low","identityVerified":false,"soldCompCount":25,"pricedCompCount":25,"ebayExpectedSale":75,"netProfit":-1771}""");

        Assert.True(ev.GetProperty("guess").GetBoolean());
        Assert.Contains("different product", ev.GetProperty("why").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_a_title_lookup_cannot_reprice_get_no_live_button()
    {
        // The board's canReprice rule, on the card: lots and freebies are priced by their own
        // arithmetic, and a refused category would only be refused again.
        Assert.False(Evidence("""{"evidenceTier":"none","soldCompCount":0,"liquidation":{"lotSize":4}}""").GetProperty("canLive").GetBoolean());
        Assert.False(Evidence("""{"evidenceTier":"none","soldCompCount":0,"freebie":{"urgency":"today"}}""").GetProperty("canLive").GetBoolean());
        Assert.False(Evidence("""{"evidenceTier":"none","soldCompCount":0,"valuation":{"status":"manual","providerId":"vehicle_book"}}""").GetProperty("canLive").GetBoolean());
    }

    // ── Running the shipped rules ───────────────────────────────────────────────

    private static JsonElement Evidence(string rowJson) =>
        RunRule(FunctionBody("facebookPickEvidence"), "facebookPickEvidence", rowJson);

    private static bool CanReprice(string rowJson) =>
        RunRule(FunctionBody("canReprice") + "\n" + FunctionBody("valuationRefused"), "canReprice", rowJson).GetBoolean();

    private static JsonElement RunRule(string source, string call, string rowJson)
    {
        var driver =
            source + "\n" +
            "const row = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));\n" +
            $"console.log(JSON.stringify({call}(row)));\n";

        var stamp = Guid.NewGuid().ToString("N");
        var scriptFile = Path.Combine(Path.GetTempPath(), $"fb_pick_evidence_{stamp}.cjs");
        var dataFile = Path.Combine(Path.GetTempPath(), $"fb_pick_evidence_{stamp}.json");
        File.WriteAllText(scriptFile, driver);
        File.WriteAllText(dataFile, rowJson);

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
            Assert.True(proc.WaitForExit(30000), "the evidence rule did not finish");
            Assert.True(proc.ExitCode == 0, $"the pick evidence rule threw:\n{stderr}");

            return JsonDocument.Parse(stdout.Trim()).RootElement.Clone();
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
            try { File.Delete(dataFile); } catch { }
        }
    }

    // ── Reading the source ──────────────────────────────────────────────────────

    /// <summary>A top-level <c>function name(...) { ... }</c> from app.js. Functions in that file sit two spaces in, so the first brace back at that column closes it.</summary>
    private static string FunctionBody(string name)
    {
        var start = js_index(name);
        var end = Js.IndexOf("\n  }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of {name}()");
        return Js[start..(end + 4)];
    }

    private static int js_index(string name)
    {
        var at = Js.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name}() is gone from app.js");
        return at;
    }

    /// <summary>One minimal-API handler's source: from its Map call to the next one.</summary>
    private static string Handler(string route)
    {
        var start = Program.IndexOf($"\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{route} is no longer mapped in Program.cs");
        var end = Program.IndexOf("\napp.Map", start, StringComparison.Ordinal);
        return end > start ? Program[start..end] : Program[start..];
    }

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"\"{from}\" is gone from the source");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end > start ? source[start..end] : source[start..];
    }

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(RepoRoot(), "ING eBay AutoLister", relative);
        Assert.True(File.Exists(path), "missing source file: " + path);
        return File.ReadAllText(path);
    }

    private static string ReadAsset(string name)
    {
        var path = Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
