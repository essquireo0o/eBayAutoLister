using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Fetches a live show's page and hands it to <see cref="WhatnotShowParser"/>. One request, one
/// press, and every way it can fail turned into a sentence with a next move in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is fetched, and why this one.</b> The public show page — the same document the seller's
/// own browser loads when they open the show. Not Whatnot's internal GraphQL API: that endpoint is
/// undocumented and unversioned, calling it means impersonating their own client against a schema
/// nobody promised to keep, and automated access to a private API is the kind of thing a platform's
/// terms reserve to itself. A page view is a page view. The whole tradeoff is written up at the top
/// of <see cref="WhatnotShowParser"/>, including what it costs: a page is a snapshot at load, so the
/// title is solid and the live bid is not.
/// </para>
/// <para>
/// <b>What it does not do.</b> No polling loop lives here — one call answers one press. No sign-in,
/// no cookie, no session: this reads what a signed-out visitor sees, which is why a page that needs
/// an account comes back as "couldn't read it" rather than as a wrong answer. And it prices nothing.
/// The title it finds goes to the same <c>/api/whatsnot/bid</c> the typed box uses.
/// </para>
/// <para>
/// The address is validated by <see cref="FrameEmbedPolicy.Validate"/> before anything is fetched —
/// the same guard the embed check uses, because this is the same app making a request at an address
/// somebody typed, and loopback/private-network addresses are not live shows.
/// </para>
/// </remarks>
public sealed class WhatnotShowReader(IHttpClientFactory httpFactory)
{
    /// <summary>
    /// The read happens beside a lot that is being bid on. Past this the answer has missed the lot
    /// it was about, and a seller staring at a spinner is worse off than one who typed the name.
    /// </summary>
    public const int ReadTimeoutSeconds = 8;

    public async Task<WhatnotShowRead> ReadAsync(string? rawUrl, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var url = FrameEmbedPolicy.Normalize(rawUrl);
        var read = new WhatnotShowRead { Url = url };

        try
        {
            if (url.Length == 0)
            {
                return Refuse(read, WhatnotReadStatuses.Invalid,
                    "No address to read.",
                    "The read needs the address of the show you're watching.",
                    "Paste it above, or press ⌂ in the panel below and open a show.");
            }

            // The same address guard the embed check runs. This app is about to make the request, so
            // "is that a public web page" has to be answered before it does.
            var (addressOk, why) = FrameEmbedPolicy.Validate(url);
            if (!addressOk)
            {
                return Refuse(read, WhatnotReadStatuses.Invalid, why,
                    "The read fetches a public https page and nothing else.",
                    "Paste the show's address from your browser.");
            }

            var (showOk, headline, detail, hint) = WhatnotShowParser.ValidateShowUrl(url);
            if (!showOk)
                return Refuse(read, WhatnotReadStatuses.NotAShow, headline, detail, hint);

            var http = httpFactory.CreateClient();
            PublicFeedHttp.ApplyBrowserHeaders(http);
            // A show page is a document, not a feed; the shared browser headers ask for XML first.
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
            http.Timeout = TimeSpan.FromSeconds(ReadTimeoutSeconds);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds));

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            read.HttpStatus = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return Refuse(read, StatusFor(response.StatusCode), HeadlineFor(response.StatusCode),
                    DetailFor(response.StatusCode), HintFor(response.StatusCode));
            }

            var body = await PublicFeedHttp.ReadBoundedAsync(response, WhatnotShowParser.MaxPageBytes, deadline.Token);
            if (body is null)
            {
                return Refuse(read, WhatnotReadStatuses.Unreadable,
                    "That page came back far larger than a page should be.",
                    $"The read stops at {WhatnotShowParser.MaxPageBytes / (1024 * 1024)}MB rather than " +
                    "holding an endless response in memory while a lot is on the block.",
                    "Type the item in and press Price it.");
            }

            // A 200 is not an answer: a challenge page arrives with a success status and parses to no
            // listing at all, which reads as "the show is between lots".
            if (DealFeedParser.DetectBlock(body, "Whatnot") is not null)
            {
                return Refuse(read, WhatnotReadStatuses.Unreadable,
                    "Whatnot answered with a block page rather than the show.",
                    "It does that to requests that aren't a signed-in browser, and it comes back with " +
                    "a success code — so this checks the page itself rather than the status line.",
                    "Watch the show in the panel below and type the lot in — that always works.");
            }

            var parsed = WhatnotShowParser.Read(url, body);
            parsed.HttpStatus = read.HttpStatus;
            parsed.ElapsedMs = sw.ElapsedMilliseconds;
            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the browser hung up; there is nothing left to report to
        }
        catch (OperationCanceledException)
        {
            return Refuse(read, WhatnotReadStatuses.Unreachable,
                $"The show page didn't answer within {ReadTimeoutSeconds} seconds.",
                "The read gives up rather than outlasting the lot it was about.",
                "Type the item in and press Price it.");
        }
        catch (HttpRequestException ex)
        {
            return Refuse(read, WhatnotReadStatuses.Unreachable,
                "Couldn't reach Whatnot.",
                $"The read didn't get an answer ({ex.Message}).",
                "Check the address, or type the item in and press Price it.");
        }
        catch (Exception ex)
        {
            // Deliberately total. A failed read has to cost the seller the shortcut and never the
            // screen: the typed box below it is still the whole feature.
            return Refuse(read, WhatnotReadStatuses.Unreachable,
                "That read didn't happen.",
                ex.Message,
                "Type the item in and press Price it.");
        }
        finally
        {
            if (read.ElapsedMs == 0) read.ElapsedMs = sw.ElapsedMilliseconds;
        }
    }

    private static string StatusFor(System.Net.HttpStatusCode code) => (int)code switch
    {
        404 or 410 => WhatnotReadStatuses.NotAShow,
        403 or 429 => WhatnotReadStatuses.Unreadable,
        _ => WhatnotReadStatuses.Unreachable,
    };

    private static string HeadlineFor(System.Net.HttpStatusCode code) => (int)code switch
    {
        404 or 410 => "There's no show at that address.",
        403 => "Whatnot turned the read away.",
        429 => "Whatnot is rate-limiting this read.",
        >= 500 => "Whatnot is having trouble.",
        _ => $"Whatnot answered with HTTP {(int)code}.",
    };

    private static string DetailFor(System.Net.HttpStatusCode code) => (int)code switch
    {
        404 or 410 => "The page is gone, or the address is a show that has ended.",
        403 => "It does that to anything that isn't a signed-in browser. Nothing is wrong with the app " +
               "and nothing is wrong with the show; this read has no account behind it by design.",
        429 => "Too many reads too quickly. Pressing it once a lot is what it is built for.",
        >= 500 => $"It answered the read with HTTP {(int)code}.",
        _ => "That isn't an answer this read knows how to use.",
    };

    private static string HintFor(System.Net.HttpStatusCode code) => (int)code switch
    {
        404 or 410 => "Open the show in the panel below and copy its address again.",
        429 => "Wait a few seconds and read again — or type the lot in, which costs nothing.",
        _ => "Type the item in and press Price it.",
    };

    private static WhatnotShowRead Refuse(
        WhatnotShowRead read, string status, string headline, string detail, string hint)
    {
        read.Status = status;
        read.Headline = headline;
        read.Detail = detail;
        read.Hint = hint;
        return read;
    }
}
