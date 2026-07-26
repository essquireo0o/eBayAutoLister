using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>What a publish attempt should do, decided before anything is sent to eBay.</summary>
public enum PublishDecision
{
    /// <summary>Nothing like this has been published — go ahead.</summary>
    Proceed,

    /// <summary>An identical publish is running right now. Wait for it rather than sending a second.</summary>
    AlreadyRunning,

    /// <summary>This exact listing went live moments ago. Show that listing instead of making another.</summary>
    AlreadyPublished,
}

public sealed record PublishVerdict(PublishDecision Decision, string ListingId = "", string Detail = "");

/// <summary>
/// Stops one item becoming two live eBay listings.
/// </summary>
/// <remarks>
/// <para>
/// Publishing is the only action in this app that spends the seller's money in a way that cannot be
/// undone with a click, and it had the classic double-submit hole. <c>AddFixedPriceItem</c> is not
/// idempotent, and a failure reported to the browser does not mean eBay didn't create the listing —
/// a request that times out, or whose response is lost on the way back, leaves an item live that the
/// app believes failed. The seller then does the obvious thing and presses Publish again, and now
/// there are two listings for one physical item: two insertion fees, two sets of buyers, and an
/// oversell as soon as one sells.
/// </para>
/// <para>
/// Three independent brakes, because each covers a case the others miss:
/// </para>
/// <list type="number">
/// <item>An in-flight lease on the content fingerprint — catches the double-click and the impatient
/// second attempt while the first is still running, without any storage round trip.</item>
/// <item>A recent-publish record in <see cref="WorkRecoveryStore"/> — catches a retry after the
/// first attempt already succeeded, including one made after the app restarted.</item>
/// <item>Reconciliation against the account's live listings — catches the case neither of the above
/// can know about, where eBay created the listing but the app never received the ID.</item>
/// </list>
/// <para>
/// The lease is deliberately short-lived and self-releasing. A lock that outlives a crashed request
/// would block publishing entirely, which is a worse failure than the one being prevented.
/// </para>
/// </remarks>
public sealed class PublishGuard(WorkRecoveryStore store, ActionLog log)
{
    /// <summary>
    /// A publish that has not reported back within this window is assumed dead and its lease is
    /// reclaimed. Longer than the eBay write timeout so a slow-but-live request keeps its lease.
    /// </summary>
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How recently an identical publish must have succeeded for a repeat to be treated as a
    /// duplicate rather than a deliberate second listing.
    /// </summary>
    /// <remarks>
    /// Sellers do legitimately list two of the same item. Half an hour is long enough to cover
    /// "the button did nothing, let me try again" and short enough not to block restocking later
    /// the same day. Past the window the seller's intent is taken at face value.
    /// </remarks>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _inFlight = new();

    /// <summary>
    /// A stable identity for "this listing, as it stands now".
    /// </summary>
    /// <remarks>
    /// Only the fields that make it a different listing are included. Photo URLs are not: the
    /// publish path uploads photos to eBay's servers first, so the same listing legitimately carries
    /// different URLs on a second attempt — folding them in would make every retry look like new
    /// content and defeat the whole guard. Price and quantity are in, because changing either is a
    /// real decision to list something different.
    /// </remarks>
    public static string Fingerprint(PostListingRequest req)
    {
        var parts = string.Join("",
        [
            (req.Title ?? "").Trim(),
            (req.CategoryId ?? "").Trim(),
            (req.Condition ?? "").Trim(),
            req.Price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            req.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (req.Mpn ?? "").Trim(),
            (req.Upc ?? "").Trim(),
            (req.ListingFormat ?? "").Trim(),
        ]);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts)))[..32];
    }

    /// <summary>
    /// Decides whether this publish should be sent, and claims the lease when it should.
    /// </summary>
    public PublishVerdict Begin(string fingerprint, string? workKey)
    {
        var now = DateTimeOffset.UtcNow;

        var alreadyPublished = store.FindPublished(fingerprint, DuplicateWindow);
        if (alreadyPublished is not null)
        {
            log.Add("Warning", "Publish blocked as a duplicate",
                $"Fingerprint {fingerprint} was published as listing {alreadyPublished.ListingId} "
              + $"at {alreadyPublished.UpdatedUtc:u} — a second live listing was not created.");
            return new PublishVerdict(PublishDecision.AlreadyPublished, alreadyPublished.ListingId,
                "This exact listing already went live moments ago.");
        }

        // AddOrUpdate rather than TryAdd so a lease left behind by a crashed request is reclaimed
        // instead of blocking this fingerprint forever.
        var claimed = true;
        _inFlight.AddOrUpdate(fingerprint,
            _ => now,
            (_, existing) =>
            {
                if (now - existing < LeaseTimeout) { claimed = false; return existing; }
                return now;
            });

        if (!claimed)
        {
            log.Add("Warning", "Publish already in progress", $"Fingerprint {fingerprint}");
            return new PublishVerdict(PublishDecision.AlreadyRunning, "",
                "This listing is already being published — that request is still running.");
        }

        if (!string.IsNullOrWhiteSpace(workKey)) store.MarkPublishing(workKey!, fingerprint);
        return new PublishVerdict(PublishDecision.Proceed);
    }

    public void Succeeded(string fingerprint, string? workKey, string listingId)
    {
        _inFlight.TryRemove(fingerprint, out _);
        store.RecordPublished(workKey, fingerprint, listingId);
    }

    public void Failed(string fingerprint, string? workKey, string error)
    {
        _inFlight.TryRemove(fingerprint, out _);
        if (!string.IsNullOrWhiteSpace(workKey)) store.MarkFailed(workKey!, error);
    }

    /// <summary>
    /// Answers "did it actually go live?" after a failure that cannot say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever called after a transport-level failure — a timeout or a dropped connection — never
    /// after eBay explicitly rejected the listing. An eBay rejection is a definite no; there is
    /// nothing to reconcile, and looking anyway would invite a false match on some earlier listing.
    /// </para>
    /// <para>
    /// Matching is on exact trimmed title against the account's active listings. Titles are the one
    /// field that survives the whole publish path unchanged and are what a seller would themselves
    /// look for. A match older than the window is ignored: a seller relisting an item they sold last
    /// month must not be told their new listing already exists.
    /// </para>
    /// </remarks>
    public static EbayListingSummary? MatchPublished(
        IEnumerable<EbayListingSummary> active, string? title, DateTimeOffset now, TimeSpan window)
    {
        var wanted = (title ?? "").Trim();
        if (wanted.Length == 0) return null;

        return active
            .Where(l => !string.IsNullOrWhiteSpace(l.ListingId))
            .Where(l => string.Equals((l.Title ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .Where(l => l.StartTimeUtc is null
                     || now - new DateTimeOffset(l.StartTimeUtc.Value, TimeSpan.Zero) <= window)
            .OrderByDescending(l => l.StartTimeUtc ?? DateTime.MinValue)
            .FirstOrDefault();
    }
}
