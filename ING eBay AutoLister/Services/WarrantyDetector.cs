using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads whatever a listing says about cover, and dates it. Pure — no HTTP and no clock beyond the
/// one passed in — so every rule below is a unit test rather than a guess about a live site.
/// </summary>
/// <remarks>
/// <para>
/// The order of this file is the whole design. <b>Refusal runs first</b>: a listing that says "no
/// longer under warranty" contains the phrase "under warranty", and a Best Buy row offering a
/// three-year protection plan for $89 contains the word "warranty" without carrying one. Both would
/// be read as cover by any pattern that simply looked for the word, and both are common enough that
/// getting them wrong would make this feature worse than not having it.
/// </para>
/// <para>
/// The second rule is that <b>an inference is never allowed to look like a fact</b>. A listing that
/// states its cover and a listing that merely mentions when it was bought both produce a
/// <see cref="WarrantyDetails"/> here, and they carry different <see cref="WarrantyDetails.Evidence"/>
/// — which is what <see cref="WarrantyPricer"/> uses to decide that only one of them is worth money.
/// The estimate still reaches the seller, because "this was bought four months ago and Dyson runs 24
/// months, so ask about the receipt" is genuinely useful. It just never moves a price.
/// </para>
/// </remarks>
public static class WarrantyDetector
{
    /// <summary>
    /// One listing, read for cover — or null when it said nothing that bears on the question, which
    /// is the common case on any classifieds board.
    /// </summary>
    /// <param name="title">The listing's own title.</param>
    /// <param name="detailText">Body text where the source published one; see <see cref="LocalSupplyListing.DetailText"/>.</param>
    /// <param name="retailer">The store, on a retail row. Read alongside the text so "Open-Box" can be
    /// matched to Best Buy's programme rather than to the bare words.</param>
    public static WarrantyDetails? Detect(string? title, string? detailText, string? retailer, DateTime nowUtc)
    {
        var text = Combine(title, detailText);
        if (text.Length == 0) return null;

        // ── Refusal first ────────────────────────────────────────────────────────────────────────
        // Stated absence of cover. Returned rather than dropped: on an expensive buy it is the most
        // useful thing the listing said, and the board holds a verdict down for it.
        if (WarrantySelectors.NoCover.Match(text) is { Success: true } denial)
        {
            return new WarrantyDetails
            {
                Kind = WarrantyKinds.None,
                Evidence = WarrantyEvidence.Stated,
                KindLabel = "No warranty — sold as-is",
                MonthsRemaining = 0,
                TransfersToBuyer = false,
                SourceText = Quote(denial.Value),
                ConditionLabel = ConditionOf(text),
            };
        }

        // A plan being advertised for sale is not a plan being included. Removed rather than refused,
        // because "1 year factory warranty left, extended warranty available" is a genuine covered
        // item whose listing happens to mention both.
        var stripped = WarrantySelectors.CoverForSale.Replace(text, " ");

        var program = WarrantyCatalog.ProgramFor(text, retailer);
        var brand = WarrantyCatalog.BrandFor(title);

        var details = ReadKind(stripped, program);
        if (details is null) return null;

        details.ConditionLabel = ConditionOf(text);
        details.HasProofOfPurchase = WarrantySelectors.ProofOfPurchase.IsMatch(text);
        details.ProgramLabel = program?.Label ?? "";

        ApplyClock(details, stripped, program, brand, nowUtc);
        details.TransfersToBuyer = ReadTransferability(stripped, details.Kind, program, brand);
        details.KindLabel = LabelFor(details);

        // An estimate with no number behind it is not an estimate — it is the word "refurbished" and
        // nothing else — and an estimate that the cover has run out is a guess that something isn't
        // there. Neither is worth a chip on the row: both would fire constantly, say nothing the
        // seller can act on, and crowd out the readings that were actually stated.
        if (details.Evidence == WarrantyEvidence.Estimated && details.MonthsRemaining is null or 0) return null;

        return details;
    }

