namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The seller's own code for one item, on its way to eBay.
/// </summary>
/// <remarks>
/// <para>
/// A SKU is the only key that survives the whole life of a flip. eBay mints the listing ID at
/// publish and mints a new one on every relist; the SKU is the seller's, it is chosen before the
/// listing exists, and <see cref="CostBasisStore"/> is deliberately keyed on both so a relisted item
/// keeps the cost the seller already recorded. That only works if the SKU the app wrote on the draft
/// is the SKU that reaches eBay — which, until now, it was not: every publish minted a fresh random
/// one and threw the seller's away.
/// </para>
/// <para>
/// <b>It never invents one.</b> <see cref="Sanitize"/> returns an empty string when the draft has no
/// SKU, and the publish path sends no <c>SKU</c> element at all in that case. A random code written
/// onto somebody's live listing is a code in their Seller Hub, their reports and their exports that
/// they did not choose and cannot look up.
/// </para>
/// <para>
/// <b>What eBay accepts.</b> Up to 50 characters, and no whitespace — the Trading API takes the SKU
/// as an element value and the Inventory API takes it in the URL path, so a space is a value that
/// works in one call and breaks the other. Spaces become hyphens rather than disappearing, because
/// "S19J PRO" collapsing to "S19JPRO" is a different code from the one on the seller's shelf label.
/// </para>
/// <para>Pure and static: no clock but <see cref="Mint"/>'s randomness, no I/O, no eBay.</para>
/// </remarks>
public static class SellerSku
{
    /// <summary>eBay's own limit on a SKU, in both the Trading and Inventory APIs.</summary>
    public const int MaxLength = 50;

    /// <summary>
    /// The seller's SKU, cut down to what eBay will accept — or an empty string when there is
    /// nothing usable in it, which is the signal to send no SKU at all rather than to make one up.
    /// </summary>
    public static string Sanitize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "";

        var cleaned = new System.Text.StringBuilder(requested.Length);
        foreach (var ch in requested.Trim())
        {
            if (char.IsWhiteSpace(ch)) cleaned.Append('-');
            // Letters and digits pass, plus the three separators every warehouse code already uses.
            // Anything else — quotes, slashes, the em dash a title picked up — is dropped rather
            // than transliterated: a SKU is a key, and a key nobody can retype is not one.
            else if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') cleaned.Append(ch);
        }

        var sku = cleaned.ToString().Trim('-');
        return sku.Length <= MaxLength ? sku : sku[..MaxLength].TrimEnd('-');
    }

    /// <summary>
    /// A fresh SKU for a listing that carries none. Twenty characters, which is short enough to read
    /// off a screen and long enough that two of them never collide.
    /// </summary>
    public static string Mint() => $"SKU-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// The SKU to publish under: the seller's own when the draft carries one, and a minted one
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// Used by the Inventory API path, where a SKU is not optional — it is the key the whole call is
    /// addressed to. The Trading API path does not use this, because there a blank SKU is a listing
    /// without one, which is what the seller asked for.
    /// </remarks>
    public static string For(string? requested)
    {
        var sku = Sanitize(requested);
        return sku.Length > 0 ? sku : Mint();
    }
}
