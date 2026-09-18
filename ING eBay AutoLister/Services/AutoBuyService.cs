using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What one placement attempt came back with. <see cref="Ok"/> true means eBay accepted the call;
/// the rest says what that acceptance actually amounts to, because on eBay "accepted" is three
/// different things: a Buy It Now is a <b>commitment to pay</b> (not a payment), a bid is a standing
/// high bid <b>only while nobody outbids it</b>, and a Best Offer is a proposal the seller may decline.
/// </summary>
/// <param name="Committed">A Buy It Now the buyer is now obliged to pay for on eBay.</param>
/// <param name="HighBidder">For a bid: the max bid is currently the high bid.</param>
/// <param name="Link">Where on eBay the seller finishes the job (pay, or watch the bid).</param>
/// <param name="NotEnabled">eBay refused because this application may not place offers at all — a
/// keyset problem, not a listing problem, and every further attempt would fail the same way.</param>
public sealed record AutoBuyPlacement(
    bool Ok, string Detail, bool Committed = false, bool HighBidder = false, string Link = "", bool NotEnabled = false);

/// <summary>
/// Finds the listings a rule buys out of. A delegate rather than a hard call to
/// <c>EbayService</c> so the loop's judgement — the matching, the caps, the arm — can be tested
/// with no network and no eBay account.
/// </summary>
public delegate Task<IReadOnlyList<EbayOpportunityItem>> AutoBuyListingSource(
    AutoBuyRule rule, CancellationToken ct);

/// <summary>
/// Actually places the buy, offer, or bid on eBay. The one seam that spends money; wired to
/// <c>EbayService</c>'s PlaceOffer calls in <c>Program.cs</c>, and stubbed in tests so a test can
/// never reach eBay. <paramref name="price"/> is the amount to put on eBay (the listing price for a
/// Buy It Now, the offer, or the max bid) — shipping is not part of it, eBay adds that itself.
/// </summary>
public delegate Task<AutoBuyPlacement> AutoBuyPlacer(
    AutoBuyRule rule, EbayOpportunityItem item, decimal price, CancellationToken ct);

