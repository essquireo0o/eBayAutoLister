using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>What one placement attempt came back with. Ok true means eBay accepted it.</summary>
public sealed record AutoBuyPlacement(bool Ok, string Detail);

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
/// never reach eBay.
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
/// A listing that fails the rule — price over the ceiling, an excluded word, a thin-feedback
/// seller — is skipped. Only then does a buy get as far as the caps: the rule's remaining budget,
/// the rule's count, the day's count, the global budget. Past all of that, live buying decides
/// whether eBay is actually called or the row is written as a simulation.</para>
/// <para><b>One at a time.</b> A single process-wide gate; the manual "Run now" takes the same
/// gate, so a button press during a sweep is answered rather than run alongside it.</para>
/// <para><b>No retries.</b> A buy that eBay refuses is recorded and the item is remembered, so the
/// rule does not throw itself at the same failing listing every quarter-hour.</para>
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
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RunTimeout);
                return await ScanAndActAsync(rule, settings, manual, timeout.Token);
            }
            finally
            {
                Scanning = false;
                ScanningRuleId = null;
                LastScanUtc = DateTimeOffset.UtcNow;
                store.RecordRun(ruleId, _lastStatus, DateTimeOffset.UtcNow, rule.IntervalMinutes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string _lastStatus = "never_run";

    private async Task<RuleRunReport> ScanAndActAsync(
        AutoBuyRule rule, AutoBuySettings settings, bool manual, CancellationToken ct)
    {
        IReadOnlyList<EbayOpportunityItem> items;
        try
        {
            items = await listingSource(rule, ct);
        }
        catch (Exception ex)
        {
            _lastStatus = "search_failed";
            log.Add("Auto-Buy", $"Search failed for \"{rule.Name}\"", ex.Message);
            return new RuleRunReport("search_failed", ex.Message, 0, 0);
        }

        var scanned = 0;
        var acted = 0;

        foreach (var item in items.Take(MaxItemsPerScan))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;

            if (string.IsNullOrWhiteSpace(item.ItemId)) continue;
            if (store.HasSeen(rule.Id, item.ItemId)) continue;

            var (ok, _) = Evaluate(rule, item);
            if (!ok) continue;

            var price = PriceFor(rule, item);

            // The caps, in the order that spends the least to check. A cap stop is remembered like a
            // buy so the rule does not re-evaluate the same blocked item next tick.
            var capReason = CapReason(rule, settings, price);
            if (capReason is not null)
            {
                store.MarkSeen(rule.Id, item.ItemId);
                Record(rule, item, price, AutoBuyOutcome.Skipped, capReason);
                _lastStatus = "capped";
                // A budget or day cap is not going to clear later in this same run; stop here.
                return new RuleRunReport("capped", capReason, scanned, acted);
            }

            // Past every bar. Remember it BEFORE acting, so a crash mid-purchase cannot let the next
            // tick buy the same item a second time.
            store.MarkSeen(rule.Id, item.ItemId);

            if (settings is { Armed: true, LiveBuying: true })
            {
                AutoBuyPlacement result;
                try { result = await placer(rule, item, price, ct); }
                catch (Exception ex) { result = new AutoBuyPlacement(false, ex.Message); }

                if (result.Ok)
                {
                    store.RecordBuy(rule.Id, price);
                    Record(rule, item, price, AutoBuyOutcome.Placed, result.Detail);
                    log.Add("Auto-Buy", $"Bought for {price:C2}",
                        $"\"{rule.Name}\" {ModeVerb(rule.Mode)} {Trim(item.Title)} at {price:C2}.");
                    acted++;
                    _lastStatus = "bought";
                    // One buy per run per rule. The next due tick can buy the next one; a rule does
                    // not empty its whole budget into one sweep.
                    return new RuleRunReport("bought", $"Bought {Trim(item.Title)} for {price:C2}.", scanned, acted);
                }

                Record(rule, item, price, AutoBuyOutcome.Failed, result.Detail);
                log.Add("Auto-Buy", $"Purchase refused for \"{rule.Name}\"", result.Detail);
                _lastStatus = "buy_failed";
                // A refusal is not retried on the same item; move on within this run.
                continue;
            }

            // Simulate: the match is real, the money is not spent. This is what the feature does
            // every second until the seller turns live buying on, on purpose.
            Record(rule, item, price, AutoBuyOutcome.Simulated,
                $"Would {ModeVerb(rule.Mode)} at {price:C2}. Live buying is off.");
            acted++;
            _lastStatus = "simulated";
            return new RuleRunReport("simulated",
                $"Would buy {Trim(item.Title)} for {price:C2} — live buying is off.", scanned, acted);
        }

        _lastStatus = acted > 0 ? "acted" : "no_match";
        return new RuleRunReport(_lastStatus, acted > 0 ? "Acted on a listing." : "Nothing matched this run.", scanned, acted);
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

    // ── The pure decisions, kept static so a test needs no service ────────────

    /// <summary>
    /// Does this listing pass the rule? Returns the reason it did not, for the ledger. Price, the
    /// excluded words, the seller floor, and the mode's own shape are all checked here.
    /// </summary>
    public static (bool Ok, string Reason) Evaluate(AutoBuyRule rule, EbayOpportunityItem item)
    {
        if (item.Price <= 0m) return (false, "no price");
        if (item.Price > rule.MaxItemPrice) return (false, $"priced {item.Price:C2}, over the {rule.MaxItemPrice:C2} ceiling");

        if (rule.MinSellerFeedback > 0 && item.SellerFeedbackScore < rule.MinSellerFeedback)
            return (false, $"seller feedback {item.SellerFeedbackScore} is under {rule.MinSellerFeedback}");

        foreach (var word in ExcludeWords(rule.ExcludeKeywords))
            if (item.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                return (false, $"title contains excluded word \"{word}\"");

        // For an offer or a bid, the amount to commit must sit at or under the ceiling; the store's
        // validation guarantees it at save time, and this holds it at act time too.
        if (rule.Mode is AutoBuyMode.BestOffer or AutoBuyMode.AuctionBid && rule.OfferOrBidPrice > rule.MaxItemPrice)
            return (false, "offer or bid is above the ceiling");

        return (true, "");
    }

    /// <summary>What this rule would commit on this item: the listing price to buy it now, else the offer or bid.</summary>
    public static decimal PriceFor(AutoBuyRule rule, EbayOpportunityItem item) =>
        rule.Mode == AutoBuyMode.BuyItNow ? item.Price : rule.OfferOrBidPrice;

    /// <summary>The first cap this buy would breach, or null if it clears them all.</summary>
    public static string? CapReason(AutoBuyRule rule, AutoBuySettings settings, decimal price)
    {
        if (rule.MaxBuys > 0 && rule.Buys >= rule.MaxBuys) return "this rule has hit its buy count.";
        if (price > rule.RemainingBudget) return $"this rule has {rule.RemainingBudget:C2} of budget left, less than {price:C2}.";
        if (settings.MaxBuysPerDay > 0 && settings.BuysToday >= settings.MaxBuysPerDay) return "the day's buy limit is reached.";
        if (price > settings.GlobalRemainingBudget) return $"the global budget has {settings.GlobalRemainingBudget:C2} left, less than {price:C2}.";
        return null;
    }

    public static IEnumerable<string> ExcludeWords(string raw) =>
        (raw ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string ModeVerb(AutoBuyMode mode) => mode switch
    {
        AutoBuyMode.BestOffer => "offer",
        AutoBuyMode.AuctionBid => "bid",
        _ => "buy",
    };

    private static string Trim(string title) => title.Length <= 60 ? title : title[..57] + "…";

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
