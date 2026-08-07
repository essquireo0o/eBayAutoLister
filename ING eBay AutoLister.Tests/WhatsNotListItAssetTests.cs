namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The last step of the WhatsNot screen: the won lot becomes a listing.
/// </summary>
/// <remarks>
/// <para>
/// <b>It prices nothing.</b> The ask is <c>WonLotListing.AskingPrice</c> — the card's own
/// comps-derived resale figure, charm-rounded on the server. Neither this endpoint nor the browser
/// is allowed to arrive at a price of its own, or the app would bid on one valuation and list on
/// another, and the seller would never know which one was the evidence.
/// </para>
/// <para>
/// <b>It publishes nothing.</b> A draft on disk and a card on the deal board. Nothing here touches
/// eBay at all — no read, no write — which is also why it answers instantly.
/// </para>
/// <para>
/// <b>It writes once.</b> A row that is already a draft hands back the draft that exists. Two files
/// for one item is two listings of one item, and the second one sells something that is already
/// gone.
/// </para>
/// <para>
/// <b>It is additive.</b> Sold comps, the card, the re-price, the win, the lot list and the embed
/// check are all asserted to still be registered — a session that adds a button to a screen is
/// exactly the kind of session that quietly takes an endpoint off it.
/// </para>
/// </remarks>
public class WhatsNotListItAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Listing = ReadSource(Path.Combine("Services", "WonLotListing.cs"));
    private static readonly string Sheet = ReadSource(Path.Combine("Services", "LiveBuySheet.cs"));

    // ── The endpoint ──────────────────────────────────────────────────────────

    [Fact]
    public void The_list_endpoint_is_registered_and_behind_the_trial_guard()
    {
        Assert.Contains("app.MapPost(\"/api/whatsnot/list\"", Program, StringComparison.Ordinal);
        Assert.Contains("TrialGuard(store, license)", ListEndpoint(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything the listing editor would ask for is already on the row, so this is arithmetic and
    /// two writes. An eBay read here would put a network round trip between "I won it" and the
    /// draft, on the one screen whose whole promise is an answer in seconds.
    /// </summary>
    [Fact]
    public void Listing_a_won_lot_never_reads_ebay()
    {
        var endpoint = ListEndpoint();

        Assert.DoesNotContain("AnalyzeProductAsync", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplace", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("await", endpoint, StringComparison.Ordinal);
    }

    /// <summary>A draft is a file on the seller's desktop. Nothing on this path goes live, and no
    /// button on this screen is one press away from an eBay listing.</summary>
    [Fact]
    public void Listing_a_won_lot_publishes_nothing()
    {
        var endpoint = ListEndpoint();

        foreach (var forbidden in new[] { "EbayService", "PostListingAsync", "PublishOffer", "ebay." })
            Assert.DoesNotContain(forbidden, endpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one rule that keeps the draft honest: the ask comes back from the shared builder, which
    /// takes the card's own resale price. An endpoint that reached for a price itself would be a
    /// second opinion about what the item is worth, and it would be the one on the listing.
    /// </summary>
    [Fact]
    public void The_ask_comes_from_the_shared_builder_and_is_not_computed_here()
    {
        var endpoint = ListEndpoint();

        Assert.Contains("WonLotListing.AskingPrice(lot)", endpoint, StringComparison.Ordinal);
        // The SKU is minted once above and handed to the draft, so the file on the desktop and the
        // card on the board provably describe one lot — and the publish can find the cost by it.
        Assert.Contains("WonLotListing.Draft(lot, sku)", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Charm(", endpoint, StringComparison.Ordinal);
    }

    /// <summary>Two drafts of one lot is two listings of one item — and the second sells something
    /// that has already gone.</summary>
    [Fact]
    public void A_row_that_is_already_a_draft_gets_that_draft_back_rather_than_a_second_one()
    {
        var endpoint = ListEndpoint();

        Assert.Contains("if (lot.ListedDraftFile.Length > 0)", endpoint, StringComparison.Ordinal);
        Assert.Contains("AlreadyListed = true", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_that_is_no_longer_on_the_sheet_is_refused_in_words()
    {
        var endpoint = ListEndpoint();

        Assert.Contains("sheet.Find(req.Id)", endpoint, StringComparison.Ordinal);
        Assert.Contains("That lot isn't on the sheet", endpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The draft is the point; the board card is the bookkeeping. A board rule this lot happens to
    /// trip must not cost the seller the listing — so it is reported as untracked rather than
    /// failing the press, and the sentence stops claiming the board.
    /// </summary>
    [Fact]
    public void A_deal_board_refusal_does_not_cost_the_seller_the_draft()
    {
        var endpoint = ListEndpoint();

        Assert.Contains("long dealId = 0;", endpoint, StringComparison.Ordinal);
        Assert.Contains("Won lot not tracked on the deal board", endpoint, StringComparison.Ordinal);
        Assert.Contains("onDealBoard: dealId > 0", endpoint, StringComparison.Ordinal);
    }

    /// <summary>The link between this row and that file is knowable only now — the app does not
    /// watch the drafts folder — so it is written down at the moment it is true.</summary>
    [Fact]
    public void The_row_is_marked_listed_and_the_whole_sheet_comes_back()
    {
        var endpoint = ListEndpoint();

        Assert.Contains("sheet.MarkListed(lot.Id, filename, draft.Title, price, sku, dealId, now)",
            endpoint, StringComparison.Ordinal);
        Assert.Contains("Sheet = updated", endpoint, StringComparison.Ordinal);
    }

    // ── What the draft refuses to invent ──────────────────────────────────────

    /// <summary>
    /// The eBay category is decided from the title by the editor's own suggester. The card's
    /// category is a RESALE category — the thing that decides which sold comps count — and writing
    /// one into the other would file the listing in the wrong department silently.
    /// </summary>
    [Fact]
    public void The_draft_carries_no_ebay_category_of_its_own()
    {
        var draft = Between(Listing, "public static DraftFile Draft(WonLot lot)", "// ── The deal card");

        Assert.DoesNotContain("CategoryId =", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("Category =", draft, StringComparison.Ordinal);
    }

    /// <summary>The condition and the photos are not knowable from a live feed, and a draft that
    /// guessed them would make a claim to a buyer on the seller's behalf.</summary>
    [Fact]
    public void What_the_draft_cannot_know_is_said_out_loud_rather_than_guessed()
    {
        Assert.Contains("Set the condition and add photos", Listing, StringComparison.Ordinal);
        Assert.Contains("public static List<string> Notes(WonLot lot)", Listing, StringComparison.Ordinal);
    }

    /// <summary>The rounding is the app's own, shared with the repricer and the relister, floored at
    /// what the lot cost.</summary>
    [Fact]
    public void The_ask_is_charm_rounded_by_the_shared_rule_and_floored_at_the_landed_cost()
        => Assert.Contains("InventoryHealthAnalyzer.Charm(Math.Round(resale, 2), floorPrice: lot.LandedCost)",
            Listing, StringComparison.Ordinal);

    /// <summary>The split the cost-basis table wants, so the pipeline can write a real basis when
    /// the listing goes live — which is the step that makes Money Made able to say whether the show
    /// made money.</summary>
    [Fact]
    public void The_deal_carries_the_premium_as_unit_cost_and_the_shipping_as_freight()
    {
        Assert.Contains("PurchasePrice = Math.Round(lot.WinningBid + lot.BuyerFee, 2)", Listing, StringComparison.Ordinal);
        Assert.Contains("PurchaseExtraCost = Math.Round(lot.ShippingCost, 2)", Listing, StringComparison.Ordinal);
        Assert.Contains("SourceItemId = lot.Id", Listing, StringComparison.Ordinal);
    }

    // ── The screen ────────────────────────────────────────────────────────────

    /// <summary>The browser sends an id and paints what comes back. Every figure on the row — the
    /// ask, the sentence, the totals — is written on the server beside the arithmetic.</summary>
    [Fact]
    public void The_browser_sends_an_id_and_computes_no_money_of_its_own()
    {
        var fn = Between(Js, "async function wnListLot(id)", "function wnOpenDraft(filename)");

        Assert.Contains("safePost('/api/whatsnot/list', { id })", fn, StringComparison.Ordinal);
        Assert.Contains("if (body.sheet) wnRenderSheet(body.sheet);", fn, StringComparison.Ordinal);
        Assert.DoesNotContain(".99", fn, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.floor", fn, StringComparison.Ordinal);
    }

    /// <summary>Both the sentence and what the draft could not decide are the server's own words.</summary>
    [Fact]
    public void The_line_above_the_card_is_the_servers_sentence_and_its_notes()
        => Assert.Contains("wnSayLine([body.say, ...(body.notes || [])].filter(Boolean).join(' '));",
            Js, StringComparison.Ordinal);

    /// <summary>A listed row offers the draft, not the button that made it. Pressing List it twice
    /// is how a seller ends up with two listings of one item.</summary>
    [Fact]
    public void A_listed_row_offers_the_draft_instead_of_the_button()
    {
        var render = Between(Js, "const listed = !!l.listedDraftFile;", "</li>`;");

        Assert.Contains("data-open-draft=", render, StringComparison.Ordinal);
        Assert.Contains("data-list=", render, StringComparison.Ordinal);
        Assert.Contains("listed", render, StringComparison.Ordinal);
    }

    /// <summary>The draft opens through the SAME path every other local draft opens through — tab,
    /// form, policies and focus. A second copy of that is a second set of bugs.</summary>
    [Fact]
    public void The_draft_opens_through_the_shared_local_draft_path()
    {
        var fn = Between(Js, "function wnOpenDraft(filename)", "// ── WhatsNot: the show's lot list");

        Assert.Contains("openCopilotDrafts([filename]", fn, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/local-drafts/load", fn, StringComparison.Ordinal);
    }

    /// <summary>None of the projected resale arrives while the lots are in boxes, so how many are
    /// still unlisted is a figure on the sheet rather than something to count by eye.</summary>
    [Fact]
    public void The_totals_say_how_much_of_the_night_is_still_unlisted()
    {
        Assert.Contains("wnSheetTile('Still to list'", Js, StringComparison.Ordinal);
        Assert.Contains("(sheet.lotCount || 0) - (sheet.listedCount || 0)", Js, StringComparison.Ordinal);
        Assert.Contains("ListedCount = rows.Count(l => l.ListedDraftFile.Length > 0)", Sheet, StringComparison.Ordinal);
    }

    /// <summary>Every button on this screen has a name a screen reader can use, because the screen
    /// is used with a stream running and the keyboard is the fast way through it.</summary>
    [Fact]
    public void Both_row_buttons_are_named_for_the_lot_they_act_on()
    {
        var render = Between(Js, "const listed = !!l.listedDraftFile;", "</li>`;");

        Assert.Contains("aria-label=\"Open the draft for ", render, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"List ", render, StringComparison.Ordinal);
    }

    /// <summary>The way this screen is used is a narrow window down the side of a live stream, so
    /// the one action that turns tonight's spend into money keeps a full-sized target.</summary>
    [Fact]
    public void The_list_button_folds_on_a_narrow_window()
    {
        var narrow = Between(Css, "Narrower than this the app is a window down the side of a live stream", ".wn-frame {");

        Assert.Contains(".wn-sheet-draft", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_button_has_styles_of_its_own_and_a_visible_focus_ring()
    {
        Assert.Contains(".wn-sheet-draft {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-sheet-draft-done {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-sheet-draft:focus-visible {", Css, StringComparison.Ordinal);
    }

    // ── Additive, as every WhatsNot session has been ──────────────────────────

    [Fact]
    public void Sold_comps_and_every_earlier_whatsnot_endpoint_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/embed-check",
                 })
        {
            Assert.Contains(route, Program, StringComparison.Ordinal);
        }
    }

    /// <summary>The Copilot's own drafts still open the way they always did — the shared function
    /// grew an optional sentence, not a new behaviour.</summary>
    [Fact]
    public void The_copilots_drafts_still_open_the_way_they_did()
    {
        Assert.Contains("async function openCopilotDrafts(filenames, said)", Js, StringComparison.Ordinal);
        Assert.Contains("said?.title || `Opened ${wanted.length} rewritten draft", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_the_win_and_the_lot_list_are_untouched_by_this_pass()
    {
        Assert.Contains("const WN_REBID_DEBOUNCE_MS = 90;", Js, StringComparison.Ordinal);
        Assert.Contains("function wnPriceLotList()", Js, StringComparison.Ordinal);
        Assert.Contains("safePost('/api/whatsnot/won'", Js, StringComparison.Ordinal);
        // The argument list grows between sessions; the property is that a win is still the same
        // advisor pricing the same held quote at the price it hammered at.
        Assert.Contains("quote.Item, quote.Analysis, req.AsBid(), feeProfile, quote.Category",
            Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assets_are_versioned_past_the_build_that_shipped_without_the_list_button()
    {
        Assert.True(AssetVersion("app.js") >= 122, "app.js changed, so index.html's ?v= must move past 121");
        Assert.True(AssetVersion("style.css") >= 105, "style.css changed, so index.html's ?v= must move past 104");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string ListEndpoint() =>
        Between(Program, "app.MapPost(\"/api/whatsnot/list\"", "// The show ended.");

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
