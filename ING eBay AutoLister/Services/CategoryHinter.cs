using System.Collections.Concurrent;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Answers "which eBay category does this go in?" for a listing that has not picked one, from the
/// seller's own history first and eBay's taxonomy second.
/// </summary>
/// <remarks>
/// <para>
/// The order matters and is not negotiable. The seller's history is a record of decisions eBay
/// accepted for items this seller actually sells; eBay's suggestion is a guess made from a string
/// by something that has never seen their inventory. Where the two disagree on a title the seller
/// has listed before, the seller is right.
/// </para>
/// <para>
/// The history lookup is local and costs a SQLite read, so it runs on every pass of the readiness
/// check. The eBay lookup is a network call on a check that fires while the seller is typing, so
/// it is asked once per distinct title and then answered from memory for
/// <see cref="CacheTtl"/> — and it is skipped entirely whenever history already had an answer.
/// </para>
/// <para>
/// Never throws. A category hint is a convenience; a readiness check that fails because a
/// convenience could not be computed has got in the way of the listing it exists to help.
/// </para>
/// </remarks>
public sealed class CategoryHinter(CategoryMemoryStore memory, EbayService ebay, ActionLog log)
{
    /// <summary>How long eBay's answer for one title is reused. Categories do not move hourly, and
    /// the check that asks for this fires every time the seller stops typing.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>Distinct titles cached before the oldest are dropped.</summary>
    public const int MaxCachedTitles = 200;

    /// <summary>Below this many significant words, a title is too vague to ask eBay about — and
    /// the seller is probably still typing it.</summary>
    public const int MinTokensForEbayLookup = 2;

    /// <summary>
    /// Shortest gap between two taxonomy calls, whatever the titles were.
    /// </summary>
    /// <remarks>
    /// The per-title cache alone does not hold this down: a title being typed is a different title
    /// every time the seller pauses, so a fresh key — and a fresh call — arrives every 600ms all
    /// the way to the end of the sentence. This turns that into one call per pause in the typing.
    /// Skipping is free: the check runs again on the next edit, and by then the title is closer to
    /// what the seller meant.
    /// </remarks>
    public static readonly TimeSpan LookupCooldown = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, CategoryMatch? Match)> _ebayCache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastLookup = DateTimeOffset.MinValue;

    /// <summary>
    /// The category to offer for this title, or null when nothing can be said with confidence.
    /// </summary>
    /// <param name="title">The listing title as written so far.</param>
    /// <param name="askEbay">False on passes that must not touch the network.</param>
    public async Task<CategoryMatch?> SuggestAsync(string? title, bool askEbay = true)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        try
        {
            var fromHistory = CategorySuggester.FromHistory(title, memory.Recent());
            if (fromHistory is not null) return fromHistory;
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Category memory lookup failed", ex.Message);
        }

        if (!askEbay) return null;
        if (CategorySuggester.Tokens(title).Count < MinTokensForEbayLookup) return null;

        var key = CategorySuggester.Key(title);
        var now = DateTimeOffset.UtcNow;
        if (_ebayCache.TryGetValue(key, out var cached) && now - cached.At < CacheTtl)
            return cached.Match;

        if (now - _lastLookup < LookupCooldown) return null;
        _lastLookup = now;

        CategoryMatch? fromEbay = null;
        try
        {
            var suggestions = await ebay.GetCategorySuggestionsAsync(title);
            var best = suggestions.FirstOrDefault();
            if (best is not null) fromEbay = CategorySuggester.FromEbay(best.Id, best.Name, best.Breadcrumb);
        }
        catch (Exception ex)
        {
            // Not connected, rate limited, offline — all the same answer here: no hint. Cached as
            // a miss so a disconnected app does not re-attempt the call on every keystroke pause.
            log.Add("Info", "Category suggestion unavailable", ex.Message);
        }

        Cache(key, fromEbay);
        return fromEbay;
    }

    private void Cache(string key, CategoryMatch? match)
    {
        if (_ebayCache.Count >= MaxCachedTitles)
        {
            // Cheapest possible eviction: drop the whole cache rather than carry an LRU for a
            // convenience lookup. At worst the next few titles pay for one taxonomy call each.
            _ebayCache.Clear();
        }
        _ebayCache[key] = (DateTimeOffset.UtcNow, match);
    }
}
