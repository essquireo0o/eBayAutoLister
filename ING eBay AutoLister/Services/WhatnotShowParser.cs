using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads the lot that is on the block off a Whatnot show's own page, so the two boxes on the
/// WhatsNot card fill themselves instead of being typed while an auctioneer talks over the seller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the page and not the API.</b> A live show's current lot is structured data — Whatnot's
/// own app gets it from an internal GraphQL endpoint, and calling that directly would be the
/// shortest path to a title and a current bid. This does not do that. A private API is a documented
/// prohibition in most terms of service and an undocumented contract besides; the seller's account
/// is the thing at risk, and it is not this app's to spend. What this reads instead is the public
/// HTML of the page the seller already has open — the same bytes their browser is served, with the
/// server-rendered payload still in it, fetched once when they press a button. That is a weaker
/// source (it is a snapshot, and its shape is Whatnot's to change without telling anyone) and it is
/// the one this app is entitled to. If it ever stops working the answer is a new page shape here,
/// not a login and a private endpoint.
/// </para>
/// <para>
/// <b>What this refuses to do.</b> Everything here feeds the box every number on the live card is
/// derived from, so a parser that guesses is worse than no parser: a wrong <b>title</b> prices a
/// different product and a wrong <b>price</b> prices this one wrongly, and both arrive on screen as
/// a confident ceiling with a hammer coming down on it. Two rules carry most of that weight — a
/// number is divided by 100 only where the KEY says cents, and a bare <c>name</c> is a title only on
/// an object that is otherwise a lot for sale — and where neither can be applied honestly the read
/// says it found nothing and points at the box that always works.
/// </para>
/// <para>
/// Nothing here prices anything. The title and the starting number go straight to
/// <c>/api/whatsnot/bid</c>, which is the same eBay sold-comp path the typed item, the pasted lot
/// list and the Opportunity Finder all take — one route to a ceiling, one opinion per item.
/// </para>
/// </remarks>
public static class WhatnotShowParser
{
    /// <summary>How long a lot's name is allowed to be before it is cut. A sold search runs on a
    /// product name; past this the page is handing over a paragraph, and searching the paragraph
    /// whole returns nothing at all.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>How much of the page is fetched and examined. A live show page is megabytes of
    /// script and the payload is near the front of it; reading past this spends the seconds the lot
    /// lasts, and holding an endless response in memory spends rather more than that.</summary>
    public const int MaxPageBytes = 3 * 1024 * 1024;

    /// <summary>How many script payloads are pulled out of one page.</summary>
    public const int MaxBlobs = 240;

    /// <summary>How many top-level objects are cut out of one payload.</summary>
    public const int MaxObjectsPerBlob = 64;

    /// <summary>How many JSON nodes are walked in total. The cap is why a hostile — or merely
    /// enormous — payload cannot take this screen down with it.</summary>
    public const int MaxNodes = 60_000;

    /// <summary>How deep the walk goes. Framework payloads nest, but not like this.</summary>
    public const int MaxDepth = 40;

    /// <summary>How many listing-shaped objects are ranked before the rest are ignored.</summary>
    public const int MaxCandidates = 200;

    /// <summary>Said whenever the read cannot help, because the path that always works is one box away.</summary>
    private const string TypeItInstead =
        "You can still type the item into the box above and press Price it — that path works on any " +
        "platform, and it is the same ceiling either way.";

    // ── The address ──────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a host is Whatnot itself, rather than something that merely contains the word.</summary>
    public static bool IsWhatnotHost(string? host)
    {
        var name = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0) return false;
        return name == "whatnot.com" || name.EndsWith(".whatnot.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether what the seller typed is a web address rather than the name of a thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live screen's item box sits beside a browser tab showing the auction, so the show's own
    /// URL is the likeliest thing in the world to be pasted into it. Passed on as an item name it
    /// became the sold query <c>https www com/live/b059f792-fbd1-4811-af64-54cc653999e8</c> — the
    /// tokeniser had stripped the scheme and the host into separate words — and the card came back
    /// CAN'T PRICE IT. It was never going to price it: no eBay listing has ever been titled after a
    /// Whatnot link, and none ever will be, so that search fails identically every time it is run.
    /// </para>
    /// <para>
    /// A name is refused only when it cannot be anything else: no whitespace, and either a real
    /// http(s) address or a bare <c>www.</c> host. "S19j-Pro-104TH" and "whatnot.com" are left
    /// alone — the first is an item, and the second is a word someone might reasonably be selling.
    /// </para>
    /// </remarks>
    public static (bool IsAddress, bool IsWhatnotShow) ReadTypedAddress(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return (false, false);

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return (true, IsWhatnotHost(uri.Host));

        // "www.whatnot.com/live/…" — the address bar copied without its scheme.
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate("https://" + s, UriKind.Absolute, out var bare))
            return (true, IsWhatnotHost(bare.Host));

        return (false, false);
    }