/// <summary>
/// The loop behind eBay Auto-Buy. Wakes once a minute, runs at most one due rule, and — only when
/// the master arm and live buying are both on — spends real money inside the rule's and the day's
/// caps. Everything else it does is write down what it would have done.
/// </summary>
/// <remarks>
/// <para><b>The order of the brakes.</b> Nothing is scanned while the master arm is off. A rule
/// that is off is skipped. A listing already acted on is skipped (the store remembers every one).
/// A listing that fails the rule — all-in price over the ceiling, under the floor, shipping not
/// stated, a missing required word, an excluded word, a thin-feedback seller, an auction that is
/// not ending yet — is skipped, and the reason is counted so the run can say why nothing matched.
/// Only then does a buy get as far as the caps: the rule's remaining budget, the rule's count, the
/// day's count, the global budget. Past all of that, live buying decides whether eBay is actually
/// called or the row is written as a simulation.</para>
/// <para><b>Money is all-in.</b> Every ceiling, budget and ledger figure is item price plus the
/// shipping eBay stated. $5 with $200 freight is a $205 item. A listing with no stated shipping is
/// refused rather than booked as free.</para>
/// <para><b>One at a time.</b> A single process-wide gate; the manual "Run now" takes the same
/// gate, so a button press during a sweep is answered rather than run alongside it.</para>
/// <para><b>No retries.</b> A buy that eBay refuses is recorded and the item is remembered, so the
/// rule does not throw itself at the same failing listing every quarter-hour. When eBay says the
/// application itself may not place offers, the rule is paused with the reason on it.</para>
/// </remarks>
public sealed class AutoBuyService(
    AutoBuyStore store,
    AutoBuyListingSource listingSource,
    AutoBuyPlacer placer,
    ActionLog log) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(50);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Listings looked at per rule per run. A rule buys the first that clears every bar.</summary>
    public const int MaxItemsPerScan = 40;

    /// <summary>
    /// An auction is only bid on when it ends before the rule's next look, plus this margin. Bidding
    /// the moment a listing appears is how a rule advertises its maximum to every other bidder for
    /// six days; bidding inside the last window is how an auction is actually won.
    /// </summary>
    public static readonly TimeSpan AuctionWindowMargin = TimeSpan.FromMinutes(3);

    /// <summary>Applied only when the rule sets a feedback-score floor: a seller under this positive
    /// percentage is refused too. Score without percentage lets a 4,000-feedback, 80%-positive seller through.</summary>
    public const decimal MinPositiveFeedbackPercent = 95m;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Scanning { get; private set; }
    public long? ScanningRuleId { get; private set; }
    public DateTimeOffset? LastScanUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.Add("Auto-Buy", "Scan tick failed", ex.Message); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One wake-up: run the most-overdue enabled rule, if the feature is armed and one is due.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The master arm gates the scan itself, not just the spend: disarmed, the feature reads
        // nothing on the seller's behalf.
        if (!store.GetSettings().Armed) return;

        var due = store.ListRules()
            .Where(r => r.Enabled && !r.IsExhausted)
            .Where(r => r.NextRunUtc is null || r.NextRunUtc <= now)
            .OrderBy(r => r.NextRunUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (due is null) return;

        await RunRuleAsync(due.Id, manual: false, ct);
    }

    /// <summary>
    /// Run one rule now. Manual runs from the console take the same gate and the same path as the
    /// timer, so the button and the loop can never price or cap a thing two different ways.
    /// </summary>
    public async Task<RuleRunReport> RunRuleAsync(long ruleId, bool manual, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new RuleRunReport("busy", "A scan is already running.", 0, 0);

        try
        {
            var rule = store.GetRule(ruleId);
            if (rule is null) return new RuleRunReport("gone", "That rule no longer exists.", 0, 0);

            var settings = store.GetSettings();
            if (!settings.Armed && !manual)
                return new RuleRunReport("disarmed", "Auto-Buy is not armed.", 0, 0);

            Scanning = true;
            ScanningRuleId = ruleId;
            // The report is per run and per rule. It used to be a field on the service, so a run
            // that threw (timeout, cancel) recorded the PREVIOUS rule's status against this one.
            RuleRunReport? report = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RunTimeout);
                report = await ScanAndActAsync(rule, settings, timeout.Token);
                return report;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                report = new RuleRunReport("timed_out", $"The scan took longer than {RunTimeout.TotalMinutes:0} minutes and was stopped.", 0, 0);
                return report;
            }
            finally
            {
                Scanning = false;
                ScanningRuleId = null;
                LastScanUtc = DateTimeOffset.UtcNow;
                store.RecordRun(ruleId, report?.Status ?? "error", DateTimeOffset.UtcNow, rule.IntervalMinutes,
                    report?.Note ?? "The run ended before it could report.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuleRunReport> ScanAndActAsync(AutoBuyRule rule, AutoBuySettings settings, CancellationToken ct)
    {
        IReadOnlyList<EbayOpportunityItem> items;
        try
        {
            items = await listingSource(rule, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Auto-Buy", $"Search failed for \"{rule.Name}\"", ex.Message);
            return new RuleRunReport("search_failed", $"eBay search failed: {ex.Message}", 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var scanned = 0;
        var acted = 0;
        var rejected = new Dictionary<string, int>();

        foreach (var item in items.Take(MaxItemsPerScan))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            if (string.IsNullOrWhiteSpace(item.ItemId)) { Count(rejected, "no item id"); continue; }
            if (store.HasSeen(rule.Id, item.ItemId)) { Count(rejected, "already acted on"); continue; }

            var (ok, reason) = Evaluate(rule, item, now);
            if (!ok) { Count(rejected, reason); continue; }

            var allIn = AllInPrice(rule, item);   // what the seller's money is on the line for
            var onEbay = PriceFor(rule, item);    // what goes on the PlaceOffer call

            // The caps, in the order that spends the least to check. A cap stop is remembered like a
            // buy so the rule does not re-evaluate the same blocked item next tick.
            var capReason = CapReason(rule, settings, allIn);
            if (capReason is not null)
            {
                store.MarkSeen(rule.Id, item.ItemId);
                Record(rule, item, allIn, AutoBuyOutcome.Skipped, capReason);
                // A budget or day cap is not going to clear later in this same run; stop here.
                return new RuleRunReport("capped", Summary(scanned, rejected, $"Stopped by a cap: {capReason}"), scanned, acted);
            }

            // Past every bar. Remember it BEFORE acting, so a crash mid-purchase cannot let the next
            // tick buy the same item a second time.
            store.MarkSeen(rule.Id, item.ItemId);

            if (settings is { Armed: true, LiveBuying: true })
            {
                AutoBuyPlacement result;
                try { result = await placer(rule, item, onEbay, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = new AutoBuyPlacement(false, ex.Message); }

                if (result.NotEnabled)
                {
                    // Not this listing's fault and not the next one's either. Pause the rule with the
                    // reason on it rather than burn every match as a FAILED row, one per tick.
                    var why = $"eBay refused: {result.Detail} This rule is paused until that is fixed.";
                    Record(rule, item, allIn, AutoBuyOutcome.Failed, why);
                    store.PauseRule(rule.Id, why);
                    log.Add("Auto-Buy", $"\"{rule.Name}\" paused — eBay will not let this app place offers", result.Detail);
                    return new RuleRunReport("not_enabled", why, scanned, acted);
                }

                if (result.Ok)
                {
                    var outcome = Settle(rule, item, allIn, onEbay, result);
                    if (outcome.Banked)
                    {
                        store.RecordBuy(rule.Id, allIn);
                        Record(rule, item, allIn, AutoBuyOutcome.Placed, outcome.Detail);
                        log.Add("Auto-Buy", outcome.Headline, $"\"{rule.Name}\": {Trim(item.Title)} — {outcome.Detail}");
                        acted++;
                        // One buy per run per rule. The next due tick can buy the next one; a rule does
                        // not empty its whole budget into one sweep.
                        return new RuleRunReport("placed", Summary(scanned, rejected, outcome.Detail), scanned, acted);
                    }

                    // eBay took the call but the money is not committed (outbid on the spot). Not
                    // banked, not retried on this item; move on within the run.
                    Record(rule, item, allIn, AutoBuyOutcome.Failed, outcome.Detail);
                    log.Add("Auto-Buy", outcome.Headline, $"\"{rule.Name}\": {Trim(item.Title)} — {outcome.Detail}");
                    Count(rejected, "outbid");
                    continue;
                }

                Record(rule, item, allIn, AutoBuyOutcome.Failed, result.Detail);
                log.Add("Auto-Buy", $"Purchase refused for \"{rule.Name}\"", result.Detail);
                Count(rejected, "refused by eBay");
                // A refusal is not retried on the same item; move on within this run.
                continue;
            }

            // Simulate: the match is real, the money is not spent. This is what the feature does
            // every second until the seller turns live buying on, on purpose.
            var would = $"Would {ModeVerb(rule.Mode)} at {onEbay:C2} ({allIn:C2} with shipping). Live buying is off.";
            Record(rule, item, allIn, AutoBuyOutcome.Simulated, would);
            acted++;
            return new RuleRunReport("simulated",
                Summary(scanned, rejected, $"Would buy {Trim(item.Title)} for {allIn:C2} all-in — live buying is off."), scanned, acted);
        }

        return new RuleRunReport("no_match", Summary(scanned, rejected, "Nothing matched this run."), scanned, acted);
    }

    /// <summary>What eBay's acceptance means for THIS mode, and whether budget is committed by it.</summary>
    private static (bool Banked, string Headline, string Detail) Settle(
        AutoBuyRule rule, EbayOpportunityItem item, decimal allIn, decimal onEbay, AutoBuyPlacement result)
    {
        var link = string.IsNullOrWhiteSpace(result.Link) ? "" : $" {result.Link}";
        switch (rule.Mode)
        {
            case AutoBuyMode.BuyItNow:
                // PlaceOffer commits the buyer; it does not pay. Saying "Bought" here is how a seller
                // ends up with an unpaid-item strike they never knew was coming.
                return (true, $"Committed to buy for {allIn:C2}",
                    $"Committed on eBay at {onEbay:C2} ({allIn:C2} with shipping). Pay on eBay to complete it.{link}");
            case AutoBuyMode.AuctionBid:
                return result.HighBidder
                    ? (true, $"High bidder at up to {onEbay:C2}",
                        $"Max bid {onEbay:C2} placed and currently winning ({allIn:C2} with shipping). If you are outbid later the budget is NOT released — check the auction.{link}")
                    : (false, "Outbid on placement",
                        $"Max bid {onEbay:C2} was placed but someone already stood higher. {result.Detail}".Trim());
            default:
                return (true, $"Offer sent for {onEbay:C2}",
                    $"Best Offer of {onEbay:C2} sent ({allIn:C2} with shipping). If the seller accepts, pay on eBay; the budget is held meanwhile.{link}");
        }
    }

    private void Record(AutoBuyRule rule, EbayOpportunityItem item, decimal price, AutoBuyOutcome outcome, string detail) =>
        store.RecordExecution(new AutoBuyExecution
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ItemId = item.ItemId,
            Title = item.Title,
            Url = item.Url,
            Mode = rule.Mode,
            Price = price,
            Outcome = outcome,
            Detail = detail,
        });

    private static void Count(Dictionary<string, int> tally, string reason)
    {
        // Reasons carry the listing's own numbers ("$61.00 over..."); fold them to one bucket each.
        var key = reason.Split(':')[0].Trim();
        tally[key] = tally.GetValueOrDefault(key) + 1;
    }

    /// <summary>"40 scanned: 22 over the ceiling, 9 shipping not stated, 6 excluded word. Nothing matched."</summary>
    public static string Summary(int scanned, Dictionary<string, int> rejected, string tail)
    {
        if (scanned == 0) return $"0 listings returned. {tail}";
        var parts = rejected.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}");
        var why = rejected.Count > 0 ? ": " + string.Join(", ", parts) : "";
        return $"{scanned} scanned{why}. {tail}";
    }

    // ── The pure decisions, kept static so a test needs no service ────────────

    /// <summary>
    /// Does this listing pass the rule? Returns the reason it did not, for the run summary. The
    /// reason's text before the first colon is its bucket, so "over the ceiling: $61.00 vs $60.00"
    /// tallies with every other over-the-ceiling row.
    /// </summary>
    public static (bool Ok, string Reason) Evaluate(AutoBuyRule rule, EbayOpportunityItem item, DateTimeOffset? now = null)
    {
        if (item.ItemId.Contains('|'))
            return (false, "unusable item id: eBay gave only the REST form, which PlaceOffer cannot buy");
        if (item.Price <= 0m) return (false, "no price");
        if (rule.MinItemPrice > 0m && item.Price < rule.MinItemPrice)
            return (false, $"below the floor: {item.Price:C2} is under the {rule.MinItemPrice:C2} floor");

        // Shipping is money. A listing that will not say what it costs to ship is not booked as free;
        // it is refused, and the summary says so, so the seller can decide whether to widen the rule.
        if (!item.ShippingStated)
            return (false, "shipping not stated");

        var ceiling = rule.MaxItemPrice;
        if (rule.Mode == AutoBuyMode.BuyItNow)
        {
            var allIn = item.Price + item.ShippingCost;
            if (allIn > ceiling)
                return (false, $"over the ceiling: {allIn:C2} with shipping, ceiling {ceiling:C2}");
        }
        else
        {
            // For an offer or a bid, the amount to commit plus shipping must sit at or under the
            // ceiling; the store's validation guarantees the bare amount at save time, and this
            // holds it — shipping included — at act time too.
            if (rule.OfferOrBidPrice <= 0m)
                return (false, "no offer or bid amount");
            if (rule.OfferOrBidPrice + item.ShippingCost > ceiling)
                return (false, $"over the ceiling: {rule.OfferOrBidPrice:C2} plus {item.ShippingCost:C2} shipping, ceiling {ceiling:C2}");
            if (rule.Mode == AutoBuyMode.AuctionBid && item.Price >= rule.OfferOrBidPrice)
                return (false, $"current bid too high: already {item.Price:C2}, your max is {rule.OfferOrBidPrice:C2}");
        }

        if (rule.Mode == AutoBuyMode.AuctionBid && item.EndDate is { } ends)
        {
            var clock = now ?? DateTimeOffset.UtcNow;
            var window = TimeSpan.FromMinutes(Math.Clamp(rule.IntervalMinutes, 5, 24 * 60)) + AuctionWindowMargin;
            var left = ends - clock.UtcDateTime;
            if (left > window)
                return (false, $"not ending yet: {Describe(left)} left, bids go in inside the last {window.TotalMinutes:0} minutes");
        }

        if (rule.MinSellerFeedback > 0)
        {
            if (item.SellerFeedbackScore < rule.MinSellerFeedback)
                return (false, $"seller feedback too low: {item.SellerFeedbackScore} is under {rule.MinSellerFeedback}");
            if (item.SellerFeedbackPercent is { } pct && pct < MinPositiveFeedbackPercent)
                return (false, $"seller positive rating too low: {pct:0.#}% is under {MinPositiveFeedbackPercent:0}%");
        }

        foreach (var word in Words(rule.RequiredKeywords))
            if (!item.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                return (false, $"missing required word: \"{word}\"");

        foreach (var word in Words(rule.ExcludeKeywords))
            if (item.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                return (false, $"excluded word: \"{word}\"");

        return (true, "");
    }

    /// <summary>What goes on the PlaceOffer call: the listing price to buy it now, else the offer or max bid.</summary>
    public static decimal PriceFor(AutoBuyRule rule, EbayOpportunityItem item) =>
        rule.Mode == AutoBuyMode.BuyItNow ? item.Price : rule.OfferOrBidPrice;

    /// <summary>What the seller's money is on the line for: <see cref="PriceFor"/> plus the stated shipping.</summary>
    public static decimal AllInPrice(AutoBuyRule rule, EbayOpportunityItem item) =>
        PriceFor(rule, item) + Math.Max(0m, item.ShippingCost);

    /// <summary>The first cap this buy would breach, or null if it clears them all. <paramref name="price"/> is all-in.</summary>
    public static string? CapReason(AutoBuyRule rule, AutoBuySettings settings, decimal price)
    {
        if (rule.MaxBuys > 0 && rule.Buys >= rule.MaxBuys) return "this rule has hit its buy count.";
        if (price > rule.RemainingBudget) return $"this rule has {rule.RemainingBudget:C2} of budget left, less than {price:C2}.";
        if (settings.MaxBuysPerDay > 0 && settings.BuysToday >= settings.MaxBuysPerDay) return "the day's buy limit is reached.";
        if (price > settings.GlobalRemainingBudget) return $"the global budget has {settings.GlobalRemainingBudget:C2} left, less than {price:C2}.";
        return null;
    }

    public static IEnumerable<string> Words(string raw) =>
        (raw ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Kept for callers that predate <see cref="Words"/>.</summary>
    public static IEnumerable<string> ExcludeWords(string raw) => Words(raw);

    public static string ModeVerb(AutoBuyMode mode) => mode switch
    {
        AutoBuyMode.BestOffer => "offer",
        AutoBuyMode.AuctionBid => "bid",
        _ => "buy",
    };

    private static string Trim(string title) => title.Length <= 60 ? title : title[..57] + "…";

    private static string Describe(TimeSpan left) =>
        left.TotalHours >= 48 ? $"{left.TotalDays:0.#} days"
        : left.TotalMinutes >= 90 ? $"{left.TotalHours:0.#} hours"
        : $"{left.TotalMinutes:0} minutes";

    /// <summary>Builds the one-line safety summary the console shows above everything.</summary>
    public static string SafetyLine(AutoBuySettings s)
    {
        if (!s.Armed) return "Disarmed. Nothing is scanned and nothing is bought.";
        if (!s.LiveBuying) return "Armed, simulating. Matches are recorded but nothing is bought.";
        return $"LIVE. Real purchases, up to {s.GlobalRemainingBudget:C2} of budget and {Math.Max(0, s.MaxBuysPerDay - s.BuysToday)} buys left today.";
    }

    public AutoBuyStatus BuildStatus()
    {
        var settings = store.GetSettings();
        return new AutoBuyStatus(
            settings,
            store.ListRules(),
            store.ListRecent(40),
            Scanning,
            ScanningRuleId,
            LastScanUtc,
            SafetyLine(settings));
    }
}

/// <summary>What one run of one rule did, for the "Run now" button and the log.</summary>
public sealed record RuleRunReport(string Status, string Note, int Scanned, int Acted);
