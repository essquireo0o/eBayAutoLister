namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The photo check reaches the live card through five links and four of them are outside C#: the
/// reader is registered, the endpoint is mapped, the browser asks for it, the browser renders the
/// answer, and the stylesheet tells the four outcomes apart. Break any one and the button quietly
/// stops saying anything, which looks exactly like a photo that agreed.
/// </summary>
/// <remarks>
/// Four of these are decisions rather than plumbing, and all four are the sort of thing a later tidy
/// -up undoes without reading why: the look prices nothing, it never runs by itself, it never writes
/// into the item box on its own, and the sold-comps path this whole screen stands on is untouched.
/// </remarks>
public class WhatsNotPhotoCheckAssetTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Reader = ReadSource("Services/LotPhotoReader.cs");
    private static readonly string Judge = ReadSource("Services/LotPhotoJudge.cs");

    // ── The endpoint ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_reader_is_registered_and_the_endpoint_is_mapped()
    {
        Assert.Contains("builder.Services.AddSingleton<LotPhotoReader>();", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/photo\"", Program, StringComparison.Ordinal);
        Assert.Contains("await reader.FetchAsync(req.ImageUrl, ct)", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole safety property. A photograph is evidence about IDENTITY; the ceiling is made of
    /// sales that happened. An endpoint that priced what it had just looked at would put a second
    /// opinion about money on the one screen where there is no time to notice there are two — and it
    /// would be the weaker of the two.
    /// </summary>
    [Fact]
    public void Looking_at_the_photo_prices_nothing()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/photo\"", "\n});");

        foreach (var pricing in new[] { "AnalyzeProductAsync", "advisor.Build", "board.Hold", "MaxBid", "ResalePricing" })
            Assert.DoesNotContain(pricing, endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_look_goes_through_the_same_identification_the_snap_photo_path_uses()
    {
        // A second prompt for "what is this" would be a second opinion about identity, and this
        // screen has no time for two.
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/photo\"", "\n});");

        Assert.Contains("claude.IdentifyItemAsync(base64!, mediaType!, ct)", endpoint, StringComparison.Ordinal);
        Assert.Contains("LotPhotoJudge.Judge(req.Title, identity, url)", endpoint, StringComparison.Ordinal);
        // And the snap path it borrows is still there and still its own.
        Assert.Contains("app.MapPost(\"/api/snap\"", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_that_fails_comes_back_as_a_sentence_rather_than_as_a_failed_request()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/photo\"", "\n});");

        // One render path on the screen: every outcome is a look with a status on it.
        Assert.Contains("LotPhotoJudge.Failed(failure)", endpoint, StringComparison.Ordinal);
        Assert.Contains("FailureTranslator.Translate(ex, FailureDomain.Ai)", endpoint, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) { throw; }", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disagreement_is_logged_loudly_enough_to_find_afterwards()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/photo\"", "\n});");

        Assert.Contains("log.Add(", endpoint, StringComparison.Ordinal);
        Assert.Contains("LotPhotoAgreement.Differs ? \"Warning\"", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trial_guard_runs_before_anything_is_fetched_or_looked_at()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/photo\"", "\n});");

        Assert.True(endpoint.IndexOf("TrialGuard(store, license)", StringComparison.Ordinal)
                  < endpoint.IndexOf("reader.FetchAsync", StringComparison.Ordinal),
            "a paid call belongs behind the guard, not in front of it");
    }

    // ── What the reader will and won't fetch ─────────────────────────────────────────────────

    [Fact]
    public void The_address_goes_through_the_same_guard_the_embed_check_and_the_show_read_use()
    {
        Assert.Contains("FrameEmbedPolicy.Normalize(rawUrl)", Reader, StringComparison.Ordinal);
        Assert.Contains("FrameEmbedPolicy.Validate(url)", Reader, StringComparison.Ordinal);
        // And then narrower than either of them: https only, because the bytes are sent on.
        Assert.Contains("StartsWith(\"https://\"", Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_four_image_types_the_look_can_be_handed_are_fetched()
    {
        Assert.Contains("AllowedTypes = [\"image/jpeg\", \"image/png\", \"image/gif\", \"image/webp\"]",
            Reader, StringComparison.Ordinal);
        Assert.Contains("!AllowedTypes.Contains(mediaType)", Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void The_photo_is_read_through_the_apps_own_bounded_fetch_rather_than_a_second_byte_loop()
    {
        Assert.Contains("PublicFeedHttp.ApplyBrowserHeaders(http)", Reader, StringComparison.Ordinal);
        Assert.Contains("PublicFeedHttp.ReadBoundedBytesAsync(response, MaxImageBytes", Reader, StringComparison.Ordinal);

        // The one byte loop in the app still has exactly one home, and the text path is now that
        // loop decoded rather than a copy of it.
        var feed = ReadSource("Services/PublicFeedHttp.cs");
        Assert.Contains("var bytes = await ReadBoundedBytesAsync(response, maxBytes, ct);", feed, StringComparison.Ordinal);
        Assert.Contains("var body = await ReadBoundedAsync(response, maxBytes, budget);", feed, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(feed, "while ((read = await stream.ReadAsync(buffer, ct)) > 0)"));
    }

    [Fact]
    public void The_fetch_gives_up_inside_the_time_a_lot_lasts()
    {
        Assert.Contains("public const int FetchTimeoutSeconds = 6;", Reader, StringComparison.Ordinal);
        Assert.Contains("deadline.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds))", Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_that_fails_costs_the_check_and_never_the_screen()
    {
        // Every refusal carries a next move, and the next move is always the typed box that has
        // worked since the first version of this screen.
        Assert.Contains("catch (Exception ex)", Reader, StringComparison.Ordinal);
        Assert.Contains("the ceiling never needed the photo", Reader, StringComparison.Ordinal);
    }

    // ── The decision ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Written next to the rule rather than in a commit message, because "just flag every mismatch"
    /// is the obvious simplification and it is the one that ruins the feature: a panel that is wrong
    /// about a disagreement costs the seller the lot AND the panel.
    /// </summary>
    [Fact]
    public void Why_the_check_refuses_to_cry_wolf_is_written_down_in_the_code()
    {
        Assert.Contains("crying wolf", Judge, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot contradict", Judge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_search_bar_and_the_cleaning_are_the_lot_lists_own()
    {
        // A name off a photo and the same name typed by hand reach the comp lookup identically, or
        // the app has two ways of asking eBay the same question.
        Assert.Contains("LiveLotList.Clean(", Judge, StringComparison.Ordinal);
        Assert.Contains("LiveLotList.MinTitleLength", Judge, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_button_and_its_panel_sit_with_the_read_that_supplies_the_photo()
    {
        Assert.Contains("id=\"wn-photo-btn\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-photo-out\"", Html, StringComparison.Ordinal);

        Assert.True(Html.IndexOf("id=\"wn-photo-btn\"", StringComparison.Ordinal)
                  < Html.IndexOf("id=\"wn-item\"", StringComparison.Ordinal),
            "the check belongs above the box whose name it is checking");
        Assert.True(Html.IndexOf("id=\"wn-read-out\"", StringComparison.Ordinal)
                  < Html.IndexOf("id=\"wn-photo-out\"", StringComparison.Ordinal),
            "the look answers the read, so it sits under it");
    }

    /// <summary>
    /// #wn-say is the only live region on this screen. A second one during a live sale is a screen
    /// reader given two competing announcements, which is a screen reader saying nothing usable.
    /// </summary>
    [Fact]
    public void The_look_panel_is_not_a_second_live_region()
    {
        var panel = Between(Html, "<div id=\"wn-photo-out\"", ">");

        Assert.DoesNotContain("aria-live", panel, StringComparison.Ordinal);
        Assert.Contains("wnSayLine([look.headline, look.hint]", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_button_says_what_it_is_doing_while_it_is_doing_it()
    {
        var check = Between(Js, "async function wnCheckPhoto()", "function wnUsePhotoTitle(title)");

        Assert.Contains("btn.setAttribute('aria-busy', 'true')", check, StringComparison.Ordinal);
        Assert.Contains("btn.removeAttribute('aria-busy')", check, StringComparison.Ordinal);
        // And comes back whatever happened — a button left disabled by a failed look is a feature
        // that works once.
        Assert.Contains("} finally {", check, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seller has not read the show yet and presses the button anyway. A control that does
    /// nothing when pressed teaches them it is broken; this one says where the photo comes from and
    /// puts the cursor where the next thing goes.
    /// </summary>
    [Fact]
    public void Pressing_it_with_no_photo_yet_explains_rather_than_doing_nothing()
    {
        var check = Between(Js, "async function wnCheckPhoto()", "function wnUsePhotoTitle(title)");

        Assert.Contains("if (!wnPhotoUrl)", check, StringComparison.Ordinal);
        Assert.Contains("no-photo", check, StringComparison.Ordinal);
        Assert.Contains("$('wn-read-url')?.focus()", check, StringComparison.Ordinal);
        // And no request is made for a picture nobody has.
        Assert.True(check.IndexOf("if (!wnPhotoUrl)", StringComparison.Ordinal)
                  < check.IndexOf("safePost('/api/whatsnot/photo'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stale_answer_never_paints_over_a_newer_one()
    {
        var check = Between(Js, "async function wnCheckPhoto()", "function wnUsePhotoTitle(title)");

        Assert.Contains("if (seq !== wnPhotoSeq) return;", check, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_computes_nothing_about_the_photo()
    {
        // Every sentence on the panel is the server's. A browser that assembled its own would be a
        // second account of what a picture showed, and it never saw the picture.
        var render = Between(Js, "function wnRenderPhoto(look)", "async function wnCheckPhoto()");

        Assert.Contains("esc(look.headline", render, StringComparison.Ordinal);
        Assert.Contains("esc(look.detail)", render, StringComparison.Ordinal);
        Assert.Contains("esc(look.askTheHost)", render, StringComparison.Ordinal);
        foreach (var arithmetic in new[] { "* 1.3", "/ 1.3", ".toFixed(", "reduce(" })
            Assert.DoesNotContain(arithmetic, render, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_photo_actually_said_is_on_the_screen_and_folded_away()
    {
        Assert.Contains("What the photo actually said", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-photo-ev {", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_of_the_four_outcomes_has_an_edge_of_its_own()
    {
        Assert.Contains("wn-photo-${kind}", Js, StringComparison.Ordinal);
        foreach (var rule in new[]
                 {
                     ".wn-photo-ok {", ".wn-photo-new {", ".wn-photo-bad {",
                     ".wn-photo-none {", ".wn-photo-busy {",
                 })
        {
            Assert.Contains(rule, Css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_on_this_panel_overflows_the_narrow_layout()
    {
        // The whole screen already folds at 620px; the look panel joins it rather than being the one
        // thing that pushes a stream off the side of a laptop. The WhatsNot narrow block is the last
        // in the stylesheet, so it is taken from the last one of these to the end.
        var at = Css.LastIndexOf("@media (max-width: 620px)", StringComparison.Ordinal);
        Assert.True(at > 0, "the narrow layout for this screen is gone");
        var narrow = Css[at..];

        foreach (var rule in new[] { ".wn-photo-btn,", ".wn-photo-shot {", ".wn-photo-use-btn {" })
            Assert.Contains(rule, narrow, StringComparison.Ordinal);
    }

    // ── The two things it is never allowed to do ─────────────────────────────────────────────

    /// <summary>
    /// A look at a picture costs money and a second or two. The automatic reader must not reach it —
    /// a loop that bought a vision call every twenty seconds would spend the seller's money all
    /// night for the lots they were not even watching.
    /// </summary>
    [Fact]
    public void The_automatic_reader_never_triggers_a_look()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");
        var tick = Between(Js, "function wnWatchTick()", "// ── Is that actually what it says it is?");

        Assert.DoesNotContain("wnCheckPhoto", readShow, StringComparison.Ordinal);
        Assert.DoesNotContain("wnCheckPhoto", tick, StringComparison.Ordinal);
        // It keeps the address, and nothing more.
        Assert.Contains("wnPhotoUrl = read.imageUrl || '';", readShow, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the seller typed outranks what a model saw. The only path from a photo into the item box
    /// is a press on the offer, and it goes through the same ⚡ Price it a typed name does.
    /// </summary>
    [Fact]
    public void A_better_name_is_offered_and_never_substituted()
    {
        var render = Between(Js, "function wnRenderPhoto(look)", "async function wnCheckPhoto()");
        var check = Between(Js, "async function wnCheckPhoto()", "function wnUsePhotoTitle(title)");
        var use = Between(Js, "function wnUsePhotoTitle(title)", "async function wnPriceItem()");

        Assert.DoesNotContain("setVal('wn-item'", render, StringComparison.Ordinal);
        Assert.DoesNotContain("setVal('wn-item'", check, StringComparison.Ordinal);

        Assert.Contains("data-use-photo=", render, StringComparison.Ordinal);
        Assert.Contains("setVal('wn-item', name);", use, StringComparison.Ordinal);
        Assert.Contains("wnDropToken();", use, StringComparison.Ordinal);
        Assert.Contains("wnPriceItem();", use, StringComparison.Ordinal);
        Assert.Contains("on('wn-photo-btn', 'click', wnCheckPhoto);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_is_dropped_when_the_show_moves_to_a_different_lot()
    {
        // A look left on screen beside a lot that has gone is a claim about something that is no
        // longer being sold.
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        Assert.Contains("wnPhotoUrl = '';", readShow, StringComparison.Ordinal);
        Assert.Contains("if (movedOn) wnRenderPhoto(null);", readShow, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_the_tab_drops_a_look_that_is_still_in_flight()
    {
        var close = Between(Js, "function closeWhatsNotSection()", "// ── WhatsNot: the live-auction arbitrage card");

        Assert.Contains("wnPhotoSeq++;", close, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_and_the_script_were_both_re_stamped()
    {
        // wwwroot is embedded, and a cached app.js against a new server renders a button that does
        // nothing at all.
        Assert.True(AssetVersion(Html, "app.js?v=") >= 126, "app.js changed, so its stamp must move");
        Assert.True(AssetVersion(Html, "style.css?v=") >= 109, "style.css changed, so its stamp must move");
    }

    // ── Additive, as every WhatsNot session has been ─────────────────────────────────────────

    [Fact]
    public void Sold_comps_and_every_endpoint_this_screen_already_had_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps",
                     "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
                     "/api/whatsnot/embed-check", "/api/whatsnot/read",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_typed_path_still_works_on_its_own()
    {
        // The check is a second opinion about the name, not a step on the way to a ceiling. Somebody
        // watching a show on a platform this has never heard of still types the lot and gets the
        // same answer from the same function.
        Assert.Contains("on('wn-price', 'click', wnPriceItem);", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-item\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_show_read_still_fills_the_box_and_prices_it()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        Assert.Contains("setVal('wn-item', read.title);", readShow, StringComparison.Ordinal);
        Assert.Contains("wnPriceItem();", readShow, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static int AssetVersion(string html, string prefix)
    {
        var at = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{prefix}\" is no longer in index.html");
        var digits = new string(html[(at + prefix.Length)..].TakeWhile(char.IsDigit).ToArray());
        Assert.NotEqual("", digits);
        return int.Parse(digits);
    }

    private static int CountOf(string source, string needle)
    {
        int count = 0, at = 0;
        while ((at = source.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" is no longer in the source");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"\"{end}\" no longer follows \"{start}\"");
        return source[from..to];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