    /// <summary>
    /// Whether this address is a live show worth fetching, and — when it is not — what to say about
    /// it. Refusing here rather than fetching is the point: this read knows exactly one site's page
    /// shape, and pointing it at another site would either fail to parse or, far worse, parse some
    /// other page's stray title into the box that decides the bid.
    /// </summary>
    public static (bool Ok, string Headline, string Detail, string Hint) ValidateShowUrl(string? rawUrl)
    {
        var text = (rawUrl ?? "").Trim();

        if (text.Length == 0 || text.Any(char.IsWhiteSpace) || !Uri.TryCreate(Normalize(text), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "That doesn't look like a web address.",
                    "Paste the address of the show you're watching — it's the one in your browser's " +
                    "address bar while the stream is playing.",
                    TypeItInstead);
        }

        if (!IsWhatnotHost(uri.Host))
        {
            return (false, $"This reads Whatnot shows, and that address is {DisplayHost(uri.Host)}.",
                    "Only one site's page shape is known here. Pointed at another site this would " +
                    "either find nothing or find the wrong thing, and the wrong thing arrives as a " +
                    "confident ceiling on a product nobody is selling.",
                    TypeItInstead);
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || (segments.Length == 1 && segments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "That's Whatnot's browse page, not a show.",
                    "It lists a hundred shows and has no lot of its own, so \"nothing on screen\" " +
                    "about it would be true and would read as a broken feature.",
                    "Open the show you're watching and paste that address — it ends in the show's own id.");
        }

        return (true, "", "", "");
    }

    /// <summary>What the seller typed, with the scheme filled in the way a browser fills it.</summary>
    public static string Normalize(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? text
            : "https://" + text;
    }

    private static string DisplayHost(string host)
    {
        var name = (host ?? "").Trim().TrimEnd('.');
        return name.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }

    // ── Getting the data out of the page ─────────────────────────────────────────────────────

