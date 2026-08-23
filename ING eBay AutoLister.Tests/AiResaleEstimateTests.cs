using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Every card carries a number, even when nothing has sold under that name.
/// </summary>
/// <remarks>
/// The owner, 2026-08-21: "All products need an estimated cost and the AI has to give an estimated
/// cost — guess — it does not have to be perfect", and "AI can check all other sources of sold
/// items across the web". Most of a Marketplace feed has no eBay sold history under the name the
/// seller typed; "No sold data" is true and useless to somebody deciding whether to drive across
/// town. So the AI prices those from the wider second-hand market — and the whole safety of that
/// is that its answer is labelled a guess and can never become evidence.
/// </remarks>
public class AiResaleEstimateTests
{
    private static readonly string Service = ReadSource("ING eBay AutoLister/Services/ClaudeService.cs");
    private static readonly string Program = ReadSource("ING eBay AutoLister/Program.cs");
    private static readonly string Js = ReadSource("ING eBay AutoLister/wwwroot/app.js");

    [Fact]
    public void The_card_shows_the_middle_of_the_range()
    {
        Assert.Equal(17.50m, new AiResaleEstimate("1", 10m, 25m, "keys").Mid);
        Assert.Equal(115m, new AiResaleEstimate("2", 80m, 150m, "drill").Mid);
    }

    [Fact]
    public void The_board_is_priced_in_chunks_small_enough_to_come_back_whole()
    {
        // It WAS one call for sixty items. Measured 2026-08-23: sixty rows of JSON do not fit in a
        // 4096-token ceiling that adaptive thinking and web search spend from too, so the reply
        // ended mid-object and the strict reader threw the whole batch away — three times, because
        // the retry re-sent an identical prompt and truncation is deterministic. Chunks of fifteen
        // sit far inside the ceiling, and a chunk that still fails costs fifteen rows, not sixty.
        Assert.Contains("public async Task<List<AiResaleEstimate>> EstimateResaleAsync(", Service, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<AiEstimateItem> items", Service, StringComparison.Ordinal);
        Assert.Contains("const int chunkSize = 15;", Service, StringComparison.Ordinal);
        Assert.Contains("private async Task<List<AiResaleEstimate>> EstimateChunkAsync(", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void One_failed_chunk_does_not_abandon_the_rest_of_the_board()
    {
        // The owner, 2026-08-23: "The bot needs to price all the listings - people will wait for
        // good results." Fifteen rows failing is a smaller loss than the other eighty-five going
        // unpriced with them.
        Assert.Contains("AI resale estimate — one batch failed", Service.Replace("—", "—"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_reply_keeps_the_prices_that_arrived()
    {
        Assert.Contains("JsonSalvage.CompleteObjects(raw)", Service, StringComparison.Ordinal);
        Assert.Contains("reply ran out of room", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_told_to_price_the_whole_resale_market_not_just_eBay()
    {
        Assert.Contains("Mercari, OfferUp, Facebook Marketplace, and auction results", Service, StringComparison.Ordinal);
        Assert.Contains("Not the", Service, StringComparison.Ordinal);   // not the asking price, not retail
    }

    [Fact]
    public void A_thing_that_cannot_be_resold_is_omitted_rather_than_guessed_at()
    {
        // Proven live on the owner's own board: "6+ Acres of Tillable Cornfields" came back with no
        // estimate at all, while five ordinary goods beside it were priced. A missing row is a
        // correct answer; an invented land valuation on a reseller's card is not.
        Assert.Contains("OMIT the item entirely", Service, StringComparison.Ordinal);
        Assert.Contains("real estate", Service, StringComparison.Ordinal);
        Assert.Contains("A missing row is a correct answer", Service, StringComparison.Ordinal);
        Assert.Contains("Never return 0.", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_that_arrived_broken_never_reaches_a_card()
    {
        // Backwards, negative or zero is a model slip, not a price.
        Assert.Contains("e.Low > 0 && e.High > 0", Service, StringComparison.Ordinal);
        Assert.Contains("Low   = Math.Min(e.Low, e.High)", Service, StringComparison.Ordinal);
    }

    [Fact]
    public void A_board_that_cannot_reach_the_model_still_shows_its_priced_cards()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/local/ai-estimate\"", "app.MapPost(\"/api/local/price-these\"");

        // The comp-priced cards are unaffected by an AI outage, so this answers empty rather than
        // failing the request and taking the whole board down with it.
        Assert.Contains("catch (Exception ex)", endpoint, StringComparison.Ordinal);
        Assert.Contains("estimates = Array.Empty<AiResaleEstimate>()", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_cards_the_comps_could_not_price_are_sent()
    {
        var fn = Between(Js, "async function aiEstimateUnpricedPicks(", "  /**");

        Assert.Contains("if (!row || facebookPickEvidence(row).priceable) continue;", fn, StringComparison.Ordinal);
        Assert.Contains("'/api/local/ai-estimate'", fn, StringComparison.Ordinal);
        // Written only onto the row's own scratch field — never into the graded numbers.
        Assert.Contains("row.__aiEstimate = est;", fn, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guess_is_labelled_a_guess_and_never_becomes_evidence()
    {
        var card = Between(Js, "function facebookPickMoneyHtml(row)", "  // The card is a link to Marketplace");

        Assert.Contains("AI estimate", card, StringComparison.Ordinal);
        Assert.Contains("fb-pick-ev-ai", card, StringComparison.Ordinal);
        Assert.Contains("could sell ", card, StringComparison.Ordinal);

        // The grade the row was given is computed from comps alone; nothing in the estimator
        // touches evidenceTier, identityVerified or the comp counts.
        var estimator = Between(Js, "async function aiEstimateUnpricedPicks(", "  /**");
        Assert.DoesNotContain("evidenceTier", estimator, StringComparison.Ordinal);
        Assert.DoesNotContain("identityVerified", estimator, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_lot_nothing_could_price_still_gets_a_number()
    {
        // The bidding is running and "CAN'T PRICE IT" tells somebody holding a paddle nothing they
        // can act on. The AI's read goes on the card — after the stored comps and the live lookup
        // have both had their go, never instead of them.
        var fn = Between(Js, "async function wnAiEstimate(item)", "  function wnRenderCard(c)");

        Assert.Contains("'/api/local/ai-estimate'", fn, StringComparison.Ordinal);
        Assert.Contains("AI estimate", fn, StringComparison.Ordinal);
        // It is never allowed to become the ceiling: that is arithmetic on comps, and there are none.
        Assert.Contains("not a ceiling", fn, StringComparison.Ordinal);
        Assert.DoesNotContain("maxBid", fn, StringComparison.Ordinal);

        // And it only runs once the comps have failed.
        var price = Between(Js, "async function wnPriceItem()", "  /// ── The one line ─");
        Assert.Contains("await wnAiEstimate(item)", price, StringComparison.Ordinal);
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}'");
        return text[start..end];
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root");
        var path = Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "missing source file: " + path);
        return File.ReadAllText(path);
    }
}
