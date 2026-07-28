using System.Collections.Concurrent;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Rewrites every listing in the account for search, in the background, as eBay drafts.
/// </summary>
/// <remarks>
/// <para>
/// A whole account is the point of this feature, and a whole account cannot be done inside one
/// HTTP request: each listing costs a Claude call plus two eBay calls, so eighty-odd listings runs
/// for minutes. A request that long dies in the browser and takes the completed work with it — the
/// seller would pay for every rewrite and receive none of them.
/// </para>
/// <para>
/// So the work is started once and polled. Progress is readable while it runs, every listing's
/// outcome is recorded separately, and one failure never abandons the rest. Only one run at a time:
/// a second Start while a run is live returns the run already going rather than paying twice for
/// the same listings.
/// </para>
/// <para>
/// It writes <em>drafts</em>. Nothing live is revised, whatever the model returns.
/// </para>
/// </remarks>
public sealed class CopilotSeoJob(EbayService ebay, ClaudeService claude, DraftStore drafts, ActionLog log)
{
    private readonly object _gate = new();
    private CopilotSeoRun? _run;

    /// <summary>
    /// Set once eBay refuses to make drafts, so the rest of the run does not pay to be refused
    /// eighty more times.
    /// </summary>
    /// <remarks>
    /// eBay's <c>sell/listing/v1_beta/item_draft</c> is a Limited Release API and answers 404 with
    /// an empty body for an account that was never approved for it — measured, not assumed. That is
    /// not a reason to throw the rewrite away: the app has its own draft store and its own publish
    /// path, so the work lands there instead and the seller still reviews and publishes it. Losing a
    /// paid-for rewrite because someone else's API is gated would be the worst outcome available.
    /// </remarks>
    private bool _ebayDraftsUnavailable;

    /// <summary>The run in progress or the last one finished. Null until one has been started.</summary>
    public CopilotSeoRun? Current => _run;

    /// <summary>
    /// Starts a rewrite over <paramref name="listingIds"/>, or over every live listing when that is
    /// empty. Returns the run — already-running when one is live, so a double click cannot start
    /// two sweeps of the same account.
    /// </summary>
    public CopilotSeoRun Start(IReadOnlyList<string>? listingIds)
    {
        lock (_gate)
        {
            if (_run is { Finished: false }) return _run;
            _run = new CopilotSeoRun { StartedAt = DateTimeOffset.UtcNow };
        }

        var run = _run;
        _ = Task.Run(() => RunAsync(run, listingIds));
        return run;
    }