    private static readonly Regex ScriptTag = new(
        @"<script\b[^>]*>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every chunk of JSON text the page carries, in the two shapes a Next.js app serves it in: the
    /// whole payload in a <c>&lt;script type="application/json"&gt;</c> tag, and the app-router
    /// shape, where it arrives as escaped strings pushed one at a time into an array.
    /// </summary>
    public static IEnumerable<string> JsonBlobs(string? html)
    {
        var text = html ?? "";
        if (text.Length > MaxPageBytes) text = text[..MaxPageBytes];
        if (text.Length == 0) yield break;

        var given = 0;

        foreach (Match tag in ScriptTag.Matches(text))
        {
            if (given >= MaxBlobs) yield break;

            var body = tag.Groups["body"].Value;
            if (body.Length == 0) continue;

            var lead = body.AsSpan().TrimStart();
            if (lead.Length > 0 && (lead[0] == '{' || lead[0] == '['))
            {
                given++;
                yield return body;
                continue;
            }

            // The streamed shape. The payload is a JS string literal with row markers around it
            // ("3:{…}\n"), so the literal is decoded here and the objects are cut out of it below —
            // handing the whole stream to a JSON parser would have it reject all of it.
            if (!body.Contains("__next_f", StringComparison.Ordinal)
                && !body.Contains("\\\"", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var literal in StringLiterals(body))
            {
                if (given >= MaxBlobs) yield break;

                var decoded = UnescapeJsString(literal);
                if (decoded.Length == 0 || !decoded.Contains('{')) continue;

                given++;
                yield return decoded;
            }
        }
    }

    /// <summary>The double-quoted string literals in a piece of script, backslashes honoured.</summary>
    private static IEnumerable<string> StringLiterals(string text)
    {
        var i = 0;
        var found = 0;

        while (i < text.Length && found < MaxBlobs)
        {
            if (text[i] != '"') { i++; continue; }

            var start = ++i;
            while (i < text.Length)
            {
                if (text[i] == '\\') { i += 2; continue; }
                if (text[i] == '"') break;
                i++;
            }

            // An unterminated literal means the page was cut off mid-string. Everything after it is
            // guesswork about where the string ended, so the scan stops rather than guessing.
            if (i >= text.Length) yield break;

            found++;
            yield return text[start..i];
            i++;
        }
    }

    /// <summary>
    /// A JS string literal's own text. Returns empty for anything it cannot decode exactly —
    /// half of an escaped string parses into confident nonsense, which is the one failure mode this
    /// read cannot have.
    /// </summary>
    public static string UnescapeJsString(string? text)
    {
        if (text is null) return "";

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\') { sb.Append(c); continue; }

            if (++i >= text.Length) return "";   // a trailing backslash is a cut-off string

            switch (text[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case '\'': sb.Append('\''); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 >= text.Length
                        || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                            CultureInfo.InvariantCulture, out var code))
                    {
                        return "";
                    }
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    // Not an escape this understands, so this is not the kind of string it thought
                    // it was reading. Dropping all of it is the only safe answer.
                    return "";
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The balanced <c>{…}</c> objects in a piece of text, in order. Braces inside strings do not
    /// count, and an object that never closes ends the scan rather than the process.
    /// </summary>
    public static IReadOnlyList<string> ObjectSpans(string? text)
    {
        var spans = new List<string>();
        var source = text ?? "";

        var i = 0;
        while (i < source.Length && spans.Count < MaxObjectsPerBlob)
        {
            if (source[i] != '{') { i++; continue; }

            var end = MatchingBrace(source, i);
            // Nothing later in a truncated payload can close either, so the scan stops here.
            if (end < 0) break;

            spans.Add(source[i..(end + 1)]);
            i = end + 1;
        }

        return spans;
    }

    private static int MatchingBrace(string s, int start)
    {
        var depth = 0;
        var inString = false;

        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];

            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }

    // ── Which object is the lot ──────────────────────────────────────────────────────────────

    /// <summary>Keys whose object IS the lot on the block. The page saying so outranks every other
    /// signal on the object combined.</summary>
    private static readonly HashSet<string> OnTheBlockKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "activeListing", "currentListing", "activeLot", "currentLot", "liveListing",
        "nowSelling", "currentItem", "activeItem", "listingOnBlock",
    };

    /// <summary>Keys that name a lot without saying it is the one on screen.</summary>
    private static readonly HashSet<string> LotKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "listing", "lot", "item", "product", "auction",
    };

