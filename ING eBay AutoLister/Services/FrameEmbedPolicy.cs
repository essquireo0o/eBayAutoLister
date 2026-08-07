using System.Net;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Answers one question for the WhatsNot panel's embedded browser: <b>will a browser let this app
/// put that site in an iframe?</b>
/// </summary>
/// <remarks>
/// <para>
/// A site refuses framing with <c>X-Frame-Options</c> or with a CSP <c>frame-ancestors</c>
/// directive, and when it does the browser blocks the load and tells the embedding page
/// <i>nothing</i> — no error, no event, no readable document. The frame is simply blank, which is
/// indistinguishable from a slow feed, a signed-out feed, or a bug in this app. Sellers have been
/// told "if the frame stays blank, that's the site refusing"; this replaces that guess with the
/// site's own headers, fetched by the app, which has no such restriction.
/// </para>
/// <para>
/// The verdict is read from a live response wherever possible. <see cref="KnownRefusal"/> is only
/// the fallback for when the probe can't reach one — a remembered list is a claim about the past,
/// and sites do change their headers, so it must never outrank what the site said just now.
/// </para>
/// <para>
/// Nothing here reads a response body. The probe wants six headers and a status line, so it stops
/// at the headers and disposes the response — a live-stream page is megabytes and this screen's
/// whole promise is an answer in about a second.
/// </para>
/// </remarks>
public sealed class FrameEmbedPolicy(IHttpClientFactory httpFactory)
{
    /// <summary>Short on purpose. This runs beside a frame that is already loading; a check that
    /// outlasts the frame it is describing has missed its moment.</summary>
    public const int ProbeTimeoutSeconds = 6;

