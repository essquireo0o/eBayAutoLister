namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The one question the live card never asked — will eBay let you sell this — reaches the screen
/// through seven links, and most of them are the sort of thing a later tidy-up removes without
/// reading why: the read running against the lot's NAME, the per-unit resale price the
/// authentication thresholds are checked against, the call deferring to it before it weighs any
/// price, the warning list carrying it on a card nothing priced, the strip drawing it FIRST, the
/// spoken line, and the ranking sinking a refused lot below every other. Break any one and the
/// feature silently does nothing on every card forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// <para>
/// Three of these are decisions rather than plumbing. It is the only read on this card allowed to
/// <b>overrule the call</b>, and it does that without shading a single number — a lot eBay refuses
/// to list has no eBay resale price at any bid, so the ceiling above it is not too high, it is a
/// price for a transaction that cannot happen. The authentication leg is <b>said and never
/// charged</b>, because its cost is days and <c>LiveHoldCost</c> already prices the calendar. And
/// the vocabulary is tuned to a deliberate asymmetry: a rule that fires wrongly costs a lot somebody
/// else wins, and a rule that stays quiet costs the whole purchase.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched, and this is purely additive to it.
/// </para>
/// </remarks>
public class WhatsNotGateAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Gate = ReadSource("Services/LiveResaleGate.cs");
    private static readonly string GateModels = ReadSource("Models/LiveGateModels.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");

    // ── What it reads ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// It reads the lot's NAME, not the category the seller picked from a dropdown. A category says
    /// what the object claims to be; the name is where an auctioneer types "replica".
    /// </summary>
    [Fact]
    public void It_reads_the_name_and_not_the_category()
    {
        Assert.Contains("LiveResaleGate.Read(item,", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("ResaleCategory", Gate, StringComparison.Ordinal);
        Assert.DoesNotContain("CategoryId", Gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Authenticity Guarantee thresholds are on a SALE price, so they are checked against the
    /// card's own per-unit resale figure — the one every other block on this card is priced from.
    /// A second idea of what the thing is worth is how a strip ends up disagreeing with the ladder.
    /// </summary>
    [Fact]
    public void The_thresholds_are_checked_against_the_cards_own_per_unit_resale_price()
    {
        Assert.Contains("card.Gate = LiveResaleGate.Read(item, perUnitResale);", Advisor, StringComparison.Ordinal);

        // And on a card nothing priced, the bar is left explicitly unchecked rather than assumed.
        Assert.Contains("card.Gate = LiveResaleGate.Read(item, null);", Advisor, StringComparison.Ordinal);
        Assert.Contains("Nothing priced this one", Gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pure: no clock, no network, no state. It sits on the path a bid moving every two seconds runs
    /// down, and a card re-priced from held comps has to re-run it and get the same answer.
    /// </summary>
    [Fact]
    public void It_reaches_for_nothing_outside_itself()
    {
        foreach (var forbidden in new[] { "DateTime", "HttpClient", "async ", "await ", "static readonly List" })
            Assert.DoesNotContain(forbidden, Gate, StringComparison.Ordinal);
    }

    // ── The one thing it is allowed to do to the call ─────────────────────────────────────────

    /// <summary>
    /// The refusal is reached before every reason a ceiling cannot be trusted, because it is not a
    /// reason a ceiling cannot be trusted — it is the absence of anything for a ceiling to be a
    /// ceiling ON.
    /// </summary>
    [Fact]
    public void The_call_asks_whether_it_can_be_listed_before_it_weighs_any_price()
    {
        var judge = Advisor[Advisor.IndexOf(
            "public static (string Call, string Label, string Reason) Judge(", StringComparison.Ordinal)..];

        var gateAt = judge.IndexOf("card.Gate is { Stops: true }", StringComparison.Ordinal);
        var breakEvenAt = judge.IndexOf("card.BreakEvenBid <= 0m", StringComparison.Ordinal);

        Assert.True(gateAt > 0, "the call has to ask the gate at all");
        Assert.True(gateAt < breakEvenAt, "the gate has to be asked before the first price is weighed");
    }

    /// <summary>
    /// It overrules the call and moves no money. Nothing in the advisor feeds this read back into a
    /// ceiling, a resale price or a multiplier — the genuine article is worth what the comps say it
    /// is worth, and that figure is exactly what explains why the lot was tempting.
    /// </summary>
    [Fact]
    public void Nothing_it_finds_moves_a_price()
    {
        Assert.DoesNotContain("maxBid = card.Gate", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("card.ResalePrice = card.Gate", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("card.DaysToCash = card.Gate", Advisor, StringComparison.Ordinal);

        // And the read produces no multiplier of its own for anybody to apply.
        Assert.DoesNotContain("Discount(", Gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Multiplier", Gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The badge is its own, and deliberately not DON'T BID: that one is a judgement about a price,
    /// and this is not.
    /// </summary>
    [Fact]
    public void A_refused_lot_gets_a_badge_of_its_own()
    {
        Assert.Contains("public const string CantListLabel = \"CAN'T LIST IT\";", Advisor, StringComparison.Ordinal);
        Assert.Contains("return (LiveBidCalls.Stop, CantListLabel, gate.Reason);", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused lot sinks below every other lot in the show, including the ones nothing could
    /// price. Its call is <c>stop</c>, which without this would sort it above every unpriced lot and
    /// by its own healthy profit figure at that.
    /// </summary>
    [Fact]
    public void A_refused_lot_ranks_below_even_an_unpriceable_one()
    {
        Assert.Contains("private const int BlockedTier = -1;", Advisor, StringComparison.Ordinal);
        Assert.Contains("RankLot(card.Call, card.ProfitAtMaxBid, card.Gate.Stops)", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authentication leg costs days, and days are what <see cref="LiveHoldCost"/> already
    /// prices. It is reported and never folded into the card's own days-to-cash, which is estimated
    /// from real sell-through data — a guess added to a measurement makes the measurement part guess.
    /// </summary>
    [Fact]
    public void The_authentication_days_are_reported_and_never_added_to_the_measured_ones()
    {
        Assert.Contains("public const int AuthenticationDays = 4;", Gate, StringComparison.Ordinal);
        Assert.Contains("public int ExtraDaysToCash { get; set; }", GateModels, StringComparison.Ordinal);

        // Reported on the read, and never added to the card's own estimate by anybody.
        Assert.DoesNotContain("DaysToCash +", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("card.DaysToCash", Gate, StringComparison.Ordinal);
        Assert.DoesNotContain("card.Gate.ExtraDaysToCash", Advisor, StringComparison.Ordinal);
    }

    // ── What it refuses to claim ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The catalogue is one table, in one file, dated in its own comment. eBay moves these — the
    /// Authenticity Guarantee thresholds especially — and a rule that has drifted has to be
    /// findable by the seller who disagreed with the call.
    /// </summary>
    [Fact]
    public void The_rules_are_one_table_that_says_it_is_a_snapshot()
    {
        Assert.Contains("private static readonly Rule[] Rules =", Gate, StringComparison.Ordinal);
        Assert.Contains("August 2026", Gate, StringComparison.Ordinal);

        // Every rule carries the name and the policy the strip prints, so a call can be argued with.
        Assert.Contains("record Rule(string Name, Regex Words, string Verdict, decimal Threshold, string Policy)",
            Gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The words that fired the rule go on the read. A rule that fires wrongly is fixed by retyping
    /// the name — and the seller cannot do that if they cannot see what fired.
    /// </summary>
    [Fact]
    public void The_matched_words_are_carried_and_shown()
    {
        Assert.Contains("public string Matched { get; set; }", GateModels, StringComparison.Ordinal);
        Assert.Contains("read.Matched = hit.Value.Trim();", Gate, StringComparison.Ordinal);
        Assert.Contains("gt.matched ?", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two words that would fire on ordinary eBay stock are absent by name, with the reason
    /// written beside them. "Fake fur" and "Lululemon dupe" are not counterfeits, and a rule that
    /// stopped a lot of artificial flowers is a rule the seller learns to ignore — which is the one
    /// failure a stop-the-call read cannot survive.
    /// </summary>
    [Fact]
    public void The_words_that_would_cry_wolf_are_left_out_on_purpose()
    {
        var replicas = Gate[Gate.IndexOf("\"Replicas and counterfeits\"", StringComparison.Ordinal)..];
        var rule = replicas[..replicas.IndexOf("new(\"Ammunition", StringComparison.Ordinal)];

        Assert.DoesNotContain("|fake|", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("|dupe", rule, StringComparison.Ordinal);
        Assert.Contains("fake fur", rule, StringComparison.OrdinalIgnoreCase);
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drawn FIRST, directly under the badge, above every number whose meaning depends on the
    /// answer. Every figure on this card is the price of an eBay listing and none of them is a price
    /// of anything if the listing is not allowed to exist.
    /// </summary>
    [Fact]
    public void The_strip_is_the_first_thing_under_the_badge()
    {
        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("</div>\n      ${gateStrip}\n      ${nextStrip}\n", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// The browser decides nothing. Every word — the headline, the note and the two-word tag — is
    /// the server's, so there is one place a state's wording lives.
    /// </summary>
    [Fact]
    public void The_browser_paints_the_verdict_and_never_reaches_one()
    {
        var at = Js.IndexOf("const gateStrip = gt.headline", StringComparison.Ordinal);
        Assert.True(at > 0, "the strip is built from the server's own headline");
        var block = Js[at..(at + 900)];

        Assert.Contains("esc(gt.headline)", block, StringComparison.Ordinal);
        Assert.Contains("esc(gt.note)", block, StringComparison.Ordinal);
        Assert.Contains("esc(gt.tag)", block, StringComparison.Ordinal);

        // No vocabulary, no thresholds and no verdict mapping in the browser.
        Assert.DoesNotContain("replica", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blocked", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test(", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every state the read can produce has an edge and a headline colour to land on. A verdict with
    /// no rule for it draws the neutral one, which is the state that means "nothing to worry about".
    /// </summary>
    [Fact]
    public void Every_state_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var state in new[] { "blocked", "restricted", "authenticated", "clear" })
            Assert.Contains($".wn-gate-{state} {{", Css, StringComparison.Ordinal);

        // The three states that carry a tag colour it. The clear one has no tag to colour, on
        // purpose — a badge reading OK on every ordinary card is how a seller learns to stop reading.
        foreach (var state in new[] { "blocked", "restricted", "authenticated" })
            Assert.Contains($".wn-gate-{state} .wn-gate-tag {{", Css, StringComparison.Ordinal);

        foreach (var part in new[]
        {
            ".wn-gate {", ".wn-gate-line {", ".wn-gate-label {", ".wn-gate-headline {",
            ".wn-gate-tag {", ".wn-gate-src {", ".wn-gate-note {",
        })
        {
            Assert.Contains(part, Css, StringComparison.Ordinal);
        }

        // And it folds at the narrow width like every other strip on this card.
        Assert.Contains(".wn-gate-line {\n    flex-direction: column;", Css.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
    }

    /// <summary>The browser was told to reload both assets.</summary>
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 145);
        AssetStamp.AtLeast(Html, "style.css?v=", 128);
    }

    // ── The line ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Said third — after the badge and the unit count that badge is a price of, and before every
    /// other figure whose meaning depends on it. Two states speak and the restricted one does not:
    /// a thing to go and check belongs where checking happens, not under a countdown.
    /// </summary>
    [Fact]
    public void The_line_says_it_before_every_figure_it_changes_the_meaning_of()
    {
        Assert.Contains("Join(Headline(card), HowMany(card), WhetherEbayTakesIt(card)",
            Speech, StringComparison.Ordinal);
        Assert.Contains("if (gate.Stops) return", Speech, StringComparison.Ordinal);

        // And on a refused lot the line stops there. Every clause after it is a ceiling, a room
        // figure or a resale price — all of them prices of the ALLOWED article — and the last thing
        // heard under a countdown must not be permission-shaped.
        Assert.Contains(
            "if (card.Gate is { Stops: true })\n            return Join(Headline(card), HowMany(card), WhetherEbayTakesIt(card));",
            Speech.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Verdict: LiveGateVerdicts.Authenticated, OverThreshold: true",
            Speech, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveGateVerdicts.Restricted", Speech, StringComparison.Ordinal);
    }

    // ── The warning list ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// It reaches the warning list first of all, and on the one path where no other warning can:
    /// a card nothing priced. "No eBay sold history matched" is exactly the sentence a seller reads
    /// as "the app has no opinion, use your own".
    /// </summary>
    [Fact]
    public void The_warning_is_said_even_on_a_card_nothing_priced()
    {
        Assert.Contains("if (card.Gate is { Warning.Length: > 0 } gate) warnings.Add(gate.Warning);",
            Advisor, StringComparison.Ordinal);
        Assert.Contains("if (card.Gate.Warning.Length > 0) card.Warnings.Add(card.Gate.Warning);",
            Advisor, StringComparison.Ordinal);

        // Ahead of the NoData exit, or it would never be reached on the path that needs it most.
        var warnings = Advisor[Advisor.IndexOf(
            "public static List<string> Warnings(", StringComparison.Ordinal)..];
        Assert.True(
            warnings.IndexOf("card.Gate is { Warning.Length: > 0 }", StringComparison.Ordinal)
            < warnings.IndexOf("card.Call == LiveBidCalls.NoData", StringComparison.Ordinal));
    }

    // ── The log ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's own log prints the verdict, the rule and the words that fired it, so the first
    /// real "the app said CAN'T LIST IT on a $400 bag" can be checked — and a rule that has drifted
    /// out of date can be found rather than argued with.
    /// </summary>
    [Fact]
    public void The_action_log_prints_the_rule_and_what_matched()
    {
        Assert.Contains("$\"gate {card.Gate.Verdict}\"", Program, StringComparison.Ordinal);
        Assert.Contains("card.Gate.RuleName", Program, StringComparison.Ordinal);
        Assert.Contains("card.Gate.Matched", Program, StringComparison.Ordinal);
        Assert.Contains("card.Gate.ThresholdPrice", Program, StringComparison.Ordinal);
    }

    // ── The card carries it ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Present on every card, never null. A block that only appeared once a policy was tripped would
    /// be a block whose silence means both "eBay is fine with this" and "nothing ever looked", and
    /// the second is the expensive reading.
    /// </summary>
    [Fact]
    public void The_card_always_carries_the_read()
    {
        Assert.Contains("public LiveGateRead Gate { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    // ── Sold comps are untouched ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The constraint every WhatsNot session has worked under. This screen stands on the sold-comps
    /// path, and every endpoint it stands on is still registered.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched_and_still_registered()
    {
        foreach (var route in new[]
        {
            "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
            "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
            "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
        })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
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
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