    /// <summary>Keys naming a lot the show is pointing at rather than selling.</summary>
    private static readonly HashSet<string> PinnedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pinnedListing", "featuredListing", "nextListing", "upcomingListing",
    };

    /// <summary>Keys naming a lot that has already been and gone.</summary>
    private static readonly HashSet<string> PastKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "previousListing", "lastListing", "soldListing", "recentListing", "lastSold",
    };

    /// <summary>
    /// Objects that carry a <c>name</c> and are emphatically not lots. <c>name</c> is on users,
    /// categories, shipping profiles and half of a framework's own objects; reading it as a title
    /// wherever it appears is how a read prices the seller's username.
    /// </summary>
    private static readonly HashSet<string> NotALotKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "seller", "buyer", "owner", "host", "viewer", "profile", "account",
        "category", "categories", "subcategory", "tag", "tags", "badge", "badges",
        "shipping", "shippingProfile", "address", "location", "payment", "currency",
        "image", "images", "media", "thumbnail", "photo", "photos", "video",
        "props", "pageProps", "page", "meta", "metadata", "config", "settings", "app",
        "chat", "message", "messages", "comment", "comments", "stream", "liveStream", "show",
    };

    private static readonly string[] TitleKeys = ["title", "name", "productName", "itemName"];

    /// <summary>Price keys in the order a live number beats a stale one, each also tried with a
    /// <c>Cents</c> suffix. Order is the whole ranking: what somebody has BID outranks what the lot
    /// is listed at, and both outrank where the bidding opens.</summary>
    private static readonly string[] LiveBidKeys =
    [
        "currentBid", "currentPrice", "highestBid", "currentBidAmount", "bidAmount",
        "lastBid", "winningBid", "price", "salePrice", "listingPrice", "buyItNowPrice",
    ];

    private static readonly string[] OpeningBidKeys =
    [
        "startingPrice", "startPrice", "startingBid", "openingBid", "openingPrice",
        "minimumBid", "minBid", "reservePrice",
    ];

    private static readonly string[] ImageKeys =
    [
        "imageUrl", "image", "thumbnailUrl", "thumbnail", "photoUrl", "coverImageUrl", "coverUrl",
    ];

    private static readonly string[] ActiveWords =
    [
        "ACTIVE", "LIVE", "OPEN", "SELLING", "IN_PROGRESS", "INPROGRESS", "CURRENT", "RUNNING", "BIDDING",
    ];

    private static readonly string[] SoldWords =
    [
        "SOLD", "ENDED", "CLOSED", "COMPLETE", "COMPLETED", "FINISHED", "PURCHASED", "WON", "EXPIRED",
    ];

    private sealed class Candidate
    {
        public JsonElement Node;
        public string Path = "";
        public string Key = "";
        public int Rank;
        public int Order;
    }

    // ── The read ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lot on the block, off the show's page. Never throws: everything this touches is somebody
    /// else's document and the caller is a screen with a hammer coming down on it.
    /// </summary>
    public static WhatnotShowRead Read(string? showUrl, string? html)
    {
        var read = new WhatnotShowRead { Url = Normalize(showUrl) };

        var candidates = new List<Candidate>();
        var nodes = 0;

        foreach (var blob in JsonBlobs(html))
        {
            foreach (var span in ObjectSpans(blob))
            {
                if (nodes >= MaxNodes || candidates.Count >= MaxCandidates) break;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(span); }
                catch (JsonException) { continue; }   // a JS object, not a JSON one

                using (doc) Collect(doc.RootElement.Clone(), "", "", 0, candidates, ref nodes);
            }
        }

        var best = candidates
            .OrderByDescending(c => c.Rank)
            .ThenBy(c => c.Order)
            .FirstOrDefault();

        return best is null ? NothingOnTheBlock(read) : Describe(read, best);
    }

    /// <summary>Walks one parsed payload, keeping every object that could be a lot for sale.</summary>
    private static void Collect(
        JsonElement node, string path, string key, int depth, List<Candidate> found, ref int nodes)
    {
        if (depth > MaxDepth || nodes >= MaxNodes || found.Count >= MaxCandidates) return;
        nodes++;

        if (node.ValueKind == JsonValueKind.Object)
        {
            var rank = RankOf(key, node);
            if (rank > 0)
                found.Add(new Candidate { Node = node, Path = path, Key = key, Rank = rank, Order = found.Count });

            foreach (var property in node.EnumerateObject())
            {
                if (nodes >= MaxNodes || found.Count >= MaxCandidates) return;
                var childPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                Collect(property.Value, childPath, property.Name, depth + 1, found, ref nodes);
            }

            return;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (nodes >= MaxNodes || found.Count >= MaxCandidates) return;
                // Array items keep their array's key: a lot inside "listings" is still a listing.
                Collect(item, path, key, depth + 1, found, ref nodes);
            }
        }
    }

    /// <summary>
    /// How much this object looks like the lot on the block. Zero means it is not a lot at all, and
    /// that is the judgement doing the real work here — see <see cref="NotALotKeys"/>.
    /// </summary>
    private static int RankOf(string key, JsonElement node)
    {
        var byKey =
            OnTheBlockKeys.Contains(key) ? 30 :
            PinnedKeys.Contains(key) ? 10 :
            PastKeys.Contains(key) ? 2 :
            LotKeys.Contains(key) ? 15 :
            -1;

        if (byKey < 0)
        {
            // Not named as a lot. It can still be one — but only on its own evidence, and never on
            // a bare name, and never where the key says it is a person or a category.
            if (NotALotKeys.Contains(key)) return 0;

            var typed = TypeNameLooksLikeALot(node);
            if (!typed && !(HasAny(node, TitleKeys) && HasAnyPrice(node))) return 0;

            byKey = typed ? 12 : 5;
        }

        var rank = byKey + StatusRank(node);

        // A lot with a name worth searching on beats one without, all else equal — but not by
        // enough to outrank the page saying which lot is live.
        if (RawTitleOf(node).Length > 0) rank += 8;

        return rank;
    }

    private static bool TypeNameLooksLikeALot(JsonElement node)
    {
        if (!TryProperty(node, "__typename", out var typename) || typename.ValueKind != JsonValueKind.String)
            return false;

        var name = typename.GetString() ?? "";
        return name.EndsWith("Listing", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Lot", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Product", StringComparison.OrdinalIgnoreCase);
    }

    private static int StatusRank(JsonElement node)
    {
        var status = StatusOf(node);
        if (status.Length == 0) return 20;
        if (ActiveWords.Any(w => status.Equals(w, StringComparison.OrdinalIgnoreCase))) return 40;
        if (SoldWords.Any(w => status.Equals(w, StringComparison.OrdinalIgnoreCase))) return 0;
        return 20;
    }

    private static string StatusOf(JsonElement node)
    {
        foreach (var key in new[] { "status", "state", "listingStatus" })
            if (TryProperty(node, key, out var value) && value.ValueKind == JsonValueKind.String)
                return (value.GetString() ?? "").Trim();

        return "";
    }

    // ── What the chosen lot says ─────────────────────────────────────────────────────────────

    private static WhatnotShowRead Describe(WhatnotShowRead read, Candidate best)
    {
        var node = best.Node;
        var where = best.Path.Length > 0 ? best.Path : best.Key;

        var raw = RawTitleOf(node, out var titleKey);
        read.RawTitle = raw;

        var (clean, openingFromTitle) = LiveLotList.Clean(raw);

        if (TryProperty(node, "id", out var id))
        {
            read.ListingId = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString() ?? "",
                JsonValueKind.Number => id.ToString(),
                _ => "",
            };
        }

        // Not searchable. The same bar a pasted lot line has to clear, shared rather than restated:
        // a lot called "#12" is one the sold search would answer about at random, and a random
        // answer on this screen is a bid.
        if (clean.Length < LiveLotList.MinTitleLength || !clean.Any(char.IsLetter))
        {
            read.Status = WhatnotReadStatuses.NoListing;
            read.Headline = "Found the lot, but not a name to search on.";
            read.Detail = raw.Length > 0
                ? $"The lot on that page is called \"{raw}\", and there is nothing in that for a sold " +
                  "search to match on — it would answer about something at random, and a random " +
                  "answer on this screen is a bid."
                : "The lot on that page has no name on it yet, which usually means the show is " +
                  "between lots.";
            read.Hint = TypeItInstead;
            if (read.ListingId.Length > 0) read.Evidence.Add($"Listing id {read.ListingId}, at {where}");
            return read;
        }

        read.Title = Shorten(clean, read);
        read.Status = WhatnotReadStatuses.Read;
        read.Evidence.Add($"{where}.{titleKey} — \"{raw}\"");
        if (read.ListingId.Length > 0) read.Evidence.Add($"Listing id {read.ListingId}, at {where}");

        // What was changed is said, because a clean that dropped the wrong half of a name is only
        // findable next to the original.
        if (!string.Equals(clean, raw, StringComparison.Ordinal))
            read.Warnings.Add($"Read as \"{read.Title}\" from \"{raw}\" — the lot number and the asking price aren't part of what the thing is.");

        ReadPrice(read, node, where, openingFromTitle, titleKey);
        ReadExtras(read, node, where);

        var status = StatusOf(node);
        if (SoldWords.Any(w => status.Equals(w, StringComparison.OrdinalIgnoreCase)))
        {
            read.Warnings.Add($"The page marks this lot {status.ToUpperInvariant()} — it just went. " +
                              "What's on screen now is the next one, so read the show again before you bid.");
        }

        if (LooksLikeAGiveaway(read.Title))
        {
            read.Warnings.Add("This looks like a giveaway rather than a lot for sale — there's nothing " +
                              "to arbitrage, and the comps below will be about the wrong thing.");
        }

        read.Headline = read.CurrentBid is { } bid
            ? $"On the block: {read.Title} — {bid:C}"
            : $"On the block: {read.Title}";

        // Said on every successful read and not only on the awkward ones: a seller who learns this
        // limit from a screen that repeats it will not be caught out by it at $200.
        read.Detail = "Read off the show's page — a snapshot, not a live feed. The bid can move " +
                      "under your hand between reads, so read it again when the number matters. " +
                      "What it's worth comes from eBay sold comps, below.";
        read.Hint = "";
        return read;
    }

    /// <summary>
    /// The number for the bid box. A live bid beats an opening price, an opening price beats
    /// nothing, and the one thing that never happens is a number being divided by 100 because it
    /// looked large: guessing that is how a $1,299 lot becomes a $12.99 one on the screen that
    /// decides what to bid, and the mistake is invisible because $12.99 is a plausible price.
    /// </summary>
    private static void ReadPrice(
        WhatnotShowRead read, JsonElement node, string where, decimal? openingFromTitle, string titleKey)
    {
        foreach (var key in LiveBidKeys)
        {
            if (!TryMoney(node, key, out var amount, out var usedKey)) continue;
            read.CurrentBid = amount;
            read.BidKey = usedKey;
            read.Evidence.Add($"{where}.{usedKey} — {amount.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        foreach (var key in OpeningBidKeys)
        {
            if (!TryMoney(node, key, out var amount, out var usedKey)) continue;
            read.CurrentBid = amount;
            read.BidKey = usedKey;
            read.BidIsOpeningPrice = true;
            read.Evidence.Add($"{where}.{usedKey} — {amount.ToString(CultureInfo.InvariantCulture)}");
            OpeningPriceWarning(read, amount);
            return;
        }

        // Nothing priced on the object, but the lot's own name may carry the asking price — the
        // same place a pasted lot line carries it, read the same way.
        if (openingFromTitle is { } fromTitle)
        {
            read.CurrentBid = fromTitle;
            read.BidKey = titleKey;
            read.BidIsOpeningPrice = true;
            read.Evidence.Add($"{where}.{titleKey} — the asking price was written into the lot's name");
            OpeningPriceWarning(read, fromTitle);
            return;
        }

        read.Warnings.Add("No price on the page — the ceiling below still answers, it just can't say " +
                          "how much room is left in it. Type what it's standing at into the bid box.");
    }

    private static void OpeningPriceWarning(WhatnotShowRead read, decimal amount) =>
        read.Warnings.Add($"{amount:C} is where the bidding STARTS, not what anyone has bid. It's the " +
                          "right starting point for the ceiling — just don't read it as the live number.");

    private static void ReadExtras(WhatnotShowRead read, JsonElement node, string where)
    {
        foreach (var key in ImageKeys)
        {
            if (!TryProperty(node, key, out var value)) continue;

            var url = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Object => TryProperty(value, "url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() ?? ""
                    : TryProperty(value, "src", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() ?? ""
                        : "",
                _ => "",
            };

            // https only. The app's own page is served over http on localhost, but a photo pulled
            // from a plain-http address is one anything on the wire can replace, and this one is
            // about to be shown as evidence of what the seller is bidding on.
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                read.ImageUrl = url;
                read.Evidence.Add($"{where}.{key} — the lot's photo");
            }

            break;
        }

        if (TryProperty(node, "category", out var category))
        {
            read.CategoryHint = category.ValueKind switch
            {
                JsonValueKind.String => category.GetString() ?? "",
                JsonValueKind.Object => TryProperty(category, "name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "",
                _ => "",
            };
        }
        else if (TryProperty(node, "categoryName", out var named) && named.ValueKind == JsonValueKind.String)
        {
            read.CategoryHint = named.GetString() ?? "";
        }

        if (read.CategoryHint.Length > 0)
            read.Evidence.Add($"{where}.category — {read.CategoryHint}");
    }

    private static WhatnotShowRead NothingOnTheBlock(WhatnotShowRead read)
    {
        read.Status = WhatnotReadStatuses.NoListing;
        read.Headline = "Nothing on the block on that page.";
        // "Between lots" is the normal state of a live show. Said as a failure it would send the
        // seller looking for a bug that is not there.
        read.Detail = "No lot for sale was in that page. A live show is between lots a good deal of " +
                      "the time and says nothing while it is, so this is usually worth one more press " +
                      "a few seconds later.";
        read.Hint = TypeItInstead;
        return read;
    }

    // ── Reading fields off an object ─────────────────────────────────────────────────────────

    private static string RawTitleOf(JsonElement node) => RawTitleOf(node, out _);

    private static string RawTitleOf(JsonElement node, out string usedKey)
    {
        foreach (var key in TitleKeys)
        {
            if (!TryProperty(node, key, out var value) || value.ValueKind != JsonValueKind.String) continue;

            var text = (value.GetString() ?? "").Trim();
            if (text.Length == 0) continue;

            usedKey = key;
            return text;
        }

        usedKey = "";
        return "";
    }

    /// <summary>Cuts a name to something a sold search can run on, and says so when it had to.</summary>
    private static string Shorten(string title, WhatnotShowRead read)
    {
        if (title.Length <= MaxTitleLength) return title;

        var cut = title[..MaxTitleLength];
        var space = cut.LastIndexOf(' ');
        if (space > MaxTitleLength / 2) cut = cut[..space];
        cut = cut.TrimEnd();

        read.Warnings.Add($"That lot's name ran to {title.Length} characters, so the search runs on the " +
                          "first part of it. Trim it yourself if it cut the model number off.");
        return cut;
    }

    private static bool LooksLikeAGiveaway(string title)
    {
        var text = title ?? "";
        return text.Contains("giveaway", StringComparison.OrdinalIgnoreCase)
            || text.Contains("give away", StringComparison.OrdinalIgnoreCase)
            || text.Contains("free entry", StringComparison.OrdinalIgnoreCase)
            || text.Contains("follow to win", StringComparison.OrdinalIgnoreCase)
            || text.Contains("raffle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAny(JsonElement node, string[] keys) =>
        keys.Any(k => TryProperty(node, k, out var v) && v.ValueKind == JsonValueKind.String
                      && (v.GetString() ?? "").Trim().Length > 0);

    private static bool HasAnyPrice(JsonElement node) =>
        LiveBidKeys.Concat(OpeningBidKeys).Any(k => TryMoney(node, k, out _, out _));

    /// <summary>Case-insensitive property lookup — the page's casing is Whatnot's to change.</summary>
    private static bool TryProperty(JsonElement node, string name, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (!property.NameEquals(name)
                    && !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// A price off one key, in every shape a page writes one: a number, a money string, a nested
    /// <c>{amount}</c> object, and the same three again with the key saying cents.
    /// </summary>
    private static bool TryMoney(JsonElement node, string key, out decimal amount, out string usedKey)
    {
        amount = 0m;
        usedKey = "";

        foreach (var (name, cents) in new[] { (key, false), (key + "Cents", true), (key + "InCents", true) })
        {
            if (!TryProperty(node, name, out var value)) continue;
            if (!TryAmount(value, cents, out var found)) continue;

            amount = found;
            usedKey = name;
            return true;
        }

        return false;
    }

    private static bool TryAmount(JsonElement value, bool keySaysCents, out decimal amount)
    {
        amount = 0m;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var number)) return false;
                amount = keySaysCents ? number / 100m : number;
                break;

            case JsonValueKind.String:
                if (!TryParseMoney(value.GetString(), out var parsed)) return false;
                amount = keySaysCents ? parsed / 100m : parsed;
                break;

            case JsonValueKind.Object:
                // { amount, currency } and { amountCents } — the inner key decides cents, not the
                // outer one, and not the size of the number.
                foreach (var (inner, cents) in new[]
                         {
                             ("amount", keySaysCents), ("value", keySaysCents), ("price", keySaysCents),
                             ("amountCents", true), ("valueCents", true), ("cents", true),
                         })
                {
                    if (!TryProperty(value, inner, out var child)) continue;
                    if (!TryAmount(child, cents, out var found)) continue;

                    amount = found;
                    return amount > 0m;
                }
                return false;

            default:
                return false;
        }

        // Zero and negative are not prices. Reported as "no price" rather than shown, because a
        // ceiling beside "$0.00" reads as a lot nobody has bid on.
        return amount > 0m;
    }

    private static bool TryParseMoney(string? text, out decimal amount)
    {
        amount = 0m;

        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return false;

        var digits = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c == '.') digits.Append(c);
            else if (c == '-' && digits.Length == 0) digits.Append(c);
            // Group separators are dropped rather than interpreted: "1,299.00" and "1.299,00" mean
            // different things and nothing on the page says which convention it used.
        }

        return decimal.TryParse(digits.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}
