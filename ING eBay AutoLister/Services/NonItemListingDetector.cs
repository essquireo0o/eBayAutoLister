namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Screens out listings that are not the product at all — a repair service, a manual, a core
/// charge — before anything tries to price them against that product's sold comps.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to stop looks exactly like the best find of the day. Search eBay for
/// "fanuc" and the cheapest results are listings like <i>"Fanuc A02B-0168-C013 Repair Evaluation"</i>
/// at $1: a repair shop advertising a service, priced at a dollar so it sorts to the top of
/// cheapest-first. The part number in that title is real, so
/// <see cref="ComparableMatcher"/> matches it to sold comps for the actual board at $899 — an
/// exact-identifier hit, 35 points, top confidence — and the board reports $777 net profit at
/// 78,485% ROI, ranked first. Every number is arithmetically correct and the row is worthless:
/// what is for sale is labour, not the part.
/// </para>
/// <para>
/// Nothing downstream can catch this. The comps matcher screens the <i>comparables</i> for
/// parts-only/broken/accessory wording; it has no opinion about the target, and here the target is
/// what's wrong. The profit maths is fed a real ask and a real sold price. Only the title says what
/// actually happened, so it is read here, once, before the listing is priced.
/// </para>
/// <para>
/// Three kinds of listing, all of which borrow a product's identifiers without being the product:
/// <list type="bullet">
///   <item><b>Services</b> — repair, evaluation, calibration, programming. You send them yours.</item>
///   <item><b>Paperwork</b> — service and owner's manuals, "info only" listings. Real goods, but
///     worth a fraction of the machine they document, and never priced by its comps.</item>
///   <item><b>Core charges</b> — a refundable deposit line, not an item.</item>
/// </list>
/// </para>
/// <para>
/// Phrases only, never bare words. "Repair" alone would take out repair <i>kits</i> and repair
/// <i>parts</i>, which are exactly the physical goods this board exists to find, and a screen that
/// eats real inventory is worse than the junk it removes. For the same reason "for repair" and
/// "for parts only" are deliberately absent: those are genuine broken items being sold cheap, which
/// is a real sourcing play — the comps matcher already refuses to price them against working ones.
/// </para>
/// <para>Pure and static: no state, no I/O, so the whole vocabulary is testable directly.</para>
/// </remarks>
public static class NonItemListingDetector
{
    /// <summary>
    /// Phrase, and the sentence the seller is shown if they ask why a row is missing. Matched against
    /// a flattened title, so punctuation between the words ("Repair &amp; Evaluation", "Repair/Eval")
    /// does not decide whether a junk row reaches the board.
    /// </summary>
    public static readonly (string Phrase, string Reason)[] Vocabulary =
    [
        // ── Services: what's for sale is labour on a unit you already own ──────────────────
        ("repair evaluation",      "a repair service, not the part"),
        ("evaluation service",     "a repair service, not the part"),
        ("repair service",         "a repair service, not the part"),
        ("repair quote",           "a repair service, not the part"),
        ("flat rate repair",       "a repair service, not the part"),
        ("send in repair",         "a repair service, not the part"),
        ("mail in repair",         "a repair service, not the part"),
        ("we repair",              "a repair service, not the part"),
        ("advance exchange",       "an exchange service, not the part"),
        ("exchange service",       "an exchange service, not the part"),
        ("rebuild service",        "a rebuild service, not the part"),
        ("refurbishment service",  "a refurbishment service, not the part"),
        ("calibration service",    "a calibration service, not the item"),
        ("cleaning service",       "a cleaning service, not the item"),
        ("programming service",    "a programming service, not the item"),
        ("installation service",   "an installation service, not the item"),
        ("diagnostic service",     "a diagnostic service, not the item"),
        ("diagnostics service",    "a diagnostic service, not the item"),

        // ── Paperwork: documents the product rather than being it ─────────────────────────
        ("service manual",         "a manual, not the item it documents"),
        ("repair manual",          "a manual, not the item it documents"),
        ("user manual",            "a manual, not the item it documents"),
        ("users manual",           "a manual, not the item it documents"),
        ("owners manual",          "a manual, not the item it documents"),
        ("owner s manual",         "a manual, not the item it documents"),
        ("operators manual",       "a manual, not the item it documents"),
        ("operator s manual",      "a manual, not the item it documents"),
        ("instruction manual",     "a manual, not the item it documents"),
        ("parts manual",           "a manual, not the item it documents"),
        ("maintenance manual",     "a manual, not the item it documents"),
        ("programmers manual",     "a manual, not the item it documents"),
        ("programmer s manual",    "a manual, not the item it documents"),
        ("programming manual",     "a manual, not the item it documents"),
        ("operation manual",       "a manual, not the item it documents"),
        ("operating manual",       "a manual, not the item it documents"),
        ("installation manual",    "a manual, not the item it documents"),
        ("technical manual",       "a manual, not the item it documents"),
        ("manual set",             "a manual, not the item it documents"),
        ("info only",              "an information listing, not the item"),
        ("information only",       "an information listing, not the item"),
        ("photo only",             "an information listing, not the item"),
        ("picture only",           "an information listing, not the item"),

        // ── Core charges: a refundable deposit line, not a thing you receive ───────────────
        ("core charge",            "a core charge, not the part"),
        ("core deposit",           "a core charge, not the part"),
        ("core exchange",          "a core charge, not the part"),
    ];

    /// <summary>
    /// Why this listing isn't the product, or null when it looks like real goods.
    /// Null is the answer for a blank title too — an unreadable listing is not a screened one, and
    /// the pricing path already refuses those for its own reasons.
    /// </summary>
    public static string? Detect(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Padded so a phrase can only match on whole-word boundaries: without it "core charge"
        // would fire on "hardcore charger".
        var haystack = $" {Flatten(title)} ";

        foreach (var (phrase, reason) in Vocabulary)
            if (haystack.Contains($" {phrase} ", StringComparison.Ordinal))
                return reason;

        return null;
    }

    public static bool IsNotTheItem(string? title) => Detect(title) is not null;

    /// <summary>
    /// Lowercase, with every run of non-alphanumerics collapsed to one space. "Repair/Evaluation",
    /// "REPAIR - EVALUATION" and "Repair &amp; Evaluation" all flatten to the same thing, so the
    /// vocabulary above stays a list of words rather than a list of punctuation variants.
    /// </summary>
    private static string Flatten(string title)
    {
        var chars = new char[title.Length];
        var length = 0;
        var lastWasSpace = true;   // leading separators collapse away entirely

        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                chars[length++] = char.ToLowerInvariant(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                chars[length++] = ' ';
                lastWasSpace = true;
            }
        }

        return new string(chars, 0, lastWasSpace && length > 0 ? length - 1 : length);
    }
}
