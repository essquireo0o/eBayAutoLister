using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads a pasted listing URL's own HTML for the three things a Buy/Pass answer needs: what it is,
/// what they want for it, and a picture of it.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total: HTML in, <see cref="SnapPageFacts"/> out, no network and no browser. That is the
/// whole design constraint. The app already has a headless-browser route for a pasted URL
/// (<c>TakeHeadlessScreenshot</c> behind <c>/api/analyze-url</c>) and it takes tens of seconds —
/// entirely reasonable for writing a listing at a desk, and useless standing in somebody's driveway
/// with the phone in one hand. Structured metadata is what makes the fast path possible: Craigslist,
/// Facebook, OfferUp, eBay and every retailer publish Open Graph tags and JSON-LD for the benefit of
/// link previews, and a link preview is exactly the amount of information this screen needs.
/// </para>
/// <para>
/// It reads the tags rather than the page. A price scraped out of visible body text is as likely to
/// be a "customers also bought" tile or a shipping threshold as it is the item's own price, and a
/// wrong price here does not produce a wrong number — it produces a confident BUY on a deal that
/// doesn't exist. So the price comes from a declared price field or from nowhere, and a page that
/// declares none hands back a null the caller asks the seller to fill in.
/// </para>
/// </remarks>
public static class SnapPageParser
{
    // Enough of the head to carry the metadata on every site tested, and a hard bound on the work.
    // Craigslist's whole document is smaller than this; Facebook's is megabytes of script.
    public const int MaxScanChars = 400_000;

    // A listing is worth at most this much before the figure is more likely to be a page artefact
    // than a price — a phone number, a zip code, a JSON id that happened to sit next to a currency
    // symbol. Vehicles are the reason it is not lower.
    private const decimal MaxBelievablePrice = 500_000m;

    /// <summary>Does this look like something to fetch rather than something to search for?</summary>
    public static bool LooksLikeUrl(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Contains(' ')) return false;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>The site's name, for the row to say where the price came from.</summary>
    public static string SiteLabel(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        if (host.Contains("craigslist")) return "Craigslist";
        if (host.Contains("facebook") || host.Contains("fb.com")) return "Facebook Marketplace";
        if (host.Contains("offerup")) return "OfferUp";
        if (host.Contains("ebay")) return "eBay";
        if (host.Contains("mercari")) return "Mercari";
        if (host.Contains("poshmark")) return "Poshmark";
        if (host.Contains("nextdoor")) return "Nextdoor";
        if (host.Contains("shopgoodwill")) return "ShopGoodwill";
        if (host.Contains("estatesales")) return "EstateSales.net";
        return host;
    }

    /// <summary>
    /// Everything the page will admit to. <paramref name="url"/> is used only for the site label —
    /// nothing here fetches anything.
    /// </summary>
    public static SnapPageFacts Parse(string? html, string? url = null)
    {
        var facts = new SnapPageFacts { SiteLabel = SiteLabel(url) };
        if (string.IsNullOrWhiteSpace(html)) return facts;

        var doc = html.Length > MaxScanChars ? html[..MaxScanChars] : html;

        facts.Title = CleanTitle(FirstTitle(doc), facts.SiteLabel);
        facts.ImageUrl = FirstImage(doc);

        var (price, text, isFree) = FirstPrice(doc);
        facts.Price = price;
        facts.PriceText = text;
        facts.IsFree = isFree;

        return facts;
    }

    // ── Title ────────────────────────────────────────────────────────────────
    // og:title is what every one of these sites puts the item's own name in, because that is what
    // a shared link renders. <title> is the fallback and the worst of the three: it routinely
    // carries the site's name, the city and a tagline as well as the item.
    private static string FirstTitle(string html) =>
        MetaContent(html, "og:title")
        ?? MetaContent(html, "twitter:title")
        ?? JsonLdString(html, "name")
        ?? TagText(html, "title")
        ?? "";

    // Site furniture, off the end of the name. A comp lookup fed "… - craigslist" searches eBay for
    // the word craigslist, which matches nothing and quietly costs the seller their answer.
    private static readonly string[] TitleSuffixes =
    [
        " - craigslist", " | eBay", " for sale online | eBay", " | Facebook Marketplace",
        " - Facebook Marketplace", " | OfferUp", " - OfferUp", " | Mercari", " | Poshmark",
        " | Nextdoor", " - Nextdoor",
    ];