    private async Task RunAsync(CopilotSeoRun run, IReadOnlyList<string>? listingIds)
    {
        try
        {
            var ids = (listingIds ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

            if (ids.Count == 0)
            {
                run.Stage = "Reading your listings from eBay";
                ids = (await ebay.GetListingsAsync())
                    .Select(l => l.ListingId)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();
            }

            run.Total = ids.Count;
            run.Stage = ids.Count == 0 ? "No listings found" : "Rewriting";

            foreach (var id in ids)
            {
                if (run.CancelRequested) { run.Stage = "Stopped"; break; }

                try
                {
                    var current = await ebay.GetItemAsync(id);
                    var before = current.Title ?? "";

                    var improved = await claude.ImproveSeoAsync(
                        System.Text.Json.JsonSerializer.Deserialize<ImproveSeoRequest>(
                            System.Text.Json.JsonSerializer.Serialize(current))!);

                    // eBay rejects an over-length title outright, so the deterministic trim runs on
                    // top of the model's answer rather than trusting it to have counted to 80.
                    improved.Title = ListingCopilot.TidyTitle(improved.Title);

                    if (string.IsNullOrWhiteSpace(improved.Title))
                    {
                        run.Failed++;
                        run.Add(new CopilotSeoResult(id, false, before, "", null, "The rewrite came back empty."));
                        continue;
                    }

                    if (!LooksLikeHtml(improved.Description))
                    {
                        run.Failed++;
                        run.Add(new CopilotSeoResult(id, false, before, improved.Title, null,
                            "The description came back as plain text, not HTML — skipped rather than "
                            + "drafting an unformatted listing."));
                        continue;
                    }

                    // A rewrite that changes nothing is not worth a draft the seller has to review.
                    // Compared across the WHOLE listing, not the title: the pass rewrites the
                    // description and fills item specifics too, and a listing whose title was
                    // already good is exactly the one whose description most often is not. Judging
                    // on the title alone would throw away most of what was paid for.
                    if (!ContentChanged(current, improved))
                    {
                        run.Skipped++;
                        run.Add(new CopilotSeoResult(id, true, before, improved.Title, null,
                            "Already as good as the rewrite would make it — no draft made."));
                        continue;
                    }

                    var draftReq = System.Text.Json.JsonSerializer.Deserialize<PostListingRequest>(
                        System.Text.Json.JsonSerializer.Serialize(improved))!;

                    string? url = null;
                    string? where = null;

                    if (!_ebayDraftsUnavailable)
                    {
                        try
                        {
                            url = (await ebay.CreateSellerHubDraftAsync(draftReq)).SellerHubUrl;
                        }
                        catch (Exception ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
                        {
                            _ebayDraftsUnavailable = true;
                            log.Add("Info", "eBay drafts unavailable",
                                "sell/listing item_draft answered 404 — this account is not approved for it. "
                                + "Saving the rewrites as app drafts instead; nothing is lost.");
                        }
                    }

                    if (url is null)
                    {
                        drafts.SaveDraft(new DraftFile
                        {
                            Title = improved.Title,
                            SavedAt = DateTimeOffset.UtcNow.ToString("o"),
                            Data = draftReq,
                        });
                        where = "Saved as an app draft — eBay's draft API is not enabled for this account.";
                    }

                    run.Drafted++;
                    run.Add(new CopilotSeoResult(id, true, before, improved.Title, url, where));
                }
                catch (Exception ex)
                {
                    var reason = ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message;
                    run.Failed++;
                    run.Add(new CopilotSeoResult(id, false, "", "", null, reason));
                    log.Add("Warning", "Copilot SEO rewrite failed", $"{id}: {reason}");
                }
            }

            if (!run.CancelRequested) run.Stage = "Finished";
        }
        catch (Exception ex)
        {
            run.Stage = "Failed";
            run.Error = ex.Message;
            log.Add("Error", "Copilot SEO run failed", ex.Message);
        }
        finally
        {
            run.Finished = true;
            run.FinishedAt = DateTimeOffset.UtcNow;
            log.Add("Info", "Copilot SEO run finished",
                $"{run.Drafted} draft(s), {run.Skipped} already good, {run.Failed} failed, of {run.Total}. Nothing live was changed.");
        }
    }

    /// <summary>Asks the run to stop after the listing it is on. Work already done is kept.</summary>
    public void Cancel()
    {
        if (_run is { Finished: false } r) r.CancelRequested = true;
    }

    /// <summary>
    /// Did the rewrite actually change the listing? Compared across everything the pass is asked to
    /// improve, not just the title.
    /// </summary>
    private static bool ContentChanged(ListingData before, ListingData after)
    {
        static string N(string? s) => (s ?? "").Trim();

        if (!string.Equals(N(before.Title), N(after.Title), StringComparison.Ordinal)) return true;
        if (!string.Equals(N(before.Subtitle), N(after.Subtitle), StringComparison.Ordinal)) return true;
        if (!string.Equals(N(before.Description), N(after.Description), StringComparison.Ordinal)) return true;

        // Item specifics are the ranking field sellers most often leave half-empty, so a pass that
        // only filled those in is still worth a draft.
        var b = before.ItemSpecifics ?? [];
        var a = after.ItemSpecifics ?? [];
        if (b.Count != a.Count) return true;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(N(v), N(kv.Value), StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// The description has to reach eBay as real HTML. The prompt is explicit about it, but a model
    /// that answers with plain text would otherwise be drafted as-is and the seller would find a wall
    /// of unformatted text on every listing — the exact opposite of what this feature is for. Cheaper
    /// to catch here than to review eighty drafts by hand.
    /// </summary>
    private static bool LooksLikeHtml(string? description)
    {
        var d = (description ?? "").Trim();
        if (d.Length == 0) return false;
        return d.Contains("</", StringComparison.Ordinal)
            && (d.Contains("<div", StringComparison.OrdinalIgnoreCase)
             || d.Contains("<p", StringComparison.OrdinalIgnoreCase)
             || d.Contains("<ul", StringComparison.OrdinalIgnoreCase)
             || d.Contains("<h2", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One rewrite sweep, readable while it runs.</summary>
public sealed class CopilotSeoRun
{
    private readonly ConcurrentQueue<CopilotSeoResult> _results = new();

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Stage { get; set; } = "Starting";
    public int Total { get; set; }
    public int Drafted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Finished { get; set; }
    public bool CancelRequested { get; set; }
    public string? Error { get; set; }

    public int Done => Drafted + Skipped + Failed;
    public IReadOnlyCollection<CopilotSeoResult> Results => _results;

    internal void Add(CopilotSeoResult r) => _results.Enqueue(r);
}

/// <param name="Note">Why no draft was made, when the rewrite succeeded but nothing changed.</param>
public sealed record CopilotSeoResult(
    string ListingId, bool Ok, string Before, string After, string? SellerHubUrl, string? Note);
