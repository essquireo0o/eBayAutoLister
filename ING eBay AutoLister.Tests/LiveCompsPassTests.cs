using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The live half of the sold-comps path, run for a whole scan: which product groups get a live eBay
/// lookup, in what order, and what the pass does with each answer.
/// </summary>
/// <remarks>
/// This is the piece that puts Facebook Marketplace rows on the same stored-plus-live path the
/// eBay scanner's rows always had. The two rules worth pinning are the ones a future "just look
/// everything up" would break: it never spends a call on a product that already has the comps, and
/// it stops the moment the lookup refuses — the allowance is per day, so the next product would
/// only be told the same thing.
/// </remarks>
public class LiveCompsPassTests
{
    private static LiveCompsCandidate C(
        string key, string tier = LocalArbitrageEvidence.None, decimal? profit = null, decimal ask = 100m,
        bool repriceable = true, string? query = null) =>
        new(key, query ?? $"{key} title", tier, profit, ask, repriceable);

    // ── Which products get a lookup ──────────────────────────────────────────

    [Fact]
    public void A_confident_group_is_never_looked_up()
    {
        // It has the comps. A call spent on it is a call the "no sold data" card next to it needed.
        var picked = LiveCompsPass.SelectTargets(
            [C("backed", LocalArbitrageEvidence.Confident, profit: 500m), C("thin", LocalArbitrageEvidence.Low, profit: 5m)], 5);

        Assert.Equal(["thin"], picked);
    }

    [Fact]
    public void A_group_a_title_lookup_cannot_reprice_is_skipped()
    {
        // Lots and freebies are priced by their own arithmetic; a refused category would be refused
        // again. The same rule the board's own per-row button follows.
        var picked = LiveCompsPass.SelectTargets(
            [C("lot", repriceable: false, ask: 900m), C("drill", ask: 40m), C("blank", query: "  ")], 5);

        Assert.Equal(["drill"], picked);
    }

    [Fact]
    public void The_order_is_where_a_fresh_answer_is_worth_most()
    {
        var picked = LiveCompsPass.SelectTargets(
        [
            C("loss-estimate", LocalArbitrageEvidence.Low, profit: -30m, ask: 950m),
            C("cheap-unknown", ask: 9m),
            C("small-winner", LocalArbitrageEvidence.Low, profit: 12m, ask: 60m),
            C("dear-unknown", ask: 900m),
            C("big-winner", LocalArbitrageEvidence.Low, profit: 140m, ask: 400m),
        ], 10);

        // Profitable estimates first, biggest first — the rows a seller is about to act on. Then
        // the groups with no opinion at all, dearest first — no opinion on $900 is the bigger gap.
        // Then the estimates that already look like losses.
        Assert.Equal(["big-winner", "small-winner", "dear-unknown", "cheap-unknown", "loss-estimate"], picked);
    }

    [Fact]
    public void The_budget_is_a_hard_cap_and_zero_means_none()
    {
        var groups = new[] { C("a", ask: 3m), C("b", ask: 2m), C("c", ask: 1m) };

        Assert.Equal(["a", "b"], LiveCompsPass.SelectTargets(groups, 2));
        Assert.Empty(LiveCompsPass.SelectTargets(groups, 0));
        Assert.Empty(LiveCompsPass.SelectTargets(groups, -1));
    }

    [Fact]
    public void The_defaults_mirror_the_boards_own_auto_deepen_pass_and_a_days_allowance()
    {
        // Three per scan, like the three rows the board deepens after a scan; never more than the
        // default daily allowance, so one feed of forty cards cannot spend tomorrow's lookups.
        Assert.Equal(3, LiveCompsPass.DefaultBudget);
        Assert.Equal(LiveComps.DefaultDailyLimit, LiveCompsPass.MaxBudget);
    }