    /// <summary>
    /// Sites whose refusal to be framed is a standing fact, kept only for when the probe cannot get
    /// an answer of its own (these are exactly the sites whose CDNs turn a non-browser request away
    /// with a 403). Keyed by registrable domain; the value is what to call the site in a sentence.
    /// </summary>
    private static readonly Dictionary<string, string> KnownRefusers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whatnot.com"] = "Whatnot",
        ["ebay.com"] = "eBay",
        ["ebay.co.uk"] = "eBay",
        ["facebook.com"] = "Facebook",
        ["instagram.com"] = "Instagram",
        ["tiktok.com"] = "TikTok",
        ["x.com"] = "X",
        ["twitter.com"] = "Twitter",
        ["amazon.com"] = "Amazon",
        ["poshmark.com"] = "Poshmark",
        ["mercari.com"] = "Mercari",
        ["etsy.com"] = "Etsy",
        ["offerup.com"] = "OfferUp",
        ["craigslist.org"] = "Craigslist",
        ["google.com"] = "Google",
        ["youtube.com"] = "YouTube",
    };

    // ── The address ───────────────────────────────────────────────────────────

    /// <summary>
    /// What the seller typed, turned into the address the frame will actually be pointed at — the
    /// same normalisation the browser does, done here so the check and the frame cannot disagree
    /// about which site was asked.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? text
            : "https://" + text;
    }

    /// <summary>
    /// Whether this is an address worth asking about. Rejects everything that isn't a public web
    /// page, because the app is about to make the request itself: a <c>file:</c> URL, a bare
    /// machine name or an address on the local network would turn the panel's address bar into a
    /// way to make this machine fetch things that are not on the web.
    /// </summary>
    /// <remarks>
    /// Host names are judged by what they look like, not by what they resolve to — the app does not
    /// resolve first and fetch second, so a name that resolves to a private address still gets
    /// through. That is the same exposure the browser next to it has, and the response body is
    /// never read or returned: this reports six headers and a status code.
    /// </remarks>
    public static (bool Ok, string Reason) Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "That doesn't look like a web address.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (false, "Only http and https addresses can be loaded here.");

        if (uri.IsLoopback)
            return (false, "That address points back at this machine, not at a live feed.");

        var host = uri.Host;
        if (host.Length == 0)
            return (false, "That address has no site in it.");

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IsPrivate(ip))
                return (false, "That address is on the local network, not on the web.");
        }
        else if (!host.Contains('.'))
        {
            // "router", "intranet", "nas" — a single-label name only resolves on a local network.
            return (false, "That looks like a machine on your network rather than a website.");
        }

        return (true, "");
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                169 when b[1] == 254 => true,           // link-local
                172 when b[1] >= 16 && b[1] <= 31 => true,
                192 when b[1] == 168 => true,
                _ => false,
            };
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;      // unique local fc00::/7
            if (ip.IsIPv4MappedToIPv6) return IsPrivate(ip.MapToIPv4());
        }

        return false;
    }

    /// <summary>The site's name when its refusal is already known, else null.</summary>
    public static string? KnownRefusal(string host)
    {
        var name = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (name.StartsWith("www.", StringComparison.Ordinal)) name = name[4..];
        if (name.Length == 0) return null;

        if (KnownRefusers.TryGetValue(name, out var label)) return label;

        foreach (var (domain, site) in KnownRefusers)
            if (name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return site;

        return null;
    }

    // ── The headers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The verdict a browser would reach from these headers, for an embed attempted by
    /// <b>this app</b> — which is a different origin from every site this panel can load, so
    /// "same origin only" is a refusal here even though it is permission for somebody.
    /// </summary>
    /// <param name="xFrameOptions">Every <c>X-Frame-Options</c> value on the response.</param>
    /// <param name="contentSecurityPolicy">Every <c>Content-Security-Policy</c> value.</param>
    public static (string Status, string Header, string Reason) Read(
        IEnumerable<string>? xFrameOptions, IEnumerable<string>? contentSecurityPolicy)
    {
        // CSP first, and it settles it: where a response carries frame-ancestors, browsers use it
        // and ignore X-Frame-Options entirely. Reading them the other way round would report DENY
        // on a site whose CSP had just granted the frame.
        foreach (var policy in contentSecurityPolicy ?? Array.Empty<string>())
        {
            var ancestors = FrameAncestors(policy);
            if (ancestors is null) continue;

            if (PermitsThisApp(ancestors))
            {
                return (FrameEmbedStatuses.Allowed,
                        "Content-Security-Policy: frame-ancestors " + string.Join(' ', ancestors),
                        "its Content-Security-Policy allows framing");
            }

            var sources = ancestors.Count == 0 || ancestors is ["'none'"]
                ? "nothing"
                : string.Join(", ", ancestors);
            return (FrameEmbedStatuses.Refused,
                    "Content-Security-Policy: frame-ancestors " + string.Join(' ', ancestors),
                    $"its Content-Security-Policy allows framing by {sources}");
        }

        foreach (var raw in xFrameOptions ?? Array.Empty<string>())
        {
            // Servers sometimes emit the same value twice, which arrives as "DENY, DENY".
            foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var value = part.Trim();
                if (value.Equals("DENY", StringComparison.OrdinalIgnoreCase))
                    return (FrameEmbedStatuses.Refused, "X-Frame-Options: DENY", "it sends X-Frame-Options: DENY");

                if (value.Equals("SAMEORIGIN", StringComparison.OrdinalIgnoreCase))
                {
                    return (FrameEmbedStatuses.Refused, "X-Frame-Options: SAMEORIGIN",
                            "it sends X-Frame-Options: SAMEORIGIN, which only lets the site frame itself");
                }

                if (value.StartsWith("ALLOW-FROM", StringComparison.OrdinalIgnoreCase))
                {
                    // Obsolete and ignored by every current browser, which means the header grants
                    // nothing — but it is a clear statement of intent, so it is reported as one.
                    return (FrameEmbedStatuses.Refused, "X-Frame-Options: " + value,
                            "it sends X-Frame-Options: " + value);
                }

                // Anything else is a malformed value, and a browser ignores malformed values.
            }
        }

        return (FrameEmbedStatuses.Allowed, "", "nothing in its headers refuses framing");
    }

    /// <summary>The sources of a <c>frame-ancestors</c> directive, or null when the policy has none.</summary>
    private static List<string>? FrameAncestors(string? policy)
    {
        foreach (var directive in (policy ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = directive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (!parts[0].Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase)) continue;
            return [.. parts.Skip(1)];
        }
        return null;
    }

    /// <summary>
    /// Whether a frame-ancestors source list lets THIS app frame the page. The app is served from
    /// localhost, so only a wildcard or an explicit localhost source does; <c>'self'</c> and a list
    /// of the site's own domains are permission for somebody else.
    /// </summary>
    private static bool PermitsThisApp(List<string> sources)
    {
        foreach (var source in sources)
        {
            var s = source.Trim().Trim('\'');
            if (s == "*") return true;
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
            else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];

            if (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("127.0.0.1", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // ── What is worth remembering ─────────────────────────────────────────────

    /// <summary>
    /// Whether a verdict is evidence the panel may keep and act on the next time this host comes
    /// up — meaning: skip pointing the frame at it and explain the refusal straight away.
    /// </summary>
    /// <remarks>
    /// The distinction is between what a site <i>said</i> and what this check <i>failed to find
    /// out</i>. A refusal read off the site's own headers, and a refusal from the standing list, are
    /// both statements about the site. "Couldn't reach it" and "that isn't an address" are
    /// statements about this request, and a remembered timeout would turn one bad minute into a
    /// site the panel quietly declines to load for a week.
    /// </remarks>
    public static bool IsWorthRemembering(string? status, string? source) => status switch
    {
        FrameEmbedStatuses.Refused => source is "headers" or "known",
        FrameEmbedStatuses.Allowed => source is "headers",
        _ => false,
    };

    // ── The check ─────────────────────────────────────────────────────────────

    /// <summary>Asks the site itself, and turns the answer into something the panel can say out loud.</summary>
    public async Task<FrameEmbedCheck> CheckAsync(string? rawUrl, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var url = Normalize(rawUrl);
        var result = new FrameEmbedCheck { Url = url };

        if (url.Length == 0)
        {
            result.Status = FrameEmbedStatuses.Invalid;
            result.Source = "validation";
            result.Headline = "No address to load.";
            result.Detail = "Type a feed address, or press ⌂ for Whatnot's live page.";
            return result;
        }

        var (ok, reason) = Validate(url);
        if (!ok)
        {
            result.Status = FrameEmbedStatuses.Invalid;
            result.Source = "validation";
            result.Headline = reason;
            result.Detail = "The address bar takes public http and https pages.";
            return result;
        }

        var uri = new Uri(url);
        result.Host = uri.Host;
        var site = DisplayName(uri.Host);

        try
        {
            var http = httpFactory.CreateClient();
            PublicFeedHttp.ApplyBrowserHeaders(http);
            http.Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            result.HttpStatus = (int)response.StatusCode;

            IEnumerable<string> xfo =
                response.Headers.TryGetValues("X-Frame-Options", out var x) ? x : Array.Empty<string>();
            // Sent as a response header, but a stray server puts it on the entity, so both are read.
            var csp = new List<string>();
            if (response.Headers.TryGetValues("Content-Security-Policy", out var c)) csp.AddRange(c);
            if (response.Content.Headers.TryGetValues("Content-Security-Policy", out var cc)) csp.AddRange(cc);

            var (status, header, why) = Read(xfo, csp);

            if (status == FrameEmbedStatuses.Refused)
            {
                Refuse(result, site, why, header, "headers");
                return result;
            }

            // No refusing header — but a page the app was not allowed to read is a page whose real
            // headers were never seen. Saying "allowed" off a CDN's block page is how the panel
            // would end up promising a frame that then sits blank.
            if (!response.IsSuccessStatusCode)
            {
                if (KnownRefusal(uri.Host) is { } known)
                {
                    Refuse(result, known,
                        $"it turned this check away with HTTP {(int)response.StatusCode}, and it is known to refuse being framed",
                        "", "known");
                    return result;
                }

                result.Status = FrameEmbedStatuses.Unknown;
                result.Source = "unreachable";
                result.Headline = $"Couldn't tell whether {site} allows embedding.";
                result.Detail = $"It answered this check with HTTP {(int)response.StatusCode} — several of these " +
                                "sites turn away anything that isn't a signed-in browser. The frame may still " +
                                "load; if it stays blank, use Open in browser.";
                return result;
            }

            result.Status = FrameEmbedStatuses.Allowed;
            result.Source = "headers";
            result.Header = header;
            result.Headline = $"{site} allows embedding.";
            result.Detail = "Its headers don't refuse framing, so the frame should render. A page that " +
                            "needs a sign-in can still come up empty — a frame carries none of your logins.";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Timed out, DNS failed, TLS refused. If the site is one whose refusal is already known,
            // say so rather than shrugging; otherwise say plainly that the check didn't land.
            if (KnownRefusal(uri.Host) is { } known)
            {
                Refuse(result, known, "this check couldn't reach it, and it is known to refuse being framed", "", "known");
                return result;
            }

            result.Status = FrameEmbedStatuses.Unknown;
            result.Source = "unreachable";
            result.Headline = $"Couldn't reach {site} to check.";
            result.Detail = ex is OperationCanceledException or TaskCanceledException
                ? $"The check gave up after {ProbeTimeoutSeconds} seconds. The frame may still load."
                : "The check didn't get an answer. The frame may still load; if it stays blank, use Open in browser.";
            return result;
        }
        finally
        {
            result.ElapsedMs = sw.ElapsedMilliseconds;
            // Set in one place, on every path out of the probe, so a new verdict added later cannot
            // become rememberable by accident — the two early returns above are both "invalid",
            // which this would answer false for anyway.
            result.Remember = IsWorthRemembering(result.Status, result.Source);
        }
    }

    private static void Refuse(FrameEmbedCheck result, string site, string why, string header, string source)
    {
        result.Status = FrameEmbedStatuses.Refused;
        result.Source = source;
        result.Header = header;
        result.Headline = $"{site} refuses to be embedded.";
        // Both halves matter: whose decision this is, and what still works. A seller who reads only
        // "blocked" concludes the app is broken and stops using the screen that does work.
        result.Detail = $"This frame will stay blank — {why}. Open in browser is the way in, and the " +
                        "card above still prices what's on screen: type the item and press Enter.";
    }

    /// <summary>What to call the site in a sentence — its known name, else its bare host.</summary>
    private static string DisplayName(string host)
    {
        if (KnownRefusal(host) is { } known) return known;
        var name = (host ?? "").Trim().TrimEnd('.');
        return name.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }
}