    // ── Which kind of cover ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Who is on the hook, and how firmly the listing said so — or null when it said nothing that
    /// bears on cover at all.
    /// </summary>
    private static WarrantyDetails? ReadKind(string text, WarrantyTerm? program)
    {
        var stated = WarrantySelectors.HasCover.Match(text);

        if (stated.Success)
        {
            // Order matters: a bought plan is named explicitly, the factory is named explicitly, and
            // a person's own promise is named explicitly. What is left — a bare "still under
            // warranty" — is the factory's, because that is the only cover a used item carries by
            // default and it is what the phrase means to everyone who writes it.
            var kind =
                WarrantySelectors.ExtendedPlan.IsMatch(text) ? WarrantyKinds.Extended :
                WarrantySelectors.ManufacturerBacked.IsMatch(text) ? WarrantyKinds.Manufacturer :
                WarrantySelectors.SellerBacked.IsMatch(text) ? WarrantyKinds.Seller :
                program is not null ? WarrantyKinds.Refurbisher :
                WarrantyKinds.Manufacturer;

            return new WarrantyDetails
            {
                Kind = kind, Evidence = WarrantyEvidence.Stated, SourceText = Quote(stated.Value),
            };
        }

        // A plan named without the word "warranty" anywhere near it — "AppleCare+ until Nov 2027".
        if (WarrantySelectors.ExtendedPlan.Match(text) is { Success: true } plan)
        {
            return new WarrantyDetails
            {
                Kind = WarrantyKinds.Extended, Evidence = WarrantyEvidence.Stated, SourceText = Quote(plan.Value),
            };
        }

        // "Tested and guaranteed working", "30 day money back" — the seller standing behind it in
        // words that never use the word.
        if (WarrantySelectors.SellerBacked.Match(text) is { Success: true } promise)
        {
            return new WarrantyDetails
            {
                Kind = WarrantyKinds.Seller, Evidence = WarrantyEvidence.Stated, SourceText = Quote(promise.Value),
            };
        }

        // A named programme, which publishes its own terms. Treated as stated evidence because the
        // terms are a fact about the programme rather than a claim about this unit — the claim being
        // relied on is only that the listing is in the programme, which is what its own name says.
        if (program is not null)
        {
            return new WarrantyDetails
            {
                Kind = WarrantyKinds.Refurbisher, Evidence = WarrantyEvidence.Program, SourceText = program.Label,
            };
        }

        // Nothing about warranty at all. There may still be a purchase date or an unopened box, which
        // is worth an estimate and never worth a dollar — see ApplyClock, which is what decides
        // whether this reading survives.
        return new WarrantyDetails { Kind = WarrantyKinds.Manufacturer, Evidence = WarrantyEvidence.Estimated };
    }

    // ── How long is left ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dates the cover, in the order the sources deserve to be trusted: a stated end date, then a
    /// stated term measured from a stated purchase date, then a programme's published term, then the
    /// brand's standard one.
    /// </summary>
    /// <remarks>
    /// <see cref="WarrantyDetails.MonthsRemaining"/> is left null rather than guessed whenever the
    /// clock's start is unknown. "1 year warranty" on a used drill says how long the cover ran, not
    /// how much of it is left, and the difference between those two is the entire feature.
    /// </remarks>
    private static void ApplyClock(
        WarrantyDetails details, string text, WarrantyTerm? program, WarrantyTerm? brand, DateTime nowUtc)
    {
        // ── 1. A stated end date. Nothing beats being told. ──────────────────────────────────────
        if (ReadCoverUntil(text, nowUtc) is DateTime expiry)
        {
            details.ExpiresUtc = expiry;
            details.MonthsRemaining = Clamp(WholeMonths(nowUtc, expiry));
            details.TermMonths = ReadTerm(text) ?? 0;
            return;
        }

        var purchased = ReadPurchaseDate(text, nowUtc);

        // ── 2. A stated term ─────────────────────────────────────────────────────────────────────
        if (ReadTerm(text) is int termMonths)
        {
            details.TermMonths = termMonths;

            if (purchased is DateTime bought)
            {
                var end = bought.AddMonths(termMonths);
                details.ExpiresUtc = end;
                details.MonthsRemaining = Clamp(WholeMonths(nowUtc, end));
                return;
            }

            // A refurbisher's or a seller's term starts when this buyer buys it, which is today. A
            // factory term does not: it started when the item was first sold, and nobody said when
            // that was.
            details.MonthsRemaining = details.Kind is WarrantyKinds.Refurbisher or WarrantyKinds.Seller
                ? Clamp(termMonths)
                : null;
            return;
        }

        // ── 3. A programme's published term ───────────────────────────────────────────────────────
        if (program is not null && details.Kind == WarrantyKinds.Refurbisher)
        {
            details.TermMonths = program.Months;
            // Refurbished stock is sold with its cover starting fresh, so unless the listing said
            // when it was bought, the buyer gets the whole term.
            details.MonthsRemaining = purchased is DateTime boughtRefurb
                ? Clamp(WholeMonths(nowUtc, boughtRefurb.AddMonths(program.Months)))
                : Clamp(program.Months);
            if (purchased is null) details.ExpiresUtc = nowUtc.Date.AddMonths(program.Months);
            return;
        }

        // ── 4. The brand's standard cover, against a date the listing gave ───────────────────────
        if (brand is null) return;
        details.TermMonths = brand.Months;

        if (purchased is DateTime boughtOn)
        {
            var end = boughtOn.AddMonths(brand.Months);
            details.ExpiresUtc = end;
            details.MonthsRemaining = Clamp(WholeMonths(nowUtc, end));
            return;
        }

        // An unopened box is the one used listing whose clock has demonstrably not started. Still an
        // estimate, still worth nothing on the board, and still the right thing to tell the seller to
        // go and ask about.
        if (WarrantySelectors.Sealed.IsMatch(text)) details.MonthsRemaining = Clamp(brand.Months);
    }

