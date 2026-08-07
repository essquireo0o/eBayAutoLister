namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One GET against a public feed, and every way it can go wrong turned into a sentence.
///
/// Extracted from <see cref="DealFeedService"/> when <see cref="FreebieSourceService"/> became the
/// second source reading the same kind of feed off the same sites. Two copies of this would
/// eventually disagree about what a 403 means, and "wait a minute and scan again" is a promise the
/// whole app should keep the same way.
/// </summary>
/// <remarks>
/// Nothing here throws for a site's benefit: a feed that refuses, stalls or answers with a
/// challenge page has to arrive at the UI as a note beside whatever the other feeds found, never as
/// a failed response. The one exception is the caller's own cancellation, which propagates — the
/// browser is gone, so there is nothing left to report to.
/// </remarks>
public static class PublicFeedHttp
{
    /// <summary>
    /// These are feeds published for readers, so the request is shaped like a reader: a browser
    /// asking for one document it was pointed at. A bare client with no user agent is the shape a
    /// CDN refuses first, and a refusal parses to zero deals — which would reach the seller as
    /// "nothing is on sale" rather than as a failure.
    ///
    /// No Accept-Encoding: this client isn't configured to decompress, and advertising gzip would
    /// hand the parser a binary blob that reads as an empty feed. A silent wrong answer is worse
    /// than a loud failure.
    /// </summary>
    public static void ApplyBrowserHeaders(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "application/rss+xml,application/xml;q=0.9,text/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
    }

    /// <summary>
    /// Fetches one feed. <paramref name="budget"/> is the scan's own deadline and
    /// <paramref name="caller"/> is the request's; only the latter propagates.
    /// </summary>
    /// <param name="site">The aggregator's name, used in the block-page sentence.</param>
    /// <param name="movedHint">Where to edit when the feed 404s — the catalogue that owns the URL.</param>
    /// <param name="maxBytes">Ceiling on the response body, so an endless stream can't exhaust memory.</param>
    public static async Task<(string? Body, string? Error, bool Retryable)> GetAsync(
        HttpClient http, string url, string site, string movedHint, int maxBytes,
        CancellationToken budget, CancellationToken caller)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget);
            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode switch
                {
                    // Backing off is the correct and only response. Retrying in a loop is how an
                    // address gets blocked from a feed that was working fine a minute ago.
                    403 or 429 => (null, "rate-limited right now — wait a minute and scan again", true),
                    404 or 410 => (null, $"that feed has moved — it needs updating in {movedHint}", false),
                    >= 500 => (null, $"the site is having trouble (HTTP {(int)response.StatusCode})", true),
                    _ => (null, $"HTTP {(int)response.StatusCode}", false),
                };
            }

            if (response.Content.Headers.ContentLength > maxBytes)
                return (null, "that feed came back far larger than a feed should be", false);

            var body = await ReadBoundedAsync(response, maxBytes, budget);
            if (body is null) return (null, "that feed came back far larger than a feed should be", false);

            // A 200 is not the same as an answer: a challenge page is served with a success status
            // and parses to zero deals, which looks exactly like "nothing matched".
            var blocked = DealFeedParser.DetectBlock(body, site);
            return blocked is not null ? (null, blocked, true) : (body, null, false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;   // the browser hung up; there is nothing left to report to
        }
        catch (OperationCanceledException)
        {
            return (null, "didn't respond in time", true);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"couldn't be reached ({ex.Message})", true);
        }
        catch (Exception ex)
        {
            // Deliberately total. Whatever this was — DNS, a proxy, a TLS failure — it is one feed
            // failing, and the scan's other feeds still have deals to show.
            return (null, ex.Message, true);
        }
    }

    /// <summary>
    /// Reads the body without trusting the site to have declared its length. Returns null past the
    /// ceiling rather than growing a string until the process dies.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="WhatnotShowReader"/> fetches a page rather than a feed and needs its
    /// own sentences for a refusal, but must not get its own byte loop: two ceilings on how much of
    /// somebody else's document this app will hold in memory is one ceiling too many.
    /// </remarks>
    public static async Task<string?> ReadBoundedAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var bytes = await ReadBoundedBytesAsync(response, maxBytes, ct);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The same bounded read, before anything decides the bytes were text. Returns null past the
    /// ceiling.
    /// </summary>
    /// <remarks>
    /// <see cref="LotPhotoReader"/> fetches a lot's photograph, which is emphatically not UTF-8, and
    /// the one thing it must share with every other fetch in this app is the ceiling: the loop that
    /// decides how much of somebody else's response this process will hold lives here and nowhere
    /// else. <see cref="ReadBoundedAsync"/> is this, decoded.
    /// </remarks>
    public static async Task<byte[]?> ReadBoundedBytesAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();

        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > maxBytes) return null;
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
