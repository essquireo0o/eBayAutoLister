namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The cache-busting stamps on <c>index.html</c>'s two big assets, asserted as a floor rather than
/// as an exact number.
/// </summary>
/// <remarks>
/// Eleven test classes each ended their own change by pinning the stamp that change had just
/// written — <c>Assert.Contains("app.js?v=145", …)</c>. Each was right on the day, and together
/// they froze the stamp: the twelfth change to touch app.js could not raise it without eleven
/// unrelated tests going red, so the pressure was to leave the stamp alone and ship a script every
/// returning browser would serve from cache. That is the exact failure the stamp exists to prevent.
/// <para>
/// What each of those tests actually meant is "app.js changed in my change, so the stamp must have
/// moved past where it was" — a floor, which is what this asserts, and which two of the sibling
/// classes were already written as.
/// </para>
/// </remarks>
internal static class AssetStamp
{
    /// <summary>Fails unless <paramref name="html"/> stamps the asset at <paramref name="minimum"/> or later.</summary>
    public static void AtLeast(string html, string prefix, int minimum)
    {
        var at = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{prefix}\" is no longer in index.html — the asset is not being versioned at all.");

        var digits = new string(html[(at + prefix.Length)..].TakeWhile(char.IsDigit).ToArray());
        Assert.True(digits.Length > 0, $"\"{prefix}\" is not followed by a number in index.html.");

        var stamp = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(stamp >= minimum,
            $"index.html stamps {prefix}{stamp}, which is older than {minimum}. The stamp only ever goes up: " +
            "lowering it serves a stale file to every browser that has the newer one cached.");
    }
}