    /// <summary>
    /// Challenge and interstitial pages, by the titles they publish.
    /// </summary>
    /// <remarks>
    /// The failure this exists to stop, found by pointing the finished feature at a live Walmart
    /// product page: the bot check answered <b>HTTP 200</b> with a complete set of Open Graph tags,
    /// and the app dutifully priced an item called "Robot or human?" — coming back with a confident
    /// "BUY UNDER $464" against comps for whatever eBay thinks those words are worth. A status-code
    /// check cannot catch that, because nothing failed. Only the title gives it away.
    /// <para>
    /// The whole point of this screen is a number somebody acts on while standing in front of the
    /// seller, so a wrong page must produce no answer rather than a plausible one. Refusing to name
    /// the item routes it into the endpoint's own "that page didn't say what it is" reply, which
    /// tells the seller to photograph the thing instead — the one route no CDN can block.
    /// </para>
    /// </remarks>
    // Phrases no product is ever called. Safe to match anywhere in the title, because a listing that
    // contains "pardon our interruption" is not a listing.
    private static readonly string[] ChallengePhrases =
    [
        "robot or human", "are you a human", "are you a robot", "verify you are human",
        "just a moment", "attention required", "access to this page has been denied",
        "pardon our interruption", "checking your browser", "request blocked",
        "unusual traffic", "enable javascript",
    ];

    // Words that ARE plausible inside a real item's name — "Blocked Drain Auger", "Security Camera",
    // "Page Not Found Poster". These only count when they are the WHOLE title, which a listing's
    // never is.
    private static readonly string[] ChallengeExactTitles =
    [
        "access denied", "security check", "captcha", "blocked", "forbidden", "403 forbidden",
        "page not found", "404 not found", "error", "one moment please",
    ];

