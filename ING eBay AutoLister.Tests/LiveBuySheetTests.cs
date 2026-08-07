using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The buy sheet is the first thing on the WhatsNot screen that is a FACT rather than advice: the
// lots actually won, at the prices actually paid. What is pinned here is that it stays one:
//
//   · a row is the card's own figures, copied — never a second calculation of the same money;
//   · a lot nothing could price is still a real spend, and the total says so rather than dropping it;
//   · the ceiling is what the app said BEFORE the hammer, written down while it is still knowable;
//   · every sentence rounds against the seller, exactly as the card's spoken line does;
//   · the return is a return on the money that has evidence behind it, not on the whole spend.
public class LiveBuySheetTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly JackpotHunter Hunter = new(Profit);
    private static readonly LiveBidAdvisor Advisor = new(Profit, Hunter);
    private static readonly FeeProfile Fees = new();
    private static readonly DateTime Now = new(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    // ── The row is the card ───────────────────────────────────────────────────

    /// <summary>
    /// The whole legitimacy of this feature. A won lot goes back through the SAME
    /// <see cref="LiveBidAdvisor.Build"/> at the price it hammered at, and the row copies what came
    /// back. A row saying $84 profit beside a card saying $60 about one lot at one price is worse
    /// than no row at all — the seller acts on whichever they read last.
    /// </summary>
    [Fact]
    public void A_row_carries_the_cards_own_money_and_computes_none_of_it()
    {
        var card = Advisor.Build(Product, Analysis(), Ask(bid: 120m, shipping: 15m, fee: 8m), Fees, nowUtc: Now);

        var row = LiveBuySheet.RowFrom(card, Now);

        Assert.Equal(card.CurrentBid, row.WinningBid);
        Assert.Equal(card.BuyerFee, row.BuyerFee);
        Assert.Equal(card.ShippingCost, row.ShippingCost);
        Assert.Equal(card.LandedCostNow, row.LandedCost);
        Assert.Equal(card.ResalePrice, row.ResalePrice);
        Assert.Equal(card.ProfitNow, row.ProjectedProfit);
        Assert.Equal(card.RoiNow, row.ProjectedRoiPercent);
        Assert.Equal(card.MaxBid, row.CeilingAtWin);
        Assert.Equal(card.Call, row.Call);
        Assert.Equal(card.CompCount, row.CompCount);
    }

    /// <summary>
    /// The landed cost is the bid plus the premium plus the shipping — what winning actually cost,
    /// not what was bid. A sheet that totalled bare bids would report a night as cheaper than the
    /// card statement.
    /// </summary>
    [Fact]
    public void The_row_is_costed_all_in_not_at_the_bid()
    {
        var card = Advisor.Build(Product, Analysis(), Ask(bid: 100m, shipping: 20m, fee: 10m), Fees, nowUtc: Now);

        var row = LiveBuySheet.RowFrom(card, Now);

        Assert.Equal(100m, row.WinningBid);
        Assert.Equal(130m, row.LandedCost);   // 100 + 10% + 20
    }

    /// <summary>
    /// Nothing priced it, and it was won anyway. The spend is real and has to count; the resale
    /// side is absent rather than zero, because a zero would drag every total down as though the
    /// thing were established to be worthless.
    /// </summary>
    [Fact]
    public void An_unpriceable_lot_is_still_a_real_spend()
    {
        var card = Advisor.Build(Product, analysis: null, Ask(bid: 80m, shipping: 10m), Fees, nowUtc: Now);
        Assert.Equal(LiveBidCalls.NoData, card.Call);

        var row = LiveBuySheet.RowFrom(card, Now);

        Assert.False(row.Priced);
        Assert.Equal(90m, row.LandedCost);
        Assert.Null(row.ResalePrice);
        Assert.Null(row.ProjectedProfit);
        Assert.Null(row.ProjectedRoiPercent);
    }

    // ── The ceiling, written down while it is still knowable ──────────────────

    [Fact]
    public void Winning_above_the_ceiling_is_recorded_as_such()
    {
        var underneath = Advisor.Build(Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now);
        Assert.True(underneath.MaxBid > 40m, "the fixture has to leave room under the ceiling");

        var over = Advisor.Build(Product, Analysis(), Ask(bid: underneath.MaxBid + 25m), Fees, nowUtc: Now);
        var row = LiveBuySheet.RowFrom(over, Now);

        Assert.Equal(25m, row.PaidOverCeiling);
        Assert.Equal(underneath.MaxBid, row.CeilingAtWin);
        Assert.Equal(LiveBidCalls.Stop, row.Call);
    }

    [Fact]
    public void Winning_at_or_under_the_ceiling_is_not_an_overpay()
    {
        var card = Advisor.Build(Product, Analysis(), Ask(bid: 30m), Fees, nowUtc: Now);
        Assert.Equal(0m, LiveBuySheet.RowFrom(card, Now).PaidOverCeiling);

        var atTheLine = Advisor.Build(Product, Analysis(), Ask(bid: card.MaxBid), Fees, nowUtc: Now);
        Assert.Equal(0m, LiveBuySheet.RowFrom(atTheLine, Now).PaidOverCeiling);
    }

    /// <summary>
    /// No ceiling at all is not an overpay of the whole bid. A card with no maximum has no line to
    /// be over, and "$180 past it" would invite the reading that some smaller bid was fine.
    /// </summary>
    [Fact]
    public void A_lot_with_no_ceiling_at_all_is_not_recorded_as_an_overpay()
    {
        var card = LiveBuySheet.RowFrom(
            new LiveBidCard { Item = Product, Call = LiveBidCalls.Stop, CurrentBid = 180m, MaxBid = 0m, LandedCostNow = 180m },
            Now);

        Assert.Equal(0m, card.PaidOverCeiling);
    }

    /// <summary>A rate with no active listings under it is not a rate. The card shows a dash for
    /// it, so the row must not store a number the card refused to show.</summary>
    [Fact]
    public void An_unbounded_sell_through_rate_is_not_carried_onto_the_row()
    {
        var card = Advisor.Build(Product, Analysis(rateIsUnbounded: true), Ask(bid: 60m), Fees, nowUtc: Now);

        Assert.Null(LiveBuySheet.RowFrom(card, Now).SellThroughRate);
    }

    // ── What a row says ───────────────────────────────────────────────────────

    /// <summary>
    /// Same rule as <see cref="LiveBidSpeech"/>: what was paid rounds up, what it is worth rounds
    /// down, what was overpaid rounds up. A line skimmed during a show must never be the optimistic
    /// version of the row under it.
    /// </summary>
    [Fact]
    public void The_row_sentence_rounds_against_the_seller()
    {
        var said = LiveBuySheet.SayRow(new WonLot
        {
            Item = Product, WinningBid = 119.20m, LandedCost = 140.60m, Priced = true,
            ResalePrice = 310.90m, ProjectedProfit = 84.90m, ProjectedRoiPercent = 47.8m,
        });

        Assert.Contains("$120", said, StringComparison.Ordinal);   // paid, rounded up
        Assert.Contains("$141", said, StringComparison.Ordinal);   // all in, rounded up
        Assert.Contains("$310", said, StringComparison.Ordinal);   // worth, rounded down
        Assert.Contains("$84 profit", said, StringComparison.Ordinal);
        Assert.Contains("47% return", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_resale_figure_says_so_and_claims_nothing_else()
    {
        var said = LiveBuySheet.SayRow(new WonLot
        {
            Item = Product, WinningBid = 80m, LandedCost = 90m, Priced = false,
        });

        Assert.Contains("Nothing on eBay priced it", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Resells", said, StringComparison.Ordinal);
        Assert.DoesNotContain("profit", said, StringComparison.Ordinal);
        Assert.DoesNotContain("return", said, StringComparison.Ordinal);
    }

    /// <summary>A loss is stated as a loss. "-$40 profit" is a sentence a reader can take the wrong
    /// way at a glance, which is the only way this line is ever read.</summary>
    [Fact]
    public void A_lot_bought_at_a_loss_says_down_rather_than_a_negative_profit()
    {
        var said = LiveBuySheet.SayRow(new WonLot
        {
            Item = Product, WinningBid = 300m, LandedCost = 300m, Priced = true,
            ResalePrice = 250m, ProjectedProfit = -40.20m, ProjectedRoiPercent = -13.4m,
        });

        Assert.Contains("$41 down", said, StringComparison.Ordinal);   // the loss rounds up
        Assert.DoesNotContain("profit", said, StringComparison.Ordinal);
        Assert.DoesNotContain("return", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overpaid_row_says_how_far_over_it_went()
    {
        var said = LiveBuySheet.SayRow(new WonLot
        {
            Item = Product, WinningBid = 200m, LandedCost = 200m, Priced = true,
            ResalePrice = 260m, ProjectedProfit = 20m, ProjectedRoiPercent = 10m,
            CeilingAtWin = 179.40m, PaidOverCeiling = 20.60m,
        });

        Assert.Contains("$21 over the ceiling", said, StringComparison.Ordinal);
    }

    // ── The totals ────────────────────────────────────────────────────────────

    [Fact]
    public void The_totals_add_up_what_was_spent_and_what_it_is_worth()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 130m, resale: 300m, profit: 120m),
            Won(landed: 70m, resale: 150m, profit: 40m),
        ]);

        Assert.Equal(2, sheet.LotCount);
        Assert.Equal(200m, sheet.Spent);
        Assert.Equal(450m, sheet.ProjectedResale);
        Assert.Equal(160m, sheet.ProjectedProfit);
        Assert.Equal(80m, sheet.ProjectedRoiPercent);   // 160 on 200
    }

    /// <summary>
    /// The return is a return on the money that has a resale figure behind it. Dividing by the
    /// whole spend would report a lower number than the evidence supports and dress it up as
    /// caution — and the seller would read it as a worse night than they had.
    /// </summary>
    [Fact]
    public void The_return_is_measured_only_on_the_lots_that_could_be_priced()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 100m, resale: 300m, profit: 100m),
            Won(landed: 400m, priced: false),
        ]);

        Assert.Equal(500m, sheet.Spent);        // the bank statement's number
        Assert.Equal(100m, sheet.PricedSpend);  // the one the return is on
        Assert.Equal(100m, sheet.ProjectedRoiPercent);
        Assert.Equal(1, sheet.UnpricedCount);
    }

    [Fact]
    public void A_sheet_with_nothing_priced_states_no_return_at_all()
    {
        var sheet = LiveBuySheet.Compose([Won(landed: 400m, priced: false)]);

        Assert.Null(sheet.ProjectedRoiPercent);
        Assert.Equal(0m, sheet.ProjectedResale);
        Assert.DoesNotContain("Resells", sheet.Say, StringComparison.Ordinal);
        Assert.DoesNotContain("return", sheet.Say, StringComparison.Ordinal);
    }

    /// <summary>Unknown is not bad. Counting a lot nothing could price as a loss is how a screen
    /// teaches a seller to stop reading it.</summary>
    [Fact]
    public void An_unpriced_lot_is_not_counted_as_a_losing_one()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 400m, priced: false),
            Won(landed: 100m, resale: 90m, profit: -30m),
        ]);

        Assert.Equal(1, sheet.LosingCount);
        Assert.Equal(1, sheet.UnpricedCount);
    }

    [Fact]
    public void Overpays_are_counted_and_totalled()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 100m, resale: 200m, profit: 50m, over: 12.50m),
            Won(landed: 100m, resale: 200m, profit: 50m),
            Won(landed: 100m, resale: 200m, profit: 50m, over: 7.50m),
        ]);

        Assert.Equal(2, sheet.OverpaidCount);
        Assert.Equal(20m, sheet.OverpaidBy);
    }

    [Fact]
    public void The_newest_win_is_the_first_row()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 10m, priced: false, at: Now.AddMinutes(-30)),
            Won(landed: 20m, priced: false, at: Now),
            Won(landed: 30m, priced: false, at: Now.AddMinutes(-10)),
        ]);

        Assert.Equal([20m, 30m, 10m], sheet.Lots.Select(l => l.LandedCost));
        Assert.Equal(Now, sheet.LastWonUtc);
        Assert.Equal(Now.AddMinutes(-30), sheet.FirstWonUtc);
    }

    [Fact]
    public void An_empty_sheet_says_nothing_at_all()
    {
        var sheet = LiveBuySheet.Compose([]);

        Assert.Equal(0, sheet.LotCount);
        Assert.Equal("", sheet.Say);
        Assert.Null(sheet.ProjectedRoiPercent);
        Assert.Null(sheet.FirstWonUtc);
    }

    // ── What the night says ───────────────────────────────────────────────────

    [Fact]
    public void The_nights_sentence_rounds_against_the_seller_too()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 620.40m, resale: 1440.90m, profit: 420.80m),
            Won(landed: 619.60m, resale: 1440.10m, profit: 419.20m),
        ]);

        Assert.Contains("2 lots won", sheet.Say, StringComparison.Ordinal);
        Assert.Contains("$1,240 spent", sheet.Say, StringComparison.Ordinal);
        Assert.Contains("$2,881", sheet.Say, StringComparison.Ordinal);   // worth, rounded down
        Assert.Contains("$840 profit", sheet.Say, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unflattering clauses are the reason a seller reads this instead of the receipts. They
    /// are never dropped, and they come after the money rather than instead of it.
    /// </summary>
    [Fact]
    public void The_discipline_clauses_are_said_out_loud()
    {
        var sheet = LiveBuySheet.Compose([
            Won(landed: 200m, resale: 300m, profit: 60m, over: 18.40m),
            Won(landed: 100m, resale: 90m, profit: -25m),
            Won(landed: 50m, priced: false),
        ]);

        Assert.Contains("1 won above the ceiling, $19 over in total", sheet.Say, StringComparison.Ordinal);
        Assert.Contains("1 priced to lose money", sheet.Say, StringComparison.Ordinal);
        Assert.Contains("1 with no resale figure", sheet.Say, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_night_carries_no_warning_clauses()
    {
        var sheet = LiveBuySheet.Compose([Won(landed: 100m, resale: 300m, profit: 120m)]);

        Assert.DoesNotContain("above the ceiling", sheet.Say, StringComparison.Ordinal);
        Assert.DoesNotContain("lose money", sheet.Say, StringComparison.Ordinal);
        Assert.DoesNotContain("no resale figure", sheet.Say, StringComparison.Ordinal);
        Assert.Contains("1 lot won", sheet.Say, StringComparison.Ordinal);
    }

    [Fact]
    public void A_night_that_is_down_overall_says_down()
    {
        var sheet = LiveBuySheet.Compose([Won(landed: 300m, resale: 200m, profit: -80.40m)]);

        Assert.Contains("$81 down", sheet.Say, StringComparison.Ordinal);
        Assert.DoesNotContain("profit", sheet.Say, StringComparison.Ordinal);
    }

    // ── The file ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_win_survives_the_app_being_restarted_mid_show()
    {
        using var temp = new TempSheet();
        var card = Advisor.Build(Product, Analysis(), Ask(bid: 120m, shipping: 15m, fee: 8m), Fees, nowUtc: Now);

        new LiveBuySheet(temp.Path).Record(card, Now);

        // A different instance, as though the process had been restarted between lots.
        var reopened = new LiveBuySheet(temp.Path).Read();

        Assert.Equal(1, reopened.LotCount);
        Assert.Equal(Product, reopened.Lots[0].Item);
        Assert.Equal(card.LandedCostNow, reopened.Lots[0].LandedCost);
        Assert.Equal(card.ResalePrice, reopened.Lots[0].ResalePrice);
    }

    [Fact]
    public void Recording_returns_the_whole_sheet_so_nothing_adds_money_up_in_the_browser()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);

        sheet.Record(Card(bid: 100m), Now);
        var after = sheet.Record(Card(bid: 60m), Now.AddMinutes(2));

        Assert.Equal(2, after.LotCount);
        Assert.Equal(after.Lots.Sum(l => l.LandedCost), after.Spent);
        Assert.NotEqual("", after.Say);
    }

    [Fact]
    public void A_row_can_be_taken_off_and_an_unknown_id_changes_nothing()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        var recorded = sheet.Record(Card(bid: 100m), Now);
        var id = recorded.Lots[0].Id;

        Assert.Equal(1, sheet.Remove("not-an-id").LotCount);
        Assert.Equal(0, sheet.Remove(id).LotCount);
        Assert.Equal(0, new LiveBuySheet(temp.Path).Read().LotCount);
    }

    [Fact]
    public void Clearing_ends_the_show_and_the_next_win_starts_a_new_sheet()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);
        sheet.Record(Card(bid: 100m), Now);

        Assert.Equal(0, sheet.Clear().LotCount);
        Assert.Equal(1, sheet.Record(Card(bid: 50m), Now).LotCount);
    }

    /// <summary>The lot just won is the newest, and is the one thing a trim must never take.</summary>
    [Fact]
    public void Past_the_cap_the_oldest_lots_go_and_the_newest_stays()
    {
        using var temp = new TempSheet();
        var sheet = new LiveBuySheet(temp.Path);

        BuySheet? last = null;
        for (var i = 0; i < LiveBuySheet.MaxLots + 5; i++)
            last = sheet.Record(Card(bid: 10m + i), Now.AddSeconds(i));

        Assert.Equal(LiveBuySheet.MaxLots, last!.LotCount);
        Assert.Equal(10m + LiveBuySheet.MaxLots + 4, last.Lots[0].WinningBid);
    }

    /// <summary>
    /// An unreadable file is an empty sheet, not a crash on the one screen being used with a live
    /// stream running. Nothing is deleted to achieve that — the file is still there to look at.
    /// </summary>
    [Fact]
    public void An_unreadable_sheet_opens_empty_rather_than_throwing()
    {
        using var temp = new TempSheet();
        File.WriteAllText(temp.Path, "{ this is not the sheet ]");

        Assert.Equal(0, new LiveBuySheet(temp.Path).Read().LotCount);
        Assert.True(File.Exists(temp.Path), "a file we could not read is not a file to delete");
    }

    /// <summary>
    /// The sentence is regenerated from the numbers on load rather than trusted from the file. A
    /// row hand-edited — or written by an older build — must not put words on screen that its own
    /// figures no longer support.
    /// </summary>
    [Fact]
    public void The_stored_sentence_is_rebuilt_from_the_stored_numbers()
    {
        using var temp = new TempSheet();
        File.WriteAllText(temp.Path, """
            [{"id":"a1","item":"Antminer S19","wonAtUtc":"2026-08-06T20:00:00Z","winningBid":100,
              "landedCost":100,"priced":true,"resalePrice":300,"projectedProfit":150,
              "projectedRoiPercent":150,"call":"bid","say":"Won it for nothing, worth a million."}]
            """);

        var say = new LiveBuySheet(temp.Path).Read().Lots[0].Say;

        Assert.DoesNotContain("worth a million", say, StringComparison.Ordinal);
        Assert.Contains("$150 profit", say, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class TempSheet : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ing-buy-sheet-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            foreach (var p in new[] { Path, AtomicFile.BackupPathFor(Path), AtomicFile.TempPathFor(Path) })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private static WonLot Won(
        decimal landed, decimal? resale = null, decimal? profit = null, decimal over = 0m,
        bool priced = true, DateTime? at = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Item = Product,
            WonAtUtc = at ?? Now,
            WinningBid = landed,
            LandedCost = landed,
            Priced = priced,
            ResalePrice = priced ? resale : null,
            ProjectedProfit = priced ? profit : null,
            ProjectedRoiPercent = priced && profit is { } p && landed > 0m ? Math.Round(p / landed * 100m, 1) : null,
            PaidOverCeiling = over,
            Call = priced ? LiveBidCalls.Bid : LiveBidCalls.NoData,
        };

    private static LiveBidCard Card(decimal bid) =>
        Advisor.Build(Product, Analysis(), Ask(bid: bid), Fees, nowUtc: Now);

    private static LiveBidRequest Ask(
        decimal? bid = null, decimal? shipping = null, decimal? fee = null, decimal? target = null) =>
        new() { Title = Product, CurrentBid = bid, ShippingCost = shipping, BuyerFeePercent = fee, TargetRoiPercent = target };

    /// <summary>A market analysis in the shape <c>AnalyzeProductAsync</c> produces one, so the row
    /// is exercised through the same object the endpoint hands the advisor.</summary>
    private static MarketAnalysisResult Analysis(decimal? expected = 200m, bool rateIsUnbounded = false)
    {
        var newest = Now.AddDays(-9);
        var oldest = Now.AddDays(-60);

        return new MarketAnalysisResult
        {
            PriceEstimate = new PriceEstimate
            {
                MedianPrice = expected,
                ExpectedSalePrice = expected,
                QuickSalePrice = expected * 0.85m,
                Percentile25 = 170m,
                Percentile75 = 240m,
                MinimumRealisticPrice = 136m,
                MaximumRealisticPrice = 288m,
                LocalMedianPrice = expected,
                LocalExpectedSalePrice = expected,
                LocalWeight = 1m,
                PricedOnCompCount = 8,
                IdentityVerified = true,
                LocalOldestSoldAtUtc = oldest,
                LocalNewestSoldAtUtc = newest,
            },
            SellThrough = new SellThroughAnalysis
            {
                SoldComparableCount = 8,
                ActiveComparableCount = rateIsUnbounded ? 0 : 10,
                SellThroughRate = rateIsUnbounded ? null : 80m,
                RateIsUnbounded = rateIsUnbounded,
                SellThroughScore = 72,
                Interpretation = rateIsUnbounded ? "Unverified — no active comps to measure against" : "Very Strong",
                EstimatedMonthlySales = 4m,
                EstimatedDaysToSell = 14,
                LiquidityLevel = "Fast Mover",
            },
            Confidence = new ConfidenceBreakdown { Score = 70, Level = "Good" },
            Sources = new SourceBreakdown
            {
                LocalComparableCount = 8,
                TerapeakComparableCount = 0,
                LocalWeightPercent = 100m,
                PricedOnCompCount = 8,
                IdentityVerified = true,
            },
            TopSoldComparables =
            [
                new MarketplaceComparableResult
                {
                    ItemId = "c1", Title = Product, SoldPrice = 195m, TotalPrice = 195m,
                    Condition = "Used", SoldDate = newest, ItemUrl = "https://www.ebay.com/itm/c1",
                },
            ],
        };
    }
}