    [Fact]
    public void A_candidate_is_read_off_the_row_the_board_would_show()
    {
        var thin = new LocalArbitrageOpportunity { EvidenceTier = LocalArbitrageEvidence.Low, NetProfit = 42m };
        var c = LiveCompsPass.Candidate("k", "Bitmain Antminer S19j Pro", thin, 150m);
        Assert.Equal(("k", "Bitmain Antminer S19j Pro", LocalArbitrageEvidence.Low, 42m, 150m, true),
            (c.Key, c.Query, c.Tier, c.PreliminaryProfit, c.LocalAsk, c.Repriceable));

        // A Facebook row with no comps is exactly the candidate this exists for.
        var unpriced = new LocalArbitrageOpportunity { Source = "facebook", EvidenceTier = LocalArbitrageEvidence.None };
        Assert.True(LiveCompsPass.Candidate("fb", "DeWalt DCD771C2 20V Drill", unpriced, 40m).Repriceable);
        Assert.Null(LiveCompsPass.Candidate("fb", "DeWalt DCD771C2 20V Drill", unpriced, 40m).PreliminaryProfit);

        // And the rows the board itself refuses to reprice by title.
        Assert.False(LiveCompsPass.Candidate("lot", "pallet", new LocalArbitrageOpportunity { Liquidation = new LiquidationLotEconomics() }, 1m).Repriceable);
        Assert.False(LiveCompsPass.Candidate("free", "couch", new LocalArbitrageOpportunity { Freebie = new FreebieEconomics() }, 0m).Repriceable);
        Assert.False(LiveCompsPass.Candidate("car", "2011 Tundra",
            new LocalArbitrageOpportunity { Valuation = new ResaleValuation { Status = ValuationStatuses.Manual, ProviderId = "vehicle_book" } }, 9000m).Repriceable);
    }

    [Fact]
    public void A_row_the_comps_provider_found_nothing_for_is_exactly_what_the_lookup_is_for()
    {
        // The eBay-comps provider stamps "manual / no sold history" on a row it looked up and found
        // empty — the same status a refused vehicle gets. Reading that as a refusal is the bug that
        // kept every "no sold data" Facebook card out of the live pass: the rows with the least
        // evidence were the only ones never offered more.
        var noHistory = new ResaleValuation
        {
            Status = ValuationStatuses.Manual, ProviderId = ResaleValuationProviders.EbayComps, SourceLabel = "no sold history",
        };
        var row = new LocalArbitrageOpportunity { Source = "facebook", EvidenceTier = LocalArbitrageEvidence.None, Valuation = noHistory };

        Assert.False(LiveCompsPass.ValuationRefused(noHistory));
        Assert.True(LiveCompsPass.Candidate("fb", "ddr4 8gb computer memory", row, 30m).Repriceable);
        Assert.Equal(["fb"], LiveCompsPass.SelectTargets([LiveCompsPass.Candidate("fb", "ddr4 8gb computer memory", row, 30m)], 3));

        // Whereas a refusal by any other provider stays a refusal, and no valuation at all is not one.
        Assert.True(LiveCompsPass.ValuationRefused(new ResaleValuation { Status = ValuationStatuses.Manual, ProviderId = "vehicle_book" }));
        Assert.False(LiveCompsPass.ValuationRefused(new ResaleValuation { Status = ValuationStatuses.Comps, ProviderId = ResaleValuationProviders.EbayComps }));
        Assert.False(LiveCompsPass.ValuationRefused(null));
    }

    // ── What the pass does with each answer ──────────────────────────────────

    private static Func<string, CancellationToken, Task<LiveCompsRun>> Answering(
        List<string> asked, params (string Outcome, string Message)[] script)
    {
        var i = 0;
        return (query, _) =>
        {
            asked.Add(query);
            var (outcome, message) = script[Math.Min(i++, script.Length - 1)];
            return Task.FromResult(new LiveCompsRun { Query = query, Finished = true, Outcome = outcome, Message = message, RowsFound = outcome == "ok" ? 7 : 0 });
        };
    }

