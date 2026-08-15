using System.Security.Cryptography;
using System.Text;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Serves Facebook Marketplace listing photos from THIS origin instead of hotlinking Facebook's CDN.
///
/// The CDN URLs Marketplace hands out are signed, short-lived, and reject a cross-origin referrer —
/// so a browser on app.inglisting.com renders them blank even though the server can fetch them fine
/// (verified: a plain server-side GET returns the JPEG). This rewrites each photo URL to
/// <c>/api/fb-photo?u=…&amp;s=…</c>; the endpoint fetches the image once, caches it to disk, and
/// serves it same-origin. The HMAC over the URL stops it being an open image proxy, and the host is
/// pinned to Facebook besides — together they close the SSRF door the "fetch any URL" shape opens.
///
/// HOSTED ONLY. On the desktop build the photos load directly in the app's own browser with the
/// right referrer, so there is nothing to fix: <see cref="Enabled"/> is false and <see cref="Rewrite"/>
/// returns the URL untouched.
/// </summary>
public static class FbPhotoProxy
{
#if HOSTED
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif

    // HMAC key material. Reuses the hosted token-encryption secret — same trust boundary, and it is
    // guaranteed present on the build where this is enabled (the app refuses to start without it).
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes(
        "fb-photo-proxy|" + (Environment.GetEnvironmentVariable("CREDENTIALS_ENCRYPTION_KEY") ?? "dev")));

    public static string CacheDir { get; } = Path.Combine(AppPaths.DataHome, "fb-photo-cache");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>A URL this proxy is willing to fetch: https, on a Facebook host. Everything else is
    /// refused, so a tampered <c>u=</c> cannot point the server at an internal address.</summary>
    public static bool IsFacebookCdn(string? url) =>
        !string.IsNullOrEmpty(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase));

    public static string Sign(string url)
    {
        using var h = new HMACSHA256(Key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
    }

    public static bool Verify(string url, string? sig) =>
        !string.IsNullOrEmpty(sig)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(url)), Encoding.ASCII.GetBytes(sig));

    /// <summary>The same-origin URL to hand the browser, or the original when this is off or the URL
    /// is not a Facebook photo (e.g. a Craigslist thumbnail, which needs no help).</summary>
    public static string Rewrite(string? url)
    {
        if (!Enabled || !IsFacebookCdn(url)) return url ?? "";
        return "/api/fb-photo?u=" + Uri.EscapeDataString(url!) + "&s=" + Sign(url!);
    }

    public static void RewriteItems(IEnumerable<LocalSupplyListing>? items)
    {
        if (!Enabled || items is null) return;
        foreach (var it in items)
            if (IsFacebookCdn(it.ImageUrl))
                it.ImageUrl = Rewrite(it.ImageUrl);
    }

    /// <summary>
    /// Returns the image bytes for a verified Facebook photo URL, from the on-disk cache when present
    /// and by fetching (then caching) when not. Null on any refusal or failure — the caller answers
    /// 404, which a browser treats as a broken image, not a broken page.
    /// </summary>
    public static async Task<byte[]?> GetBytesAsync(string url, string? sig, CancellationToken ct)
    {
        if (!Enabled || !IsFacebookCdn(url) || !Verify(url, sig)) return null;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
            var path = Path.Combine(CacheDir, key + ".img");

            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path, ct);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // No Referer on purpose: Facebook's CDN serves the signed URL to an anonymous request but
            // rejects one that carries a foreign site's referrer, which is the whole reason the
            // browser could not load it directly.
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return null;

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            try { File.Move(tmp, path, overwrite: true); } catch { try { File.Delete(tmp); } catch { } }
            return bytes;
        }
        catch { return null; }
    }
}
