using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Amazon half of the AI Listing screen — the panel, the switch that reaches it, and the three
/// sentences it is not allowed to stop saying.
/// </summary>
/// <remarks>
/// <para>
/// Phases 1–4 built an Amazon pipeline that refuses to lie: it will not call a queued submission a
/// published listing (<see cref="AmazonSubmissionWords.ForbiddenWords"/>), will not invent a GTIN to
/// close a gap, and will not submit to production at all
/// (<see cref="AmazonSubmitGuard"/>). None of that survives contact with a UI that renders
/// <c>state: "submitted"</c> as a green tick reading "Listed!".
/// </para>
/// <para>
/// Nothing in C# renders this screen, so nothing in C# notices when it starts saying otherwise.
/// These are what notice. They are deliberately about the words and the wiring rather than about
/// layout: a panel that moves is a redesign, a panel that says "published" is a seller waiting for
/// orders on a listing that does not exist.
/// </para>
/// </remarks>
public class AmazonListingUiAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js   = ReadAsset("app.js");
    private static readonly string Css  = ReadAsset("style.css");

    // ── Getting there ────────────────────────────────────────────────────────

    [Fact]
    public void Amazon_is_reachable_from_the_same_screen_that_publishes_to_eBay()
    {
        // Alongside eBay, not a second screen. The draft is one draft; the destination is a switch.
        Assert.Contains("id=\"nl-market-ebay\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-market-amazon\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('nl-market-amazon', 'click', () => nlSetMarket(NL_MARKET_AMAZON))",
                        Js, StringComparison.Ordinal);

        // And it is in the listing modal's footer, beside Publish — not parked on some other page.
        var switchAt  = Html.IndexOf("id=\"nl-market-amazon\"", StringComparison.Ordinal);
        var publishAt = Html.IndexOf("id=\"nl-btn-publish\"", StringComparison.Ordinal);
        var footerAt  = Html.IndexOf("class=\"new-listing-footer\"", StringComparison.Ordinal);
        Assert.True(footerAt > 0 && switchAt > footerAt && switchAt < publishAt,
            "the marketplace switch is no longer in the listing footer above Publish");
    }

    [Fact]
    public void The_switch_carries_the_destination_each_side_actually_reaches()
    {
        // The environment is on the tab. A seller must never have to press something to find out
        // whether it reaches a real account.
        var amazonTab = Slice(Html, "id=\"nl-market-amazon\"", "</button>");
        Assert.Contains("Sandbox", amazonTab, StringComparison.Ordinal);

        var ebayTab = Slice(Html, "id=\"nl-market-ebay\"", "</button>");
        Assert.Contains("Live account", ebayTab, StringComparison.Ordinal);
    }

    // ── What Amazon requires, and what the draft answers ─────────────────────

    [Fact]
    public void The_panel_is_the_same_object_as_the_eBay_readiness_bar()
    {
        // Same classes on purpose: a seller who has learned one has learned the other, and a
        // divergent second implementation is where the two start disagreeing about a draft.
        Assert.Contains("id=\"nl-amz\" class=\"rd-bar amz-bar\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-amz-score\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-amz-grade\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-amz-headline\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-amz-counts\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-amz-list\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_asks_the_endpoints_that_already_exist_rather_than_re_deriving_the_answer()
    {
        Assert.Contains("'/api/amazon/listing-fill'", Js, StringComparison.Ordinal);
        Assert.Contains("'/api/amazon/status'", Js, StringComparison.Ordinal);
        Assert.Contains("'/api/amazon/product'", Js, StringComparison.Ordinal);
        Assert.Contains("/api/amazon/listing-state?sku=", Js, StringComparison.Ordinal);

        // The routes are the ones the app actually maps.
        Assert.Equal("/api/amazon/listing-fill", AmazonListingFillEndpoints.FillPath);
        Assert.Equal("/api/amazon/product",      AmazonSubmitEndpoints.ProductPath);
        Assert.Equal("/api/amazon/listing-state", AmazonSubmitEndpoints.StatePath);
        Assert.Equal("/api/amazon/status",        AmazonStatusEndpoint.Path);
    }

    [Fact]
    public void The_draft_that_is_checked_is_the_draft_on_the_screen()
    {
        // buildNlPayload() verbatim. A re-typed copy is a copy that can disagree with the form the
        // seller is looking at — and the fill endpoint takes the eBay body for exactly this reason.
        Assert.Contains("const draft = buildNlPayload();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_is_the_servers_verdict_and_not_a_more_generous_one_computed_here()
    {
        // AmazonListingFill.CanSubmit is false whenever a required attribute has no value, a value
        // falls outside a closed list, or an either/or requirement is unmet. Recomputing "ready"
        // from the counts in the browser is how a blocked listing turns green.
        Assert.Contains("f.canSubmit       ? 'Ready to submit to the sandbox'", Js, StringComparison.Ordinal);
        Assert.Contains("const blocked = !f.canSubmit;", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_of_what_will_stop_this_is_on_the_panel_and_on_the_button()
    {
        // The same pattern the eBay path uses, with the same phrase, because it is the same
        // promise: the reasons are counted before anything is sent, not after a rejection.
        Assert.Contains("will stop this", Js, StringComparison.Ordinal);
        Assert.Contains("`Submit to Amazon Sandbox (${f.counts.blocking} will stop this)`",
                        Js, StringComparison.Ordinal);
        Assert.Contains("<strong>Amazon would reject this submission, so it was not sent.</strong>",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void An_either_or_requirement_is_rendered_as_one_requirement()
    {
        // Told separately that externally_assigned_product_identifier and merchant_suggested_asin
        // are both missing, a seller reasonably concludes they need both.
        Assert.Contains("'Amazon needs one of: ' + (c.options || []).join(' or ')", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_shows_everything_the_filled_badge_is_counting()
    {
        // The badge counts every filled attribute, optional included. A list built only from the
        // required ones would read "13 filled" over ten rows and send the seller looking for three
        // attributes that are going onto their listing and are nowhere on the screen.
        Assert.Contains("const filled = [...required, ...optional].filter(a => a.state === 'filled');",
                        Js, StringComparison.Ordinal);

        // And the footnote counts only what is genuinely not shown.
        Assert.Contains("const quiet = optional.filter(a => a.state !== 'filled').length;",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filled_attribute_says_where_its_value_came_from()
    {
        // The value goes onto a listing under the seller's account and they are the one answerable
        // for it, so "where did that come from?" has to be answerable without reading the mapper.
        Assert.Contains("` — from ${esc(a.source)}`", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_app_refuses_to_invent_can_be_answered_by_the_seller_on_the_panel()
    {
        // Without this the panel is a dead end rather than a screen. Two of these are required on
        // most product types, neither can be derived from a product description, so the mapper
        // correctly refuses both — and until there was somewhere to answer them, no draft the AI
        // wrote could ever become submittable at all.
        Assert.Contains("function nlAmzAnswerControl(", Js, StringComparison.Ordinal);
        Assert.Contains("draft.sellerAttributes = { ...nlAmzAnswers };", Js, StringComparison.Ordinal);
        Assert.Contains(".amz-answer-input", Css, StringComparison.Ordinal);

        // A closed list gets a list. A free-text box over five words Amazon publishes is how a
        // seller types a sixth and is rejected for it.
        Assert.Contains("if (a.selectionOnly && values.length)", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sellers_answer_is_attributed_to_them_and_still_checked_against_Amazon()
    {
        // Being answered by a human makes a value the seller's to stand behind. It does not make it
        // legal — the schema check is not skipped for it, or the answer box would be a way to post
        // anything at all to Amazon under the seller's account.
        Assert.Equal("you answered it here", AmazonListingMapper.SellerAnswerSource);
        Assert.Contains("Your statement", Js, StringComparison.Ordinal);

        // And it is re-checked the moment it is given, so a rejection lands here rather than
        // against the account minutes later.
        Assert.Contains("nlRunAmazonFill(true);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Declarations_do_not_survive_into_the_next_draft()
    {
        // Carrying "batteries: no" onto the next item would be this app making the statement after
        // all, in the seller's name and about something it has never seen.
        Assert.Contains("Object.keys(nlAmzAnswers).forEach(k => delete nlAmzAnswers[k]);",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_could_not_run_is_never_shown_as_a_check_that_passed()
    {
        var failure = Slice(Js, "// A check that cannot run must never be mistaken", "nlAmzState = data;");
        Assert.Contains("canSubmit: false", failure, StringComparison.Ordinal);
        Assert.Contains("Nothing has been sent.", failure, StringComparison.Ordinal);
    }

    // ── The three sentences it may not stop saying ───────────────────────────

    [Fact]
    public void Every_screen_that_can_submit_says_sandbox()
    {
        // The banner on the panel…
        Assert.Contains("id=\"nl-amz-env-tag\"", Html, StringComparison.Ordinal);
        Assert.Contains(">SANDBOX<", Html, StringComparison.Ordinal);

        // …the tab that got here…
        Assert.Contains("is-sandbox\">Sandbox<", Html, StringComparison.Ordinal);

        // …the note beside the send button…
        Assert.Contains("id=\"nl-amz-send-note\"", Html, StringComparison.Ordinal);

        // …and the button itself. Three places, because this panel is where somebody decides to
        // press send and a sandbox submission mistaken for a real one is a seller waiting for
        // orders that cannot come.
        Assert.Contains("id=\"nl-btn-amz-submit\"", Html, StringComparison.Ordinal);
        Assert.Contains(">Submit to Amazon Sandbox<", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_submission_is_pending_and_is_never_called_published()
    {
        Assert.Contains("stays <strong>pending</strong> until Amazon confirms it", Html, StringComparison.Ordinal);
        Assert.Contains("PENDING", Js, StringComparison.Ordinal);

        // The backend has no field a UI could bind a "published" to, and this is the assertion that
        // the UI did not invent one anyway. Checked against the same list the backend enforces.
        //
        // Comments are stripped first, and not to make this pass: the rule is about what the panel
        // can put in front of a seller, and the comment stating the rule necessarily contains the
        // word it forbids. What is left after stripping is the code and every string it can render.
        var amazonJs = WithoutComments(Slice(Js, "// ── Amazon (SP-API sandbox)", "function nlAddSpecificRow("));
        Assert.False(string.IsNullOrWhiteSpace(amazonJs), "the Amazon panel is no longer in app.js");

        foreach (var forbidden in AmazonSubmissionWords.ForbiddenWords)
            Assert.DoesNotContain(forbidden, amazonJs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_submitted_listing_is_never_dressed_as_a_success()
    {
        // "success" is the class the eBay publish uses when a listing is genuinely live. A queued
        // Amazon submission is not that, and must not borrow the styling that says it is.
        Assert.Contains("el.className = 'nl-result-msg amz-result ' + (submitted ? 'pending' : 'error');",
                        Js, StringComparison.Ordinal);
        Assert.Contains(".nl-result-msg.pending", Css, StringComparison.Ordinal);

        // The submission result is a paragraph, not a one-line status, and .nl-result-msg is a
        // centred flex ROW — every child becomes a column in it and a sentence turns into a stack
        // of two-word lines, tall enough to push the panel above it off the screen.
        Assert.Contains(".nl-result-msg.amz-result", Css, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column;", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_only_route_from_pending_to_an_answer_is_asking_Amazon()
    {
        // Amazon validates afterwards, on its own schedule, with the reason attached to the SKU
        // rather than returned to the caller. Without this button the panel could never learn that
        // a submission it reported as taken was refused ten minutes later.
        Assert.Contains("Ask Amazon what became of it", Js, StringComparison.Ordinal);
        Assert.Contains("function nlAmzCheckState(", Js, StringComparison.Ordinal);

        // And when it does answer, "buyable" is quoted as Amazon's word, not asserted as the app's.
        Assert.Contains("data.amazonSaysBuyable === true", Js, StringComparison.Ordinal);
        Assert.Contains("Amazon’s own status for SKU", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_banner_reads_the_environment_rather_than_asserting_the_happy_one()
    {
        // A UI that says "sandbox" out of habit is a UI that will say it on the day somebody points
        // the build at a real seller account — which is the one day it matters.
        Assert.Contains("if (data.sandbox === false)", Js, StringComparison.Ordinal);
        Assert.Contains("PRODUCTION — WILL NOT SEND", Js, StringComparison.Ordinal);
        Assert.Contains(".amz-env.is-production", Css, StringComparison.Ordinal);

        // And the build genuinely does refuse, so the banner is reporting a fact rather than a hope.
        var refusal = AmazonSubmitGuard.Check(new AmazonOptions { Sandbox = false });
        Assert.NotNull(refusal);
        Assert.Equal("production_refused", refusal!.Code);
    }

    [Fact]
    public void A_stock_level_nobody_stated_is_not_invented_as_one()
    {
        // AmazonProductSubmitRequest.Quantity is int? precisely because 0 is a real answer meaning
        // out of stock, so a plain int cannot tell it from "nobody said". A `|| 1` in the browser
        // would undo that on the way in and publish a stock level the app made up.
        Assert.Contains("function nlAmzQuantity(", Js, StringComparison.Ordinal);
        Assert.Contains("quantity: nlAmzQuantity(),", Js, StringComparison.Ordinal);

        // Scoped to the Amazon region: buildNlPayload has the same `|| 1` for eBay and is right to,
        // because eBay's AddFixedPriceItem has no way to express "unstated". Asserting against the
        // whole file would be asserting about the eBay path, which this change does not touch.
        var amazonJs = Slice(Js, "// ── Amazon (SP-API sandbox)", "function nlAddSpecificRow(");
        Assert.DoesNotContain("|| 1,", amazonJs, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SKU_is_asked_for_and_never_generated()
    {
        // A submission replaces whatever is already at a SKU, so inventing one is inventing which
        // of the seller's listings to overwrite.
        Assert.Contains("id=\"nl-amz-sku\"", Html, StringComparison.Ordinal);
        Assert.Contains("This app will not invent one", Js, StringComparison.Ordinal);
    }

    // ── The eBay path is untouched ───────────────────────────────────────────

    [Fact]
    public void Choosing_Amazon_swaps_the_panel_and_leaves_the_eBay_path_alone()
    {
        // One draft, two destinations. Nothing about the form, the photos or the eBay publish
        // changes — only which panel and which send button are on screen.
        Assert.Contains("$('nl-btn-publish')?.classList.toggle('hidden', amazon);", Js, StringComparison.Ordinal);
        Assert.Contains("$('nl-btn-amz-submit')?.classList.toggle('hidden', !amazon);", Js, StringComparison.Ordinal);

        // The eBay publish still runs its own gate against its own state.
        Assert.Contains("if (mode === 'publish' && nlBlockersStopPublish()) return;", Js, StringComparison.Ordinal);
        Assert.Contains("const endpoint = mode === 'publish' ? '/api/listing/publish' : '/api/listing/post';",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_draft_resets_both_panels_together()
    {
        // Two panels describing the same draft must never disagree about which draft that is.
        Assert.Contains("nlResetAmazon();", Js, StringComparison.Ordinal);
        Assert.Contains("function nlResetAmazon(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changed_assets_are_stamped_past_where_they_were()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 151);
        AssetStamp.AtLeast(Html, "style.css?v=", 131);
    }

    /// <summary>
    /// The same JavaScript with its <c>//</c> comments removed — the part a seller can be shown.
    /// </summary>
    /// <remarks>
    /// Crude on purpose: it drops any line from its first <c>//</c>, which would also cut a URL
    /// written inline. That is the safe direction. Cutting too much can only make an assertion
    /// about what is NOT said weaker in a way that fails loudly elsewhere; leaving a comment in
    /// makes it fail for the wrong reason.
    /// </remarks>
    private static string WithoutComments(string js) =>
        string.Join('\n', js.Split('\n').Select(line =>
        {
            var at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }));

    /// <summary>The text between two markers, for asserting about one region rather than the file.</summary>
    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
