namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The seller's SKU, followed from the draft that minted it to the cost basis it unlocks.
/// </summary>
/// <remarks>
/// <para>
/// The chain has five links and every one of them is a place it can silently come apart: the WhatsNot
/// draft writes a SKU, the editor carries it without letting anyone retype it, the publish sends it
/// to eBay, the publish endpoint joins it to the deal card it names, and the pipeline's own
/// <c>ApplyDealCostBasis</c> writes the cost. Break any link and there is no error anywhere — just a
/// listing that sells for a profit figure the app made up.
/// </para>
/// <para>
/// <b>The arithmetic is not here and must not arrive here.</b> There is one function in this app that
/// turns a purchase price into a cost basis, and the publish path calls it rather than computing a
/// second answer.
/// </para>
/// <para>
/// <b>Bookkeeping never fails a publish.</b> By the time this runs the listing is live on eBay, so
/// everything on this path is caught and said, never raised.
/// </para>
/// </remarks>
public class PublishedCostAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Ebay = ReadSource(Path.Combine("Services", "EbayService.cs"));
    private static readonly string Listing = ReadSource(Path.Combine("Services", "WonLotListing.cs"));
    private static readonly string Models = ReadSource(Path.Combine("Models", "ListingData.cs"));

    // ── The SKU reaches eBay ──────────────────────────────────────────────────

    /// <summary>
    /// The bug this closes. Every publish used to mint <c>SKU-{guid}</c> and throw the seller's own
    /// code away, which is why <c>CostBasisStore</c>'s SKU fallback — the thing that keeps a
    /// relisted item's cost — had never once fired for a listing this app created.
    /// </summary>
    [Fact]
    public void The_publish_sends_the_sellers_own_sku_rather_than_minting_one()
    {
        var publish = Between(Ebay, "public async Task<PublishListingResult> PublishListingAsync",
                              "// ── Trading API: AddFixedPriceItem");

        Assert.Contains("SellerSku.Sanitize(req.Sku)", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.NewGuid()", publish, StringComparison.Ordinal);
        Assert.Contains("new PublishListingResult(\"\", listingId, sku)", publish, StringComparison.Ordinal);
    }

    /// <summary>
    /// A listing with no SKU publishes with no SKU. Writing a random code onto somebody's live
    /// listing puts a key in their Seller Hub, their reports and their exports that they did not
    /// choose and cannot look anything up by.
    /// </summary>
    [Fact]
    public void A_listing_without_a_sku_carries_no_sku_element_at_all()
        => Assert.Contains("{(sku.Length == 0 ? \"\" : $\"<SKU>{Xe(sku)}</SKU>\")}", Ebay, StringComparison.Ordinal);

    /// <summary>The Inventory API addresses every call TO the SKU, so there it cannot be blank —
    /// but it still prefers the seller's own before minting.</summary>
    [Fact]
    public void The_inventory_api_path_prefers_the_sellers_sku_and_mints_only_as_a_fallback()
    {
        Assert.Contains("var sku = SellerSku.For(req.Sku);", Ebay, StringComparison.Ordinal);
        Assert.Contains("public static string Mint()", ReadSource(Path.Combine("Services", "SellerSku.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One property, not two. <c>UpdateListingRequest</c> used to redeclare <c>Sku</c>, so an edit
    /// and a publish had different properties of the same name and anything holding one as a
    /// <c>PostListingRequest</c> read the blank base.
    /// </summary>
    [Fact]
    public void There_is_one_sku_property_on_the_listing_request()
    {
        Assert.Contains("public string Sku { get; set; } = \"\";", Models, StringComparison.Ordinal);

        var update = Between(Models, "public class UpdateListingRequest : PostListingRequest", "public class ImproveSeoRequest");
        Assert.DoesNotContain("public string Sku", update, StringComparison.Ordinal);
    }

    // ── The publish joins the listing to what it cost ─────────────────────────

    [Fact]
    public void The_publish_endpoint_links_the_cost_and_has_the_stores_to_do_it()
    {
        var endpoint = PublishEndpoint();

        Assert.Contains("DealStore deals, CostBasisStore costBasis", endpoint, StringComparison.Ordinal);
        Assert.Contains("LinkPublishedCost(result.Sku, result.ListingId, req.Price, deals, costBasis, earnings, log)",
            endpoint, StringComparison.Ordinal);
        Assert.Contains("costMessage = costLink", endpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is one function in this app that turns a purchase price into a cost basis. A second one
    /// on this path would be a second answer to "what did this cost", and the two would drift.
    /// </summary>
    [Fact]
    public void The_cost_is_written_by_the_pipelines_own_function_and_not_computed_here()
    {
        var linker = Linker();

        Assert.Contains("ApplyDealCostBasis(moved, earnings, costBasis)", linker, StringComparison.Ordinal);
        Assert.DoesNotContain("new CostBasisEntry", linker, StringComparison.Ordinal);
        Assert.DoesNotContain("costBasis.Save", linker, StringComparison.Ordinal);
    }

    /// <summary>The decision — which deal, and whether to write at all — is the pure one, tested on
    /// its own. The endpoint may carry it out; it may not make one of its own.</summary>
    [Fact]
    public void Which_deal_this_listing_belongs_to_is_decided_by_the_pure_rule()
    {
        var linker = Linker();

        Assert.Contains("PublishedCostLink.Decide(sku, listingId, deals.GetAll(), costBasis.GetAll())",
            linker, StringComparison.Ordinal);
        Assert.Contains("if (!plan.ShouldWrite)", linker, StringComparison.Ordinal);
        Assert.DoesNotContain("d.Title ==", linker, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listing is already live by the time this runs. A bookkeeping failure that surfaced as a
    /// publish error would send the seller looking for a listing that exists — or publishing it a
    /// second time.
    /// </summary>
    [Fact]
    public void Nothing_about_the_bookkeeping_can_fail_the_publish()
    {
        var linker = Linker();

        Assert.Contains("try", linker, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", linker, StringComparison.Ordinal);
        Assert.Contains("Could not record what this listing cost", linker, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", linker, StringComparison.Ordinal);
    }

    /// <summary>Publishing is what Listed means — but only for a card that has not got there yet.
    /// Sold stays sold.</summary>
    [Fact]
    public void The_card_is_only_advanced_when_the_rule_said_to()
        => Assert.Contains("Stage = plan.AdvanceToListed ? DealStages.Listed : null,", Linker(), StringComparison.Ordinal);

    // ── The screen ────────────────────────────────────────────────────────────

    /// <summary>The code was minted by the screen that knew what the lot cost. Retyping it is how
    /// the join breaks, so the field is carried rather than edited.</summary>
    [Fact]
    public void The_editor_carries_the_sku_without_offering_it_to_be_retyped()
    {
        Assert.Contains("<input id=\"nl-sku\" type=\"hidden\" />", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-sku-note\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_publish_payload_carries_the_sku_the_draft_arrived_with()
        => Assert.Contains("sku: ($('nl-sku')?.value || '').trim(),", Js, StringComparison.Ordinal);

    /// <summary>A SKU left behind on a cleared form would attach the next listing to the last
    /// lot's cost — the one failure on this path that writes a wrong number rather than none.</summary>
    [Fact]
    public void Opening_a_draft_sets_the_sku_and_clearing_the_form_drops_it()
    {
        Assert.Contains("function nlClearForm()", Js, StringComparison.Ordinal);
        Assert.Contains("nlSetSku('');", Js, StringComparison.Ordinal);
        Assert.Contains("nlSetSku(d.sku || '');", Js, StringComparison.Ordinal);
    }

    /// <summary>Hidden is not invisible: the line says what the code is for, because it is about
    /// money already spent.</summary>
    [Fact]
    public void The_line_under_the_sku_says_what_it_is_for()
    {
        var fn = Between(Js, "function nlSetSku(sku)", "function nlClearForm()");

        Assert.Contains("Money Made", fn, StringComparison.Ordinal);
        Assert.Contains("note.classList.add('hidden')", fn, StringComparison.Ordinal);
        Assert.Contains(".nl-sku-note {", Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// Said whether it worked or not. A seller who believes the cost was captured, and finds out
    /// months later that it was not, was told the wrong thing at the one moment they could have
    /// fixed it.
    /// </summary>
    [Fact]
    public void What_the_publish_did_about_the_money_is_shown_in_the_servers_own_words()
    {
        Assert.Contains("if (body.costMessage) {", Js, StringComparison.Ordinal);
        Assert.Contains("esc(body.costMessage)", Js, StringComparison.Ordinal);
        Assert.Contains("addActivity('What this listing cost', body.costMessage);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assets_are_versioned_past_the_build_that_shipped_without_the_sku()
    {
        Assert.True(AssetVersion("app.js") >= 123, "app.js changed, so index.html's ?v= must move past 122");
        Assert.True(AssetVersion("style.css") >= 106, "style.css changed, so index.html's ?v= must move past 105");
    }

    // ── The won lot, which is where the SKU comes from ────────────────────────

    /// <summary>
    /// Minted once, given to both. <c>Sku()</c> falls back to a fresh GUID for a lot whose id is too
    /// short, so calling it twice could hand back two codes — and the join between the listing and
    /// its cost would silently not exist.
    /// </summary>
    [Fact]
    public void The_draft_and_the_deal_card_are_given_one_minted_sku()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/list\"", "// The show ended.");

        Assert.Contains("var sku = WonLotListing.Sku(lot);", endpoint, StringComparison.Ordinal);
        Assert.Contains("WonLotListing.Draft(lot, sku)", endpoint, StringComparison.Ordinal);
        Assert.Contains("WonLotListing.Deal(lot, now, sku)", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_draft_a_won_lot_makes_carries_that_sku_onto_the_listing()
    {
        var draft = Between(Listing, "public static DraftFile Draft(WonLot lot, string sku)", "// ── The deal card");

        Assert.Contains("Sku = SellerSku.Sanitize(sku),", draft, StringComparison.Ordinal);
    }

    // ── Additive ──────────────────────────────────────────────────────────────

    /// <summary>Sold comps and every WhatsNot endpoint are still registered. A session that touches
    /// the publish path is exactly the kind of session that quietly breaks a screen it never opened.</summary>
    [Fact]
    public void Sold_comps_and_every_whatsnot_endpoint_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/embed-check",
                     "/api/whatsnot/list",
                 })
        {
            Assert.Contains(route, Program, StringComparison.Ordinal);
        }
    }

    /// <summary>The two ways a cost basis was already written are untouched: moving a card to
    /// Listed, and "Apply what you paid" on a deal whose sales are sitting uncounted.</summary>
    [Fact]
    public void The_deal_boards_own_routes_to_a_cost_basis_still_work()
    {
        Assert.Contains("app.MapPost(\"/api/deals/{id:long}/stage\"", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/deals/{id:long}/apply-cost\"", Program, StringComparison.Ordinal);
    }

    /// <summary>The duplicate-publish guard still runs before anything is sent, and still keys on
    /// the listing's content. Two live listings for one item is the worst outcome this path has.</summary>
    [Fact]
    public void The_duplicate_publish_guard_is_untouched()
    {
        var endpoint = PublishEndpoint();

        Assert.Contains("var fingerprint = PublishGuard.Fingerprint(req);", endpoint, StringComparison.Ordinal);
        Assert.Contains("guard.Succeeded(fingerprint, req.WorkKey, result.ListingId);", endpoint, StringComparison.Ordinal);
        Assert.Contains("PublishDecision.AlreadyPublished", endpoint, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string PublishEndpoint() =>
        Between(Program, "app.MapPost(\"/api/listing/publish\"", "// Answers \"is it actually live?\"");

    private static string Linker() =>
        Between(Program, "static string LinkPublishedCost(", "// One place that assembles the board");

    private static int AssetVersion(string file)
    {
        var marker = $"{file}?v=";
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, $"{file} carries no cache-buster");
        var digits = new string(Html[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.Parse(digits);
    }

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of \"{from}\"");
        return source[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", relativePath));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