    /// <summary>A stated warranty end date, resolved against today. Null when the listing named none.</summary>
    public static DateTime? ReadCoverUntil(string? text, DateTime nowUtc)
    {
        var match = WarrantySelectors.CoverUntil.Match(text ?? "");
        if (!match.Success) return null;

        if (match.Groups[1].Success)
        {
            // A warranty end date is in the future by definition, so a year-less "3/15" read in
            // December means next March rather than one that has already gone.
            return ParseNumericDate(match.Groups[1].Value, nowUtc, preferFuture: true);
        }

        return ParseNamedMonth(match.Groups[2].Value, match.Groups[3].Value, endOfMonth: true);
    }

    /// <summary>When the item was bought, however the listing said it. Null when it didn't.</summary>
    public static DateTime? ReadPurchaseDate(string? text, DateTime nowUtc)
    {
        var onDate = WarrantySelectors.PurchasedOn.Match(text ?? "");
        if (onDate.Success)
        {
            // A purchase is in the past, so a year-less date is read backwards rather than forwards.
            var parsed = onDate.Groups[1].Success
                ? ParseNumericDate(onDate.Groups[1].Value, nowUtc, preferFuture: false)
                : ParseNamedMonth(onDate.Groups[2].Value, onDate.Groups[3].Value, endOfMonth: false);

            // A "purchase date" in the future is a misparse, not a purchase.
            if (parsed is DateTime date && date <= nowUtc) return date;
        }

        var ago = WarrantySelectors.PurchasedAgo.Match(text ?? "");
        if (!ago.Success) return null;

        // "bought 3 months ago" and "bought last month" are the same sentence with the count in
        // words; "a week ago" and "last week" both mean one.
        var count = ago.Groups[1].Success && int.TryParse(ago.Groups[1].Value, out var parsedCount) ? parsedCount : 1;
        return ago.Groups[3].Value.ToLowerInvariant() switch
        {
            "week" or "weeks" => nowUtc.Date.AddDays(-7 * count),
            "year" or "years" => nowUtc.Date.AddYears(-count),
            _ => nowUtc.Date.AddMonths(-count),
        };
    }

    /// <summary>A stated term in whole months — "90 day warranty" is 3, "3 yr warranty" is 36.</summary>
    public static int? ReadTerm(string? text)
    {
        var match = WarrantySelectors.Term.Match(text ?? "");
        if (!match.Success) return null;

        // Two alternations, either of which may be the one that captured: the number can come before
        // the word "warranty" or after it.
        var (rawCount, rawUnit) = match.Groups[1].Success
            ? (match.Groups[1].Value, match.Groups[2].Value)
            : (match.Groups[3].Value, match.Groups[4].Value);

        if (!int.TryParse(rawCount, out var count) || count <= 0) return null;

        var months = rawUnit.ToLowerInvariant() switch
        {
            "year" or "yr" or "yrs" => count * 12,
            "day" or "days" => (int)Math.Round(count / 30.0, MidpointRounding.AwayFromZero),
            _ => count,
        };

        return months > 0 ? months : null;
    }

    // ── Does it survive the resale ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the person the reseller sells to gets anything. The listing's own words win, then the
    /// programme's terms, then the brand's, then the kind's default — see
    /// <see cref="WarrantyCatalog.TransfersByDefault"/>.
    /// </summary>
    public static bool ReadTransferability(string text, string kind, WarrantyTerm? program, WarrantyTerm? brand)
    {
        if (WarrantySelectors.NonTransferable.IsMatch(text)) return false;
        if (WarrantySelectors.Transferable.IsMatch(text)) return true;

        if (kind == WarrantyKinds.Refurbisher && program is not null) return program.Transfers;
        if (kind == WarrantyKinds.Manufacturer && brand is not null) return brand.Transfers;

        return WarrantyCatalog.TransfersByDefault(kind);
    }