    /// <summary>
    /// True when the page is a bot check, a block page or an error page rather than a listing.
    /// </summary>
    public static bool IsChallengeTitle(string? title)
    {
        var t = Collapse(WebUtility.HtmlDecode(title ?? "")).ToLowerInvariant().TrimEnd('.', '!', '?', ' ');
        if (t.Length == 0) return false;

        if (ChallengeExactTitles.Contains(t)) return true;

        // Bounded: a challenge page announces itself in a few words, and the bound is what keeps a
        // long, legitimate description that happens to quote one of these phrases from being thrown
        // away on the strength of it.
        return t.Length <= 60 && ChallengePhrases.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    // The registry labels that sit between a brand and a country code — the reason "someshop.co.uk"
    // is not a site called "co". Not a public-suffix list, and it does not need to be: this only
    // decides which word to strip off the end of a title, so a miss leaves the title slightly long
    // rather than breaking anything.
    private static readonly string[] RegistrySecondLevels =
        ["co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "govt"];

    // The site's own brand word, however the label arrived: a host for the sites this parser has
    // never heard of ("en.wikipedia.org" → "wikipedia"), and the label itself for the ones it has.
    private static string SecondLevelDomain(string siteLabel)
    {
        var label = (siteLabel ?? "").Trim();
        if (label.Length == 0) return "";
        if (!label.Contains('.')) return label;

        var parts = label.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return label;

        // someshop.co.uk → the brand is one label further left than it is on someshop.com.
        if (parts.Length >= 3 && RegistrySecondLevels.Contains(parts[^2], StringComparer.OrdinalIgnoreCase))
            return parts[^3];

        return parts[^2];
    }

    private static string CleanTitle(string raw, string siteLabel)
    {
        var t = Collapse(WebUtility.HtmlDecode(raw ?? ""));
        if (t.Length == 0) return "";

        if (IsChallengeTitle(t)) return "";

        foreach (var suffix in TitleSuffixes)
        {
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                t = t[..^suffix.Length].TrimEnd();
        }

        // The site's own name off the end, for every site not in the list above. Derived from the
        // host rather than enumerated, because the list will always be one site short of the one
        // the seller just pasted — "… - Wikipedia", "… | Newegg", "… – OfferUp" all go the same way.
        if (SecondLevelDomain(siteLabel) is { Length: > 2 } brand)
        {
            t = Regex.Replace(t, $@"\s*[-–—|·]\s*{Regex.Escape(brand)}\s*$", "", RegexOptions.IgnoreCase);
        }

        // Facebook titles read "Marketplace - Dewalt drill" and eBay's read "Dewalt DCD771 Drill |
        // eBay"; both leaders are noise in front of the only words that matter.
        t = Regex.Replace(t, @"^\s*Marketplace\s*[-–|]\s*", "", RegexOptions.IgnoreCase);

        // A price is not part of the item's name, and leaving it in the lookup narrows the comp
        // search to sold listings that happened to type the same number into their own title.
        t = Regex.Replace(t, @"^\s*\$[\d,]+(?:\.\d{2})?\s*[-–|·]\s*", "");

        // The trailing city Craigslist and Facebook append. Only stripped when it is parenthesised
        // or after a comma-and-state, so "New York Yankees Jersey" keeps its city.
        t = Regex.Replace(t, @"\s*\([^()]{1,40}\)\s*$", "");

        if (siteLabel.Length > 0 && t.Equals(siteLabel, StringComparison.OrdinalIgnoreCase)) return "";
        return Collapse(t);
    }

    // ── Image ────────────────────────────────────────────────────────────────
    private static string FirstImage(string html)
    {
        var url = MetaContent(html, "og:image")
                  ?? MetaContent(html, "twitter:image")
                  ?? MetaContent(html, "twitter:image:src")
                  ?? "";
        url = WebUtility.HtmlDecode(url).Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "";
    }

    // ── Price ────────────────────────────────────────────────────────────────
    // Declared price fields only, in descending order of how specifically each one means "this
    // item's price". Craigslist is the one site here that publishes no price metadata at all, so it
    // gets the one markup exception: its own <span class="price"> element, which is the price and
    // nothing else.
    private static (decimal? Price, string Text, bool IsFree) FirstPrice(string html)
    {
        var candidates = new List<string?>
        {
            MetaContent(html, "product:price:amount"),
            MetaContent(html, "og:price:amount"),
            MetaContent(html, "twitter:data1"),
            ItemPropContent(html, "price"),
            JsonLdString(html, "price"),
            JsonLdString(html, "lowPrice"),
            CraigslistPriceSpan(html),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var text = Collapse(WebUtility.HtmlDecode(candidate));
            if (LooksFree(text)) return (0m, text, true);

            if (TryParsePrice(text, out var value)) return (value, text, false);
        }

        // A listing that says "free" and prices nothing is a real and common case, and it is the
        // best possible answer to "what do they want for it" — checked last so it can never
        // override a page that declared an actual number.
        if (FreeMetaClaim(html)) return (0m, "Free", true);

        return (null, "", false);
    }

    private static bool LooksFree(string text) =>
        text.Equals("free", StringComparison.OrdinalIgnoreCase)
        || text.Equals("$0", StringComparison.OrdinalIgnoreCase)
        || text.Equals("$0.00", StringComparison.OrdinalIgnoreCase);

    private static bool FreeMetaClaim(string html)
    {
        var title = MetaContent(html, "og:title") ?? "";
        return Regex.IsMatch(WebUtility.HtmlDecode(title), @"(^|\W)free(\W|$)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// A price out of whatever shape the field arrived in: "1,250.00", "$1,250", "USD 1250",
    /// "1250.00 USD". Rejects anything that isn't believable as a price for a thing.
    /// </summary>
    public static bool TryParsePrice(string? text, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = Regex.Match(text, @"\d[\d,]*(?:\.\d{1,2})?");
        if (!match.Success) return false;

        var digits = match.Value.Replace(",", "");
        if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return false;

        if (value <= 0m || value > MaxBelievablePrice) return false;

        price = Math.Round(value, 2);
        return true;
    }

    // ── Extractors ───────────────────────────────────────────────────────────
    // Attribute order varies by site, so each of these tries content-last and content-first rather
    // than assuming one shape. Deliberately narrow: they read declared metadata, not page text.

    private static string? MetaContent(string html, string property)
    {
        var name = Regex.Escape(property);

        var m = Regex.Match(html,
            $"<meta[^>]+(?:property|name)\\s*=\\s*[\"']{name}[\"'][^>]*?content\\s*=\\s*[\"']([^\"']*)[\"']",
            RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value;

        m = Regex.Match(html,
            $"<meta[^>]+content\\s*=\\s*[\"']([^\"']*)[\"'][^>]*?(?:property|name)\\s*=\\s*[\"']{name}[\"']",
            RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value : null;
    }

    private static string? ItemPropContent(string html, string prop)
    {
        var name = Regex.Escape(prop);
        var m = Regex.Match(html,
            $"itemprop\\s*=\\s*[\"']{name}[\"'][^>]*?content\\s*=\\s*[\"']([^\"']*)[\"']",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // The first value of a JSON key anywhere in the document. Deliberately not a JSON parse: the
    // key is looked for inside the whole page because JSON-LD, inline state blobs and embedded
    // React props all carry the same field names, and any of the three is a better source than
    // visible text. Bounded to a short value so it can never pull in a serialised object.
    private static string? JsonLdString(string html, string key)
    {
        var name = Regex.Escape(key);
        var m = Regex.Match(html, $"[\"']{name}[\"']\\s*:\\s*[\"']([^\"']{{1,120}})[\"']", RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value;

        m = Regex.Match(html, $"[\"']{name}[\"']\\s*:\\s*(\\d[\\d.]{{0,12}})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? TagText(string html, string tag)
    {
        var m = Regex.Match(html, $"<{tag}[^>]*>(.*?)</{tag}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Craigslist publishes no price metadata. Its price element is unambiguous and appears in the
    // posting header, so it is the one piece of markup this parser is allowed to read.
    private static string? CraigslistPriceSpan(string html)
    {
        var m = Regex.Match(html, @"class\s*=\s*[""'][^""']*\bprice\b[^""']*[""'][^>]*>\s*\$?\s*([\d,]+(?:\.\d{2})?)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Collapse(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
}
