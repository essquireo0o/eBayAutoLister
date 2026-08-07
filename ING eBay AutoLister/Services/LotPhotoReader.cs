using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Fetches one lot photograph so it can be looked at. One request, one press, and every way it can
/// fail turned into a sentence with a next move in it.
/// </summary>
/// <remarks>
/// <para>
/// The picture is the lot's own, off the show's page — the address <see cref="WhatnotShowReader"/>
/// already brought back. It is not a frame grabbed off the video: a live stream is somebody else's
/// bandwidth and a 480p frame of a moving object is the worst evidence in the room, while the
/// listing's photograph is the one the host chose and the one the buyer is being sold on.
/// </para>
/// <para>
/// The address goes through <see cref="FrameEmbedPolicy.Validate"/>, the same guard the embed check
/// and the show read use, and then through a narrower one: https only, and one of the four image
/// types the vision call can actually be handed. This app is about to fetch bytes from an address
/// that arrived in a request body, so "is that a public picture" has to be answered before it does.
/// The body is read through <see cref="PublicFeedHttp.ReadBoundedBytesAsync"/> rather than a byte
/// loop of its own — two ceilings on how much of somebody else's response this process holds is one
/// ceiling too many.
/// </para>
/// <para>
/// It decides nothing. It hands back base64 and a media type, or a
/// <see cref="LotPhotoLook"/> that is already a refusal with a sentence on it.
/// </para>
/// </remarks>
public sealed class LotPhotoReader(IHttpClientFactory httpFactory)
{
    /// <summary>
    /// The fetch happens beside a lot that is being bid on, and it is only the first half of the
    /// wait — the look at the picture follows it. Past this the answer has missed the lot it was
    /// about.
    /// </summary>
    public const int FetchTimeoutSeconds = 6;

    /// <summary>
    /// How much photograph this will hold. A listing thumbnail is tens of kilobytes; anything past
    /// this is not a lot photo, and sending it would spend the seller's seconds finding that out.
    /// </summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The only four the vision call can be handed. Anything else — an SVG, a video poster, an HTML
    /// error page served with a 200 — is refused here rather than sent and rejected remotely, which
    /// would cost a round trip and arrive as a mysterious failure.
    /// </summary>
    public static readonly string[] AllowedTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    public async Task<(string? Base64, string? MediaType, LotPhotoLook? Refusal)> FetchAsync(
        string? rawUrl, CancellationToken ct)
    {
        var url = FrameEmbedPolicy.Normalize(rawUrl);

        if (url.Length == 0) return (null, null, LotPhotoJudge.NoPhoto());

        var (addressOk, why) = FrameEmbedPolicy.Validate(url);
        if (!addressOk)
        {
            return (null, null, LotPhotoJudge.Refuse(LotPhotoStatuses.Invalid, why,
                "The look fetches one public picture and nothing else.",
                "Read the show again — the photo's address comes back with the lot."));
        }

        // https only. The picture is fetched by this app and then sent on; an http address is a
        // downgrade nobody asked for, and the show read already keeps https addresses only.
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, LotPhotoJudge.Refuse(LotPhotoStatuses.Invalid,
                "That photo is served over http rather than https.",
                "The app won't fetch a picture over an unencrypted connection and then send it on.",
                "Type the item in and press ⚡ Price it — the ceiling never needed the photo."));
        }

        try
        {
            var http = httpFactory.CreateClient();
            PublicFeedHttp.ApplyBrowserHeaders(http);
            // A picture is not a feed; the shared browser headers ask for XML first.
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,*/*;q=0.8");
            http.Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return (null, null, WithStatus(LotPhotoJudge.Refuse(
                    status is 404 or 410 ? LotPhotoStatuses.Unreadable : LotPhotoStatuses.Unreachable,
                    status is 404 or 410
                        ? "That photo is gone."
                        : $"The photo host answered with HTTP {status}.",
                    status is 403 or 429
                        ? "Image hosts turn away requests that aren't a browser loading a page, and this one is one."
                        : "That isn't an answer a picture can be read out of.",
                    "Read the show again for a fresh address, or type the item in and press ⚡ Price it."), status));
            }

            var mediaType = (response.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
            if (!AllowedTypes.Contains(mediaType))
            {
                return (null, null, WithStatus(LotPhotoJudge.Refuse(LotPhotoStatuses.Unreadable,
                    mediaType.Length > 0
                        ? $"That address answered with {mediaType}, not a photo."
                        : "That address didn't say what it was answering with.",
                    "Only JPEG, PNG, GIF and WebP are looked at — a page served where a picture was " +
                    "expected is usually a sign-in wall or an error, and sending it would waste the " +
                    "seconds this screen exists to save.",
                    "Type the item in and press ⚡ Price it."), (int)response.StatusCode));
            }

            var bytes = await PublicFeedHttp.ReadBoundedBytesAsync(response, MaxImageBytes, deadline.Token);
            if (bytes is null || bytes.Length == 0)
            {
                return (null, null, WithStatus(LotPhotoJudge.Refuse(LotPhotoStatuses.Unreadable,
                    bytes is null
                        ? "That photo came back far larger than a lot photo should be."
                        : "That photo came back empty.",
                    bytes is null
                        ? $"The fetch stops at {MaxImageBytes / (1024 * 1024)}MB rather than holding an " +
                          "endless response in memory while a lot is on the block."
                        : "The host answered, with nothing in it.",
                    "Type the item in and press ⚡ Price it."), (int)response.StatusCode));
            }

            return (Convert.ToBase64String(bytes), mediaType, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the browser hung up; there is nothing left to report to
        }
        catch (OperationCanceledException)
        {
            return (null, null, LotPhotoJudge.Refuse(LotPhotoStatuses.Unreachable,
                $"The photo didn't arrive within {FetchTimeoutSeconds} seconds.",
                "The fetch gives up rather than outlasting the lot it was about.",
                "Type the item in and press ⚡ Price it."));
        }
        catch (Exception ex)
        {
            // Deliberately total. A failed look costs the seller a check and never the screen: the
            // ceiling below it is priced by a path that has never needed a picture.
            return (null, null, LotPhotoJudge.Refuse(LotPhotoStatuses.Unreachable,
                "That photo couldn't be fetched.",
                ex.Message,
                "Type the item in and press ⚡ Price it."));
        }
    }

    private static LotPhotoLook WithStatus(LotPhotoLook look, int status)
    {
        look.HttpStatus = status;
        return look;
    }
}