    [Fact]
    public async Task Only_a_lookup_that_found_rows_marks_its_group_for_repricing()
    {
        var asked = new List<string>();
        var pass = await LiveCompsPass.RunAsync(
            [("drill", "DeWalt DCD771C2"), ("odd", "Zxqv nobody sells"), ("miner", "Antminer S19j Pro")],
            Answering(asked, ("ok", ""), ("empty", ""), ("ok", "")));

        Assert.Equal(["DeWalt DCD771C2", "Zxqv nobody sells", "Antminer S19j Pro"], asked);
        Assert.Equal(["drill", "miner"], pass.Refreshed);
        // The empty answer was a real call — the API bills it — and proved a negative worth knowing.
        Assert.Equal(3, pass.LookupsUsed);
        Assert.Equal("empty", pass.Outcomes["odd"]);
        Assert.False(pass.Stopped);
        Assert.Equal("", pass.Note);
    }

    [Fact]
    public async Task A_model_fetched_earlier_today_costs_nothing_and_needs_no_re_read()
    {
        // "fresh" rows were already in the database when the stored-comps pass ran.
        var pass = await LiveCompsPass.RunAsync([("a", "a"), ("b", "b")], Answering([], ("fresh", ""), ("ok", "")));

        Assert.Equal(["b"], pass.Refreshed);
        Assert.Equal(1, pass.LookupsUsed);
        Assert.False(pass.Stopped);
    }

    [Fact]
    public async Task The_pass_stops_the_moment_the_allowance_is_spent_and_keeps_the_lookups_own_sentence()
    {
        var asked = new List<string>();
        var refusal = "That's today's 10 live sold-price lookups. The rest are priced from stored comps, and your next 10 arrive at 00:00 UTC.";
        var pass = await LiveCompsPass.RunAsync(
            [("a", "a"), ("b", "b"), ("c", "c"), ("d", "d")],
            Answering(asked, ("ok", ""), ("rate_limited", refusal), ("ok", "")));

        // The third and fourth were never asked — they would only have been told the same thing.
        Assert.Equal(["a", "b"], asked);
        Assert.Equal(["a"], pass.Refreshed);
        Assert.Equal(1, pass.LookupsUsed);       // a refusal is not a call
        Assert.True(pass.Stopped);
        Assert.Equal(refusal, pass.Note);
        Assert.Equal(2, pass.Outcomes.Count);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("unavailable")]
    public async Task Busy_and_switched_off_end_the_pass_without_spending_anything(string outcome)
    {
        var asked = new List<string>();
        var pass = await LiveCompsPass.RunAsync([("a", "a"), ("b", "b")], Answering(asked, (outcome, "why")));

        Assert.Single(asked);
        Assert.Equal(0, pass.LookupsUsed);
        Assert.True(pass.Stopped);
        Assert.Equal("why", pass.Note);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("timeout")]
    public async Task A_source_that_just_failed_is_not_asked_again_with_the_rest_of_a_tiny_budget(string outcome)
    {
        var asked = new List<string>();
        var pass = await LiveCompsPass.RunAsync([("a", "a"), ("b", "b")], Answering(asked, (outcome, "down")));

        Assert.Single(asked);
        Assert.Equal(1, pass.LookupsUsed);       // the failed attempt was still billed
        Assert.Empty(pass.Refreshed);
        Assert.True(pass.Stopped);
        Assert.Equal("down", pass.Note);
    }

    [Fact]
    public async Task An_empty_target_list_is_a_clean_no_op()
    {
        var pass = await LiveCompsPass.RunAsync([], (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.Equal(0, pass.LookupsUsed);
        Assert.Empty(pass.Refreshed);
        Assert.False(pass.Stopped);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LiveCompsPass.RunAsync([("a", "a")], Answering([], ("ok", "")), cts.Token));
    }
}