    // ── Wording ──────────────────────────────────────────────────────────────────────────────────

    private static string ConditionOf(string text)
    {
        var refurb = WarrantySelectors.RefurbCondition.Match(text);
        if (refurb.Success) return refurb.Groups[1].Value.ToLowerInvariant();

        var boxed = WarrantySelectors.Sealed.Match(text);
        return boxed.Success ? boxed.Groups[1].Value.ToLowerInvariant() : "";
    }

    private static string LabelFor(WarrantyDetails details)
    {
        var name = details.Kind switch
        {
            WarrantyKinds.None => "No warranty",
            WarrantyKinds.Refurbisher => details.ProgramLabel.Length > 0 ? details.ProgramLabel : "Refurbisher warranty",
            WarrantyKinds.Extended => "Protection plan",
            WarrantyKinds.Seller => "Seller warranty",
            _ => "Manufacturer warranty",
        };

        // The estimate says so in the label itself, not only in a tooltip nobody opens. A seller
        // deciding what to pay has to be able to see at a glance which of these was actually stated.
        var qualifier = details.Evidence == WarrantyEvidence.Estimated ? " (estimated)" : "";

        return details.MonthsRemaining switch
        {
            null => $"{name}{qualifier}",
            0 => $"{name} — expired{qualifier}",
            1 => $"{name} · 1 month left{qualifier}",
            { } months => $"{name} · {months} months left{qualifier}",
        };
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static string Combine(string? title, string? detailText)
    {
        var body = (detailText ?? "").Trim();
        if (body.Length > WarrantySelectors.MaxDetailChars) body = body[..WarrantySelectors.MaxDetailChars];
        return $"{(title ?? "").Trim()} {body}".Trim();
    }

    // The listing's own words, short enough to sit in a tooltip beside the row.
    private static string Quote(string matched)
    {
        var tidied = Regex.Replace(matched.Trim(), @"\s{2,}", " ");
        return tidied.Length > QuoteChars ? tidied[..QuoteChars].TrimEnd() + "…" : tidied;
    }

    private const int QuoteChars = 90;

    private static int? Clamp(int months) =>
        months <= 0 ? 0 : Math.Min(months, WarrantySelectors.MaxCreditedMonths);

    /// <summary>Whole months from one date to another, floored. Negative spans come back as zero.</summary>
    public static int WholeMonths(DateTime from, DateTime to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        return months < 0 ? 0 : months;
    }

    // "3/2027", "12/25/26", "3/15". Missing years are resolved to whichever side of today the caller
    // says the date belongs on — a warranty ends in the future and a purchase happened in the past,
    // and reading either one the wrong way inverts the answer completely.
    private static DateTime? ParseNumericDate(string raw, DateTime nowUtc, bool preferFuture)
    {
        var parts = raw.Split(['/', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var month) || month is < 1 or > 12) return null;

        try
        {
            // "3/2027" — a month and a year, with no day. The cover runs to the end of that month.
            if (parts.Length == 2 && parts[1].Length == 4 && int.TryParse(parts[1], out var monthYear))
                return EndOfMonth(monthYear, month);

            if (!int.TryParse(parts[1], out var day) || day is < 1 or > 31) return null;

            if (parts.Length >= 3 && int.TryParse(parts[2], out var year))
                return new DateTime(year < 100 ? 2000 + year : year, month, day, 0, 0, 0, DateTimeKind.Utc);

            // No year at all: take this year's, then move it to the side of today it belongs on.
            var date = new DateTime(nowUtc.Year, month, day, 0, 0, 0, DateTimeKind.Utc);
            if (preferFuture && date < nowUtc.Date) date = date.AddYears(1);
            if (!preferFuture && date > nowUtc.Date) date = date.AddYears(-1);
            return date;
        }
        catch (ArgumentOutOfRangeException)
        {
            // 2/30 and friends. A date that isn't one is no deadline at all.
            return null;
        }
    }

    private static DateTime? ParseNamedMonth(string monthName, string year, bool endOfMonth)
    {
        if (!int.TryParse(year, out var y) || y is < 2000 or > 2100) return null;

        var month = Array.FindIndex(
            MonthNames, m => m.Equals(monthName, StringComparison.OrdinalIgnoreCase)) + 1;
        if (month == 0) return null;

        return endOfMonth ? EndOfMonth(y, month) : new DateTime(y, month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime EndOfMonth(int year, int month) =>
        new(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>"Mar 2027" — how the row prints an expiry, in the seller's own calendar terms.</summary>
    public static string MonthYear(DateTime date) =>
        date.ToString("MMM yyyy", CultureInfo.InvariantCulture);
}
