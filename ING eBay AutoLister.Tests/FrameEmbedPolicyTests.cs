using System.Net;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot browser panel's one unanswerable question: <b>why is the frame blank?</b>
///
/// A browser blocks a refused embed before anything renders and tells the embedding page nothing,
/// so the panel has to get the answer from the site's own headers. Every rule below is a rule a
/// browser applies, and getting one backwards produces the worst possible outcome for this screen —
/// a confident sentence about somebody else's server that is wrong.
/// </summary>
public class FrameEmbedPolicyTests
{
    // ── X-Frame-Options ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("DENY")]
    [InlineData("deny")]
    [InlineData(" DENY ")]
    public void Deny_is_a_refusal(string value)
    {
        var (status, header, _) = FrameEmbedPolicy.Read([value], null);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
        Assert.Contains("DENY", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAMEORIGIN is permission — for the site itself. This app is served from localhost and is a
    /// different origin from every site the panel can load, so for this frame it is a refusal.
    /// Reading it as "allowed" is how the panel would promise a frame that then sits blank.
    /// </summary>
    [Fact]
    public void Sameorigin_is_a_refusal_because_this_app_is_never_the_sites_own_origin()
    {
        var (status, _, reason) = FrameEmbedPolicy.Read(["SAMEORIGIN"], null);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
        Assert.Contains("only lets the site frame itself", reason, StringComparison.Ordinal);
    }

    /// <summary>Servers emit the header twice and it arrives comma-joined.</summary>
    [Fact]
    public void A_doubled_header_value_still_reads_as_one_refusal()
    {
        var (status, _, _) = FrameEmbedPolicy.Read(["DENY, DENY"], null);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
    }

    /// <summary>
    /// Browsers ignore an X-Frame-Options value they don't recognise, so this must too — a site
    /// with a typo in its header is a site that CAN be framed, and calling it refused would send
    /// the seller to a second browser for no reason.
    /// </summary>
    [Fact]
    public void A_malformed_value_is_ignored_exactly_as_a_browser_ignores_it()
    {
        var (status, _, _) = FrameEmbedPolicy.Read(["ALLOWALL"], null);

        Assert.Equal(FrameEmbedStatuses.Allowed, status);
    }

    [Fact]
    public void Allow_from_is_reported_as_the_refusal_it_amounts_to()
    {
        var (status, header, _) = FrameEmbedPolicy.Read(["ALLOW-FROM https://example.com"], null);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
        Assert.Contains("ALLOW-FROM", header, StringComparison.Ordinal);
    }

    [Fact]
    public void No_headers_at_all_means_nothing_refuses_the_frame()
    {
        var (status, header, reason) = FrameEmbedPolicy.Read(null, null);

        Assert.Equal(FrameEmbedStatuses.Allowed, status);
        Assert.Equal("", header);
        Assert.Contains("nothing in its headers", reason, StringComparison.Ordinal);
    }

    // ── CSP frame-ancestors ───────────────────────────────────────────────────

    [Fact]
    public void Frame_ancestors_none_is_a_refusal()
    {
        var (status, header, _) = FrameEmbedPolicy.Read(null, ["default-src 'self'; frame-ancestors 'none'"]);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
        Assert.Contains("frame-ancestors 'none'", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_ancestors_self_is_a_refusal_for_this_app()
    {
        var (status, _, _) = FrameEmbedPolicy.Read(null, ["frame-ancestors 'self'"]);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
    }

    [Fact]
    public void A_list_that_does_not_include_this_app_is_a_refusal()
    {
        var (status, _, reason) = FrameEmbedPolicy.Read(
            null, ["frame-ancestors https://partner.example.com https://*.example.org"]);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
        Assert.Contains("partner.example.com", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frame-ancestors *")]
    [InlineData("frame-ancestors 'self' http://localhost:9332")]
    [InlineData("frame-ancestors http://127.0.0.1:9332")]
    public void A_list_that_includes_this_app_allows_the_frame(string policy)
    {
        var (status, _, _) = FrameEmbedPolicy.Read(null, [policy]);

        Assert.Equal(FrameEmbedStatuses.Allowed, status);
    }

    /// <summary>
    /// Where a response carries frame-ancestors, browsers use it and ignore X-Frame-Options
    /// entirely. Reading them the other way round would report DENY on a site whose CSP had just
    /// granted the frame — a refusal invented by this app, about a page that would have loaded.
    /// </summary>
    [Fact]
    public void Frame_ancestors_beats_x_frame_options_the_way_a_browser_does()
    {
        var (status, header, _) = FrameEmbedPolicy.Read(["DENY"], ["frame-ancestors *"]);

        Assert.Equal(FrameEmbedStatuses.Allowed, status);
        Assert.Contains("frame-ancestors", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_with_no_frame_ancestors_directive_leaves_the_decision_to_x_frame_options()
    {
        var (refused, _, _) = FrameEmbedPolicy.Read(["DENY"], ["default-src 'self'; script-src 'self'"]);
        var (allowed, _, _) = FrameEmbedPolicy.Read(null, ["default-src 'self'"]);

        Assert.Equal(FrameEmbedStatuses.Refused, refused);
        Assert.Equal(FrameEmbedStatuses.Allowed, allowed);
    }

    [Fact]
    public void Directives_are_matched_case_insensitively_and_around_whitespace()
    {
        var (status, _, _) = FrameEmbedPolicy.Read(null, ["  Frame-Ancestors   'none'  ; default-src 'self'"]);

        Assert.Equal(FrameEmbedStatuses.Refused, status);
    }

    /// <summary>A directive whose name merely starts the same way is a different directive.</summary>
    [Fact]
    public void Frame_src_is_not_frame_ancestors()
    {
        var (status, _, _) = FrameEmbedPolicy.Read(null, ["frame-src 'none'"]);

        Assert.Equal(FrameEmbedStatuses.Allowed, status);
    }

    // ── The address ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("whatnot.com/live", "https://whatnot.com/live")]
    [InlineData("  www.whatnot.com  ", "https://www.whatnot.com")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("HTTPS://Example.com", "HTTPS://Example.com")]
    public void A_typed_address_gets_the_scheme_the_browser_would_assume(string typed, string expected)
    {
        Assert.Equal(expected, FrameEmbedPolicy.Normalize(typed));
    }

    [Fact]
    public void An_empty_address_normalizes_to_nothing_rather_than_to_a_scheme()
    {
        Assert.Equal("", FrameEmbedPolicy.Normalize("   "));
        Assert.Equal("", FrameEmbedPolicy.Normalize(null));
    }

    /// <summary>
    /// The app makes this request itself, so the address bar must not become a way to point this
    /// machine at things that are not on the web.
    /// </summary>
    [Theory]
    [InlineData("https://localhost:9332/api/setup/fields")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://10.0.0.5/admin")]
    [InlineData("https://172.16.4.4/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://router/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    public void Addresses_that_are_not_public_web_pages_are_refused(string url)
    {
        var (ok, reason) = FrameEmbedPolicy.Validate(url);

        Assert.False(ok);
        Assert.NotEqual("", reason);
    }

    [Theory]
    [InlineData("https://www.whatnot.com/live")]
    [InlineData("http://example.com/page?q=1")]
    [InlineData("https://8.8.8.8/")]
    public void Public_web_addresses_are_allowed_through(string url)
    {
        var (ok, _) = FrameEmbedPolicy.Validate(url);

        Assert.True(ok);
    }

    // ── The remembered list ───────────────────────────────────────────────────

    [Theory]
    [InlineData("whatnot.com", "Whatnot")]
    [InlineData("www.whatnot.com", "Whatnot")]
    [InlineData("WWW.Whatnot.COM", "Whatnot")]
    [InlineData("web.whatnot.com", "Whatnot")]
    [InlineData("www.ebay.com", "eBay")]
    public void The_sites_whose_refusal_is_already_known_are_recognised(string host, string expected)
    {
        Assert.Equal(expected, FrameEmbedPolicy.KnownRefusal(host));
    }

    /// <summary>
    /// Suffix matching on a registrable domain, not a substring — "notwhatnot.com" and
    /// "whatnot.com.evil.test" are different sites and must not inherit the verdict.
    /// </summary>
    [Theory]
    [InlineData("notwhatnot.com")]
    [InlineData("whatnot.com.example.test")]
    [InlineData("example.com")]
    [InlineData("")]
    public void A_site_that_merely_looks_similar_is_not_on_the_list(string host)
    {
        Assert.Null(FrameEmbedPolicy.KnownRefusal(host));
    }

    // ── The check as a whole ──────────────────────────────────────────────────

    [Fact]
    public async Task An_address_that_is_not_a_web_page_is_refused_without_a_request()
    {
        var policy = new FrameEmbedPolicy(new ThrowingHttpClientFactory());

        var check = await policy.CheckAsync("http://192.168.0.1/", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Invalid, check.Status);
        Assert.Equal("validation", check.Source);
    }

    [Fact]
    public async Task An_empty_address_says_so_rather_than_probing_nothing()
    {
        var policy = new FrameEmbedPolicy(new ThrowingHttpClientFactory());

        var check = await policy.CheckAsync("", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Invalid, check.Status);
        Assert.Contains("address", check.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the probe can't get an answer, a site already known to refuse is reported as refusing —
    /// but from the remembered list, and the card says so. A live verdict and a remembered one are
    /// not the same claim.
    /// </summary>
    [Fact]
    public async Task A_known_refuser_the_probe_cannot_reach_is_still_reported_as_refusing()
    {
        var policy = new FrameEmbedPolicy(new ThrowingHttpClientFactory());

        var check = await policy.CheckAsync("https://www.whatnot.com/live", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Refused, check.Status);
        Assert.Equal("known", check.Source);
        Assert.Contains("Whatnot", check.Headline, StringComparison.Ordinal);
        // The refusal must always carry the way in, or it reads as "this app is broken".
        Assert.Contains("Open in browser", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown site the probe can't reach is "couldn't tell", never "refused". Inventing a
    /// refusal for a site that would have framed fine sends the seller out of the app for nothing.
    /// </summary>
    [Fact]
    public async Task An_unreachable_site_that_is_not_on_the_list_is_reported_as_unknown()
    {
        var policy = new FrameEmbedPolicy(new ThrowingHttpClientFactory());

        var check = await policy.CheckAsync("https://example.invalid/feed", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Unknown, check.Status);
        Assert.Equal("unreachable", check.Source);
        Assert.Contains("may still load", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>Every path through the check has to fill in the host and a sentence to show.</summary>
    [Fact]
    public async Task Every_verdict_carries_a_sentence_the_panel_can_display()
    {
        var policy = new FrameEmbedPolicy(new ThrowingHttpClientFactory());

        foreach (var url in new[] { "https://www.whatnot.com/live", "https://example.invalid/", "https://10.0.0.1/" })
        {
            var check = await policy.CheckAsync(url, CancellationToken.None);

            Assert.NotEqual("", check.Headline);
            Assert.NotEqual("", check.Status);
            Assert.NotEqual("", check.Url);
        }
    }

    /// <summary>
    /// Whatnot's own headers, as it actually served them: a CSP that names its own subdomains and
    /// an X-Frame-Options behind it. This is the case the whole screen was built around, so it is
    /// pinned against the real response rather than against an invented one.
    /// </summary>
    [Fact]
    public async Task Whatnots_real_headers_are_read_as_a_refusal_from_the_headers_themselves()
    {
        var policy = new FrameEmbedPolicy(StubHttpClientFactory.Serving(
            System.Net.HttpStatusCode.OK,
            ("content-security-policy", "frame-ancestors https://*.whatnot.com 'self'"),
            ("x-frame-options", "SAMEORIGIN")));

        var check = await policy.CheckAsync("https://www.whatnot.com/live", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Refused, check.Status);
        // From the live response, not from the remembered list — the two are different claims.
        Assert.Equal("headers", check.Source);
        Assert.Contains("frame-ancestors", check.Header, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusing header is the answer even when the response was an error. Live pages 404 and 403
    /// at a probe constantly and still carry their real framing headers; checking the status first
    /// would downgrade a certain refusal to "couldn't tell".
    /// </summary>
    [Fact]
    public async Task A_refusing_header_on_an_error_response_is_still_a_refusal()
    {
        var policy = new FrameEmbedPolicy(StubHttpClientFactory.Serving(
            System.Net.HttpStatusCode.NotFound, ("x-frame-options", "DENY")));

        var check = await policy.CheckAsync("https://example.com/gone", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Refused, check.Status);
        Assert.Equal("headers", check.Source);
    }

    /// <summary>
    /// Content-Security-Policy-Report-Only is not enforced by any browser — it is a site measuring
    /// a policy it has not turned on. Reading it as a refusal would blank the panel for a site that
    /// frames perfectly well.
    /// </summary>
    [Fact]
    public async Task A_report_only_policy_is_not_a_refusal()
    {
        var policy = new FrameEmbedPolicy(StubHttpClientFactory.Serving(
            System.Net.HttpStatusCode.OK,
            ("content-security-policy-report-only", "frame-ancestors 'none'")));

        var check = await policy.CheckAsync("https://example.com/", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Allowed, check.Status);
    }

    /// <summary>
    /// An error response with nothing to go on is "couldn't tell". The frame may still load — the
    /// CDN turned away a request that wasn't a signed-in browser, and the browser is one.
    /// </summary>
    [Fact]
    public async Task An_unreadable_page_with_no_framing_headers_is_not_called_allowed()
    {
        var policy = new FrameEmbedPolicy(StubHttpClientFactory.Serving(System.Net.HttpStatusCode.Forbidden));

        var check = await policy.CheckAsync("https://example.com/", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Unknown, check.Status);
        Assert.Equal(403, check.HttpStatus);
    }

    [Fact]
    public async Task A_site_with_no_framing_headers_at_all_is_reported_as_embeddable()
    {
        var policy = new FrameEmbedPolicy(StubHttpClientFactory.Serving(System.Net.HttpStatusCode.OK));

        var check = await policy.CheckAsync("https://example.com/", CancellationToken.None);

        Assert.Equal(FrameEmbedStatuses.Allowed, check.Status);
        Assert.Equal("headers", check.Source);
        Assert.Contains("example.com", check.Headline, StringComparison.Ordinal);
    }

    /// <summary>A factory whose client always fails — the "the probe didn't land" path.</summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("no network in tests");
        }
    }

    /// <summary>A factory serving one canned response, headers and all.</summary>
    private sealed class StubHttpClientFactory(HttpStatusCode status, (string Name, string Value)[] headers)
        : IHttpClientFactory
    {
        public static StubHttpClientFactory Serving(HttpStatusCode status, params (string Name, string Value)[] headers) =>
            new(status, headers);

        public HttpClient CreateClient(string name) => new(new StubHandler(status, headers));

        private sealed class StubHandler(HttpStatusCode status, (string Name, string Value)[] headers) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent("<html></html>") };
                foreach (var (headerName, value) in headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(headerName, value))
                        response.Content.Headers.TryAddWithoutValidation(headerName, value);
                }
                return Task.FromResult(response);
            }
        }
    }
}
