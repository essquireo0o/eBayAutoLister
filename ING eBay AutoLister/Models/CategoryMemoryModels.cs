namespace ING_eBay_AutoLister.Models;

// ── What the seller has listed, and where they put it ─────────────────────────────────────────
//
// Category is the one blocker on the pre-publish checklist the app could never answer. Brand comes
// out of the title, the ZIP comes out of Settings, the box comes out of the estimator — the
// category came out of the seller typing into a search box and picking off a dropdown, every
// single time, including the fortieth time they listed the same model of miner.
//
// It is also the most expensive field on the form to leave empty, because nothing else can be
// checked until it is filled: eBay's required Item Specifics are defined per category, so a blank
// category means the readiness check cannot say what the listing needs either.
//
// These are the shapes that let the app answer it: one row per (title, category) the seller has
// actually published under, and one ranked answer for a title they are writing now.

/// <summary>One category the seller has published a listing under, and the title they used it for.</summary>
public sealed class CategoryUse
{
    public string CategoryId { get; set; } = "";

    /// <summary>eBay's display name, as it was shown when the seller chose it. May be blank on
    /// rows recorded before the name was known — the ID is what publishes.</summary>
    public string CategoryName { get; set; } = "";

    /// <summary>The listing title this category was used for. The whole matching signal.</summary>
    public string Title { get; set; } = "";

    /// <summary>How many times this exact pairing has been published.</summary>
    public int UseCount { get; set; } = 1;

    public DateTimeOffset LastUsedUtc { get; set; }
}

/// <summary>The app's answer to "where does this one go?", with what it is based on.</summary>
public sealed class CategoryMatch
{
    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";

    /// <summary>high | medium — the same scale every other suggestion uses. A category the app is
    /// not confident about is not returned at all: a wrong category is not a small mistake, it is
    /// the listing appearing in the wrong searches and carrying the wrong required specifics.</summary>
    public string Confidence { get; set; } = "";

    /// <summary>Where it came from, in plain words, for the button that accepts it.</summary>
    public string Source { get; set; } = "";

    /// <summary>0–1 overlap between this title and the closest one in the seller's history.
    /// Zero for a suggestion that came from eBay rather than from the seller's own listings.</summary>
    public double Score { get; set; }

    /// <summary>How many past listings back this category. Zero when eBay supplied it.</summary>
    public int TimesUsed { get; set; }

    /// <summary>The seller's own past title this was matched against — quoted back to them so the
    /// suggestion is checkable rather than an assertion.</summary>
    public string ExampleTitle { get; set; } = "";
}
