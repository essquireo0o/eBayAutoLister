using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A won lot that never becomes a listing is a loss with a receipt. This is the step that turns the
// buy sheet's projected resale into money actually coming back, and what is pinned here is that it
// stays honest while doing it:
//
//   · the ask is the CARD's resale price, rounded by the app's own charm rule — never a new opinion
//     about what the thing is worth, and never rounded below what the lot cost;
//   · a lot nothing could price gets a draft with NO price rather than an invented one;
//   · what the draft cannot know — the condition, the photos, the eBay category — is left blank and
//     said out loud, instead of being guessed into a claim made to a buyer;
//   · the deal card carries the cash in the split CostBasisStore wants, keyed on the row, so
//     listing the same lot twice cannot report the same capital twice;
//   · the sheet remembers which rows became drafts, because the draft is a file this app does not
//     watch and now is the only moment that link is knowable.
public class WonLotListingTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 23, 30, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    // ── The asking price is the card's own resale figure ──────────────────────

    /// <summary>
    /// The whole legitimacy of the button. The draft asks what the sold comps said the item resells
    /// for — the same number the bid was made against — put through the same rounding the repricer
    /// and the relister use. A second pricing rule here would mean the app bid on one valuation and
    /// listed on another.
    /// </summary>
    [Fact]
    public void The_ask_is_the_comps_resale_price_charm_rounded_by_the_apps_own_rule()
    {
        var lot = Won(landed: 120m, resale: 342.40m);

        var ask = WonLotListing.AskingPrice(lot);

        Assert.Equal(InventoryHealthAnalyzer.Charm(342.40m, floorPrice: 120m), ask);
        Assert.Equal(341.99m, ask);
    }

    /// <summary>
    /// Charm rounds DOWN, and the one line it may not cross is what the lot cost. A listing that
    /// asks less than the item cost is not a price, it is a donation.
    /// </summary>
    [Fact]
    public void Rounding_never_takes_the_ask_below_what_the_lot_cost()
    {
        var lot = Won(landed: 200.50m, resale: 200.60m);

        var ask = WonLotListing.AskingPrice(lot);

        Assert.Equal(200.60m, ask);
        Assert.True(ask >= lot.LandedCost, "the ask was rounded under the landed cost");
    }

    [Fact]
    public void An_already_charmed_price_is_left_alone()
        => Assert.Equal(299.99m, WonLotListing.AskingPrice(Won(landed: 100m, resale: 299.99m)));

    /// <summary>
    /// Nothing priced it, so nothing here prices it. A number invented at this point would be the
    /// only figure on the whole WhatsNot screen with no sold history under it — and it would be the
    /// one a buyer pays.
    /// </summary>
    [Fact]
    public void A_lot_nothing_could_price_gets_a_draft_with_no_price_and_is_told_so()
    {
        var lot = Won(landed: 90m, resale: null, priced: false);

        Assert.Null(WonLotListing.AskingPrice(lot));
        Assert.Equal(0m, WonLotListing.Draft(lot).Data.Price);
        Assert.Contains(WonLotListing.Notes(lot),
            n => n.Contains("no price", StringComparison.OrdinalIgnoreCase));
    }

    // ── The title ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_title_that_fits_is_carried_across_untouched()
        => Assert.Equal(Product, WonLotListing.Title(Won(landed: 100m)));

    /// <summary>eBay rejects a title past 80 characters at publish. Cutting it here means the
    /// seller sees what was cut, in the editor, before it costs them the listing.</summary>
    [Fact]
    public void A_long_title_is_cut_to_ebays_limit_on_a_word_boundary()
    {
        var lot = Won(landed: 100m);
        lot.Item = "Bitmain Antminer S19j Pro 104TH Bitcoin Miner With PSU Tested Working Ships Fast From USA";

        var title = WonLotListing.Title(lot);

        Assert.True(title.Length <= WonLotListing.MaxTitleLength, $"title was {title.Length} characters");
        Assert.DoesNotContain("  ", title, StringComparison.Ordinal);
        Assert.False(title.EndsWith(' '), "a title should not end mid-space");
        Assert.StartsWith("Bitmain Antminer S19j Pro 104TH", title, StringComparison.Ordinal);
        Assert.Contains(WonLotListing.Notes(lot), n => n.Contains("cut", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A single 90-character part number has no word boundary worth backing up to, and
    /// giving back "S19J" instead would be worse than a hard cut.</summary>
    [Fact]
    public void One_very_long_word_is_cut_hard_rather_than_back_to_nothing()
    {
        var lot = Won(landed: 100m);
        lot.Item = "S19J " + new string('X', 120);

        var title = WonLotListing.Title(lot);

        Assert.Equal(WonLotListing.MaxTitleLength, title.Length);
    }

    [Fact]
    public void Whitespace_in_a_pasted_lot_name_is_collapsed()
    {
        var lot = Won(landed: 100m);
        lot.Item = "  Antminer   S19j\tPro  ";

        Assert.Equal("Antminer S19j Pro", WonLotListing.Title(lot));
    }

    // ── What the draft refuses to invent ──────────────────────────────────────

    /// <summary>
    /// The condition, the photos and the eBay category are not knowable from a live feed. A draft
    /// that guessed them would publish a claim about somebody's item on their behalf — so they are
    /// left for the seller, and the result says so rather than letting it be discovered by a buyer.
    /// </summary>
    [Fact]
    public void The_draft_leaves_what_it_cannot_know_blank_and_names_it()
    {
        var lot = Won(landed: 120m, resale: 300m);

        var draft = WonLotListing.Draft(lot);

        Assert.Equal("", draft.Data.CategoryId);
        Assert.Equal("", draft.Data.Category);
        Assert.Empty(draft.Data.ImageUrls);
        Assert.Equal("", draft.Data.ConditionDescription);
        Assert.Contains(WonLotListing.Notes(lot),
            n => n.Contains("condition", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_draft_carries_the_title_the_price_and_one_unit()
    {
        var draft = WonLotListing.Draft(Won(landed: 120m, resale: 300m));

        Assert.Equal(Product, draft.Title);
        Assert.Equal(Product, draft.Data.Title);
        Assert.Equal(299.99m, draft.Data.Price);
        Assert.Equal(1, draft.Data.Quantity);
        Assert.Equal("FIXED_PRICE", draft.Data.ListingFormat);
        Assert.Contains(Product, draft.Data.Description, StringComparison.Ordinal);
    }

    /// <summary>The description says nothing about the item's state, because this app has never
    /// seen the item. What it does say is where the answer is: the photos, and the seller.</summary>
    [Fact]
    public void The_description_makes_no_claim_about_the_condition()
    {
        var description = WonLotListing.Description(Won(landed: 120m, resale: 300m));

        foreach (var claim in new[] { "excellent", "mint", "works", "tested", "new" })
            Assert.DoesNotContain(claim, description, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("photos", description, StringComparison.OrdinalIgnoreCase);
    }

    // ── The SKU ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_sku_is_dated_prefixed_and_unique_to_the_row()
    {
        var a = Won(landed: 100m);
        var b = Won(landed: 100m);

        var skuA = WonLotListing.Sku(a);

        Assert.StartsWith("WN-20260806-", skuA, StringComparison.Ordinal);
        Assert.NotEqual(skuA, WonLotListing.Sku(b));
        // Two presses on ONE row must mint the same SKU, or the draft and the deal card stop being
        // about the same object.
        Assert.Equal(skuA, WonLotListing.Sku(a));
    }

    // ── The deal card ─────────────────────────────────────────────────────────

    /// <summary>
    /// The split is the point: the hammer price plus the platform's premium is a unit cost, and the
    /// shipping is inbound freight. That is exactly what <see cref="CostBasisStore"/> wants when the
    /// existing pipeline writes the basis at Listed — and together they are the row's landed cost,
    /// so the board's capital and the buy sheet's spend are the same money.
    /// </summary>
    [Fact]
    public void The_deal_carries_the_cash_in_the_split_the_cost_basis_table_wants()
    {
        var lot = Won(landed: 143m, resale: 300m);
        lot.WinningBid = 120m;
        lot.BuyerFee = 8m;
        lot.ShippingCost = 15m;

        var deal = WonLotListing.Deal(lot, Now);

        Assert.Equal(128m, deal.PurchasePrice);
        Assert.Equal(15m, deal.PurchaseExtraCost);
        Assert.Equal(lot.LandedCost, deal.PurchasePrice + deal.PurchaseExtraCost);
        Assert.Equal(1, deal.Quantity);
        Assert.Equal(DealStages.Bought, deal.Stage);
    }

    /// <summary>
    /// Keyed on the row, which is what makes a second press update one card instead of putting the
    /// same night's capital on the board twice.
    /// </summary>
    [Fact]
    public void The_deal_is_keyed_on_the_row_so_listing_twice_cannot_double_the_capital()
    {
        var lot = Won(landed: 143m, resale: 300m);

        var first = WonLotListing.Deal(lot, Now);
        var second = WonLotListing.Deal(lot, Now.AddMinutes(20));

        Assert.Equal(WonLotListing.DealSource, first.Source);
        Assert.Equal(lot.Id, first.SourceItemId);
        Assert.Equal(first.SourceItemId, second.SourceItemId);
    }

    /// <summary>The forecast is frozen with the card that made it — the board grades these later,
    /// which only works if it is the number the seller actually acted on.</summary>
    [Fact]
    public void The_deal_carries_the_cards_forecast_and_the_ceiling_it_was_bought_under()
    {
        var lot = Won(landed: 143m, resale: 300m, profit: 84m);
        lot.DaysToCash = 14;
        lot.CeilingAtWin = 180m;
        lot.CompCount = 8;
        lot.SellThroughRate = 80m;

        var deal = WonLotListing.Deal(lot, Now);

        Assert.Equal(300m, deal.ProjectedSalePrice);
        Assert.Equal(84m, deal.ProjectedNetProfit);
        Assert.Equal(14, deal.ProjectedDaysToCash);
        Assert.Equal(180m, deal.MaxBuyPrice);
        Assert.Contains("8 sold", deal.ProjectedBasis, StringComparison.Ordinal);
        Assert.Contains("80% sell-through", deal.ProjectedBasis, StringComparison.Ordinal);
    }

    /// <summary>A card with no ceiling at all has no line to have held, and "bought $143 over $0"
    /// would be the board inventing a discipline failure.</summary>
    [Fact]
    public void A_lot_with_no_ceiling_carries_no_maximum_onto_the_board()
        => Assert.Null(WonLotListing.Deal(Won(landed: 143m, resale: 300m), Now).MaxBuyPrice);

    [Fact]
    public void An_unpriced_lot_says_so_on_the_board_rather_than_claiming_comps()
    {
        var basis = WonLotListing.ProjectedBasis(Won(landed: 90m, priced: false));

        Assert.Contains("No eBay sold comps", basis, StringComparison.Ordinal);
    }

    /// <summary>The deal is validated by the same rules every other deal is — including the one that
    /// refuses a Bought card with no price paid, which this can always satisfy.</summary>
    [Fact]
    public void The_deal_passes_the_boards_own_validation()
    {
        var record = DealStore.FromRequest(WonLotListing.Deal(Won(landed: 143m, resale: 300m), Now));

        Assert.Equal(DealStages.Bought, record.Stage);
        Assert.Equal(Product, record.Title);
        Assert.NotNull(record.PurchasePrice);
    }

    // ── The sentence ──────────────────────────────────────────────────────────

    /// <summary>The ask rounds DOWN when it is spoken, like every other figure this screen says
    /// about money coming in. A seller must never hear a bigger number than the draft carries.</summary>
    [Fact]
    public void The_spoken_ask_rounds_down_and_the_board_is_only_claimed_when_it_is_true()
    {
        var lot = Won(landed: 120m, resale: 299.99m);

        var tracked = WonLotListing.Say(lot, 299.99m, alreadyListed: false, onDealBoard: true);
        var not = WonLotListing.Say(lot, 299.99m, alreadyListed: false, onDealBoard: false);

        Assert.Contains("$299", tracked, StringComparison.Ordinal);
        Assert.DoesNotContain("$300", tracked, StringComparison.Ordinal);
        Assert.Contains("deal board", tracked, StringComparison.Ordinal);
        Assert.DoesNotContain("deal board", not, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_press_says_the_draft_already_exists()
        => Assert.Contains("Already drafted",
            WonLotListing.Say(Won(landed: 120m, resale: 300m), 299.99m, alreadyListed: true, onDealBoard: true),
            StringComparison.Ordinal);

    // ── The sheet remembers ───────────────────────────────────────────────────

    [Fact]
    public void A_row_can_be_found_by_its_own_id_and_an_unknown_id_is_not_an_error()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        var id = sheet.Record(Card(), Now).Lots[0].Id;

        Assert.NotNull(sheet.Find(id));
        Assert.Null(sheet.Find("not-an-id"));
        Assert.Null(sheet.Find(null));
    }

    /// <summary>
    /// The draft is a file on the seller's desktop that this app does not watch. The only moment it
    /// is knowable that this row became that draft is the moment it is made — so it is written down,
    /// and it survives the app being restarted between the show and the listing session.
    /// </summary>
    [Fact]
    public void Marking_a_row_listed_survives_a_restart()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        var id = sheet.Record(Card(), Now).Lots[0].Id;

        sheet.MarkListed(id, "antminer_1234.json", Product, 299.99m, "WN-20260806-ABCDEF", dealId: 7, nowUtc: Now);

        var row = new LiveBuySheet(temp.Path).Read().Lots[0];
        Assert.Equal("antminer_1234.json", row.ListedDraftFile);
        Assert.Equal(299.99m, row.ListedPrice);
        Assert.Equal("WN-20260806-ABCDEF", row.ListedSku);
        Assert.Equal(7, row.DealId);
        Assert.Equal(Now, row.ListedAtUtc);
    }

    [Fact]
    public void Marking_an_unknown_row_changes_nothing_and_still_answers_with_the_sheet()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        sheet.Record(Card(), Now);

        var after = sheet.MarkListed("not-an-id", "x.json", Product, 10m, "WN-1", dealId: 0, nowUtc: Now);

        Assert.Equal(1, after.LotCount);
        Assert.Equal(0, after.ListedCount);
    }

    /// <summary>
    /// None of the night's projected resale arrives while the lots are in boxes, so the sheet counts
    /// how much of it has actually been drafted — and only mentions it once some of it has, because
    /// mid-show every row is unlisted and saying so after each win is a nag.
    /// </summary>
    [Fact]
    public void The_sheet_counts_what_has_been_drafted_and_only_speaks_of_it_once_some_has()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        sheet.Record(Card(), Now);
        var id = sheet.Record(Card(), Now.AddMinutes(1)).Lots[0].Id;

        var before = sheet.Read();
        Assert.Equal(0, before.ListedCount);
        Assert.DoesNotContain("drafted", before.Say, StringComparison.OrdinalIgnoreCase);

        var after = sheet.MarkListed(id, "a.json", Product, 299.99m, "WN-1", dealId: 3, nowUtc: Now);

        Assert.Equal(1, after.ListedCount);
        Assert.Contains("1 of 2 drafted", after.Say, StringComparison.Ordinal);
    }

    /// <summary>The row's sentence is its accessible label, and it grows the one clause that says
    /// what happened next — rounded down, like the rest of what it says about money coming in.</summary>
    [Fact]
    public void The_rows_sentence_says_it_has_been_drafted()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        var id = sheet.Record(Card(), Now).Lots[0].Id;

        var say = sheet.MarkListed(id, "a.json", Product, 299.99m, "WN-1", dealId: 3, nowUtc: Now).Lots[0].Say;

        Assert.Contains("Drafted at $299.", say, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpriced_row_that_has_been_drafted_says_so_too()
    {
        var lot = Won(landed: 90m, priced: false);
        lot.ListedDraftFile = "a.json";

        Assert.Contains("Drafted, with no price on it.", LiveBuySheet.SayRow(lot), StringComparison.Ordinal);
    }

    /// <summary>A row that has not been listed says nothing about listing. The clause is a fact
    /// about this row, not a nudge attached to every row.</summary>
    [Fact]
    public void A_row_that_is_still_a_box_says_nothing_about_drafts()
        => Assert.DoesNotContain("Drafted", LiveBuySheet.SayRow(Won(landed: 120m, resale: 300m)),
            StringComparison.Ordinal);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static WonLot Won(
        decimal landed, decimal? resale = null, decimal? profit = null, bool priced = true) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Item = Product,
            WonAtUtc = Now,
            WinningBid = landed,
            LandedCost = landed,
            Priced = priced,
            ResalePrice = priced ? resale : null,
            ProjectedProfit = priced ? profit : null,
            Call = priced ? LiveBidCalls.Bid : LiveBidCalls.NoData,
        };

    /// <summary>A real card, so the rows these tests mark are the rows the endpoint writes.</summary>
    private static LiveBidCard Card()
    {
        var profit = new ProfitCalculator();
        var advisor = new LiveBidAdvisor(profit, new JackpotHunter(profit));
        return advisor.Build(
            Product,
            new MarketAnalysisResult
            {
                PriceEstimate = new PriceEstimate
                {
                    MedianPrice = 300m, ExpectedSalePrice = 300m, QuickSalePrice = 255m,
                    PricedOnCompCount = 8, IdentityVerified = true,
                    LocalOldestSoldAtUtc = Now.AddDays(-60), LocalNewestSoldAtUtc = Now.AddDays(-9),
                },
                SellThrough = new SellThroughAnalysis
                {
                    SoldComparableCount = 8, ActiveComparableCount = 10, SellThroughRate = 80m,
                    SellThroughScore = 72, EstimatedDaysToSell = 14,
                },
                Confidence = new ConfidenceBreakdown { Score = 70, Level = "Good" },
                Sources = new SourceBreakdown { LocalComparableCount = 8 },
            },
            new LiveBidRequest { Title = Product, CurrentBid = 120m, ShippingCost = 15m, BuyerFeePercent = 8m },
            new FeeProfile(),
            nowUtc: Now);
    }

    private sealed class TempSheet : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ing-list-it-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            foreach (var p in new[] { Path, AtomicFile.BackupPathFor(Path), AtomicFile.TempPathFor(Path) })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
