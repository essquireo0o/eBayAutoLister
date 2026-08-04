using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Price Position screen is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing
/// in C# notices when the sidebar button stops opening anything, a rival's eBay title lands in
/// innerHTML unescaped, or the "ask this instead" box starts appearing on the rows where that price
/// is a loss. These lock the wiring that
/// <see cref="ING_eBay_AutoLister.Services.PricePositionAnalyzer"/> is useless without.
///
/// They also lock four decisions rather than plumbing: the card leads with the blocker rather than
/// the rank, the price box appears only where moving is the recommendation, the server alone decides
/// what the blocker is, and the shelf itself is on the card rather than behind a link. Each is the
/// sort of thing a later tidy-up reverses without knowing why it was that way.
/// </summary>
public class PricePositionBoardAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void The_screen_is_reachable_from_the_sidebar_and_opens_as_a_workspace_tab()
    {
        Assert.Contains("data-page=\"position\"", Html, StringComparison.Ordinal);
        Assert.Contains("<section id=\"position-section\" class=\"opportunity-overlay hidden\">", Html, StringComparison.Ordinal);

        Assert.Matches(@"position:\s*\{\s*section:\s*'position-section',\s*open:\s*showPricePositionSection", Js);

        // Hidden with every other overlay, or opening a different screen leaves this one underneath.
        var overlays = Regex.Match(Js, @"const OVERLAY_SECTIONS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(overlays.Success, "OVERLAY_SECTIONS is no longer a single array literal");
        Assert.Contains("'position-section'", overlays.Groups[1].Value, StringComparison.Ordinal);

        Assert.Contains("bindPricePosition();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void It_sits_above_the_three_screens_that_spend_money_on_the_answer()
    {
        // Offers to Watchers gives away margin, Rescue Aging Stock ladders a markdown, and the Ad
        // Rate Advisor pays eBay for placement. All three are the wrong move on a listing that is
        // simply 40% over the front of its shelf, so the diagnosis goes above the treatments.
        var grow = Section(Html,
            "<p class=\"nav-group-label\">Grow</p>",
            "<p class=\"nav-group-label\">Account</p>");

        var position = grow.IndexOf("data-page=\"position\"", StringComparison.Ordinal);
        var offers = grow.IndexOf("data-page=\"offers\"", StringComparison.Ordinal);
        var rescue = grow.IndexOf("data-page=\"rescue\"", StringComparison.Ordinal);
        var promoted = grow.IndexOf("data-page=\"promoted\"", StringComparison.Ordinal);

        Assert.True(position > 0, "Price Position is no longer in the Grow group");
        Assert.True(position < offers && position < rescue && position < promoted,
            "the diagnosis must sit above the three screens that act on it");
    }

    [Fact]
    public void The_hero_is_the_sellers_own_capital_sitting_behind_the_shelf()
    {
        // Not "average premium", which is a statistic nobody can spend. The figure is the asking
        // value of listings a buyer reaches last.
        var hero = Section(Html, "<div class=\"er-hero pp-hero\">", "id=\"pp-notice\"");

        Assert.Contains("Listed behind cheaper copies of itself", hero, StringComparison.Ordinal);
        Assert.Contains("id=\"pp-hero-figure\"", hero, StringComparison.Ordinal);
        Assert.Contains("capitalBehindTheShelf", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_take_home_figure_shows_a_dash_rather_than_zero_when_nothing_could_be_costed()
    {
        // $0 there reads as "there is nothing left in these listings", which is the opposite of
        // what is known — most sellers record no cost basis at all.
        Assert.Contains("s.profitStillOnTheTable != null ? money(s.profitStillOnTheTable) : '—'", Js, StringComparison.Ordinal);
        // And the rows it quietly excludes are named on the label rather than left out silently.
        Assert.Contains("pricedOutWithoutCost", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_leads_with_the_blocker_rather_than_the_rank()
    {
        // "7th of 9" is a fact. "Four cheaper listings are seen before yours" is the same fact with
        // the reason it has not sold attached, and the seller is here to decide what to do next.
        // The left edge carries it too, so a price problem and a visibility problem never look the
        // same at a glance.
        Assert.Contains("pp-card-${esc(row.blocker)}", Js, StringComparison.Ordinal);
        Assert.Matches(@"\.pp-card-supply\s*\{[^}]*border-left-color:\s*var\(--danger\)", Css);
        Assert.Matches(@"\.pp-card-visibility\s*\{[^}]*border-left-color:\s*var\(--gold\)", Css);
        Assert.Matches(@"\.pp-card-price\s*\{[^}]*border-left-color:\s*var\(--warning\)", Css);
    }

    [Fact]
    public void A_row_the_board_could_not_place_never_looks_like_one_that_passed()
    {
        // "Too few rivals", "nothing comparable" and "the search failed" all arrive with no
        // blocker, exactly like a listing that is genuinely fine. Given the same green edge, three
        // absences of an answer read as three passes.
        Assert.Contains("row.rank == null ? ' pp-card-unjudged' : ''", Js, StringComparison.Ordinal);
        Assert.Matches(@"\.pp-card-unjudged\s*\{[^}]*border-left-color:\s*var\(--line-soft\)", Css);

        // And the badge on a shelf that was searched does not claim the shelf was empty.
        Assert.Contains("row.rivalsFound > 0", Card(), StringComparison.Ordinal);
        Assert.Contains("Nothing comparable", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_never_decides_what_the_blocker_is()
    {
        // Two places deciding the same thing drift, and here the drift is a price cut recommended
        // on a listing the server had already worked out nobody can see. Every branch on this card
        // reads row.blocker as it arrived; nothing here assigns one, and nothing re-derives it from
        // views, rank or premium.
        var card = Card();

        Assert.Contains("row.blocker === 'visibility'", card, StringComparison.Ordinal);
        Assert.Contains("row.blocker === 'price'", card, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"blocker\s*=[^=]", card);
        Assert.DoesNotMatch(@"blocker\s*=\s*(row\.viewCount|row\.rank|row\.premiumPercent)", Js);
    }

    [Fact]
    public void The_price_to_type_appears_only_where_moving_to_it_is_the_recommendation()
    {
        // Never on a can't-win row, where that same number is a loss the seller cannot see, and
        // never on a visibility row, where cutting fixes nothing at all. The condition is the
        // server's blocker, not a rank comparison.
        Assert.Contains("const offerPrice = row.blocker === 'price' && row.itemPriceToLead != null;", Js, StringComparison.Ordinal);
        Assert.Contains("const priceBox = offerPrice ?", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_price_offered_is_the_asking_price_not_the_delivered_one()
    {
        // The board compares delivered prices and the seller types an asking price. Handing them
        // the delivered figure would put them over the shelf they were just told to lead.
        Assert.Contains("row.itemPriceToLead", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("pp-target-price\">${esc(moneyExact(row.priceToLead))}", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_left_after_the_cut_is_rendered_next_to_the_cut()
    {
        // A price to move to, with no take-home beside it, is a race to the bottom with a button on
        // it. The floor sits there too, because it is the number that says how far down is too far.
        var card = Section(Js, "const priceBox = offerPrice ?", "const rivals =");

        Assert.Contains("netProfitAtLeadPrice", card, StringComparison.Ordinal);
        Assert.Contains("still yours", card, StringComparison.Ordinal);
        Assert.Contains("floorPrice", card, StringComparison.Ordinal);
        Assert.Contains("no cost recorded", card, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shelf_is_on_the_card_rather_than_behind_a_link()
    {
        // "You are 7th" is a claim about the seller's own money, and they should be able to check
        // it without leaving the screen. The listings left OUT of the ranking are summarised too —
        // a board that silently drops half a shelf is a board whose position nobody should believe.
        Assert.Contains("class=\"pp-rivals\"", Js, StringComparison.Ordinal);
        Assert.Contains("function pricePositionSkipSummary(rivals)", Js, StringComparison.Ordinal);
        Assert.Contains("left out of the ranking", Js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\.pp-rivals\s*\{[^}]*display:\s*none", Css);
    }

    [Fact]
    public void Every_row_can_be_checked_against_ebays_own_cheapest_first_sort()
    {
        // _sop=15 is price + shipping, lowest first — the same order this board ranks on. Any other
        // sort would open a shelf that does not match the one the card just described.
        Assert.Contains("_sop=15", Js, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\" rel=\"noopener\"", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measurement_that_was_never_taken_is_not_rendered_as_zero()
    {
        // eBay omits view counts on some accounts. "Seen 0 times" on every card would be a whole
        // board of listings falsely reported as invisible.
        Assert.Contains("if (row.viewsKnown) stats.push(['Seen'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_blocker_is_handed_to_the_screen_that_fixes_it()
    {
        // A visibility row goes to Listing Copilot, because the fix is words. A priced-out row with
        // watchers is offered the offers board, because an offer reaches exactly the people who
        // already stopped at the number and costs nothing if nobody takes it.
        Assert.Contains("pp-to-copilot", Js, StringComparison.Ordinal);
        Assert.Contains("navigateTo('copilot');", Js, StringComparison.Ordinal);
        Assert.Contains("row.blocker === 'price' && row.watchCount > 0", Js, StringComparison.Ordinal);
        Assert.Contains("navigateTo('offers');", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_filters_hide_rows_and_never_re_rank_them()
    {
        // The board's own order is what the money says. A screen that re-sorts on click invites the
        // seller to work through the shelf in an order nothing recommended.
        Assert.Contains("data-filter=\"all\"", Html, StringComparison.Ordinal);
        Assert.Contains("data-filter=\"price\"", Html, StringComparison.Ordinal);
        Assert.Contains("data-filter=\"visibility\"", Html, StringComparison.Ordinal);
        Assert.Contains("function filteredPricePositionRows(rows)", Js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"function filteredPricePositionRows\(rows\)\s*\{[^}]*\.sort\(", Js);
    }

    [Fact]
    public void The_cautions_on_a_card_are_never_folded_away()
    {
        // The seller reading this card is about to change a live price on the strength of it.
        Assert.Contains("pp-cautions", Js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\.pp-cautions\s*\{[^}]*display:\s*none", Css);
    }

    [Fact]
    public void The_board_does_not_re_read_ebay_every_time_the_tab_is_opened()
    {
        // One load is the seller's live listings plus one eBay search per product on them. Coming
        // back to a tab is not asking for a dozen searches — the Refresh button is.
        Assert.DoesNotMatch(@"position:\s*\{[^}]*refresh:", Js);
        Assert.Contains("id=\"pp-refresh\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('pp-refresh', 'click', () => loadPricePosition());", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_says_out_loud_that_it_changes_nothing()
    {
        // It sits one click from three screens that DO write to eBay, and it recommends prices.
        // A seller has to be able to tell at a glance which of those this is.
        Assert.Contains("Nothing on this screen changes a live listing", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"pp-honesty\"", Html, StringComparison.Ordinal);
    }

    [Theory]
    // Every string on this screen that arrives from the server and lands in innerHTML. Rival titles
    // and seller names are typed by strangers on eBay, which makes them the least trusted text in
    // the whole app.
    [InlineData("row.title")]
    [InlineData("row.headline")]
    [InlineData("row.blocker")]
    [InlineData("v.title")]
    [InlineData("v.url")]
    [InlineData("v.sellerUsername")]
    [InlineData("row.thumbnailUrl")]
    [InlineData("row.listingUrl")]
    public void Server_supplied_text_never_reaches_innerHTML_unescaped(string field)
    {
        // Scoped to this card, because the same field name on another screen is another screen's
        // problem — and some of these are legitimately interpolated into plain text elsewhere.
        var card = Card();

        Assert.DoesNotContain("${" + field + "}", card, StringComparison.Ordinal);
        Assert.Contains("${esc(" + field, card, StringComparison.Ordinal);
    }

    /// <summary>Everything this screen renders into innerHTML, from the card to the skip summary.</summary>
    private static string Card() => Section(Js, "function pricePositionCard(row)", "// ── Deal Pipeline");

    [Fact]
    public void The_cached_assets_are_versioned_past_the_build_that_shipped_without_this_screen()
    {
        // wwwroot files are embedded resources served with far-future caching. A seller who updates
        // and gets yesterday's app.js gets a sidebar button that opens nothing at all.
        Assert.True(AssetVersion("app.js") >= 107, "app.js changed, so index.html's ?v= must move past 106");
        Assert.True(AssetVersion("style.css") >= 95, "style.css changed, so index.html's ?v= must move past 94");
    }

    private static int AssetVersion(string file)
    {
        var match = Regex.Match(Html, Regex.Escape(file) + @"\?v=(\d+)");
        Assert.True(match.Success, $"index.html no longer versions {file}");
        return int.Parse(match.Groups[1].Value);
    }

    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
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
