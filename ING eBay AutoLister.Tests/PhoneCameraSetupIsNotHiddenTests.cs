using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The iPhone trust setup is on the screen, not folded behind a summary anybody has to find.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24, after the owner escalated "the app does not work on safari because it does not have a
/// certificate" for the third time. Everything underneath that sentence was measured working on
/// their own machine that morning: <c>openssl verify</c> passes the live leaf against the CA the
/// setup page hands out, the <c>.mobileconfig</c> embeds exactly that CA (same SHA-256), the leaf's
/// SAN carries the LAN address, and the setup port is inside the firewall rule that the phone is
/// already reaching on its neighbour. There was no broken certificate anywhere in it.
/// </para>
/// <para>
/// What was broken is that the instructions were folded. Step 2 of the pairing panel reads "do the
/// one-time setup first — the block below", and the block below was a collapsed <c>&lt;details&gt;</c>
/// rendering as one grey line. The one action standing between an iPhone and a working camera was
/// the one action nobody could see, and three sessions went hunting for a defect in the certificate
/// instead.
/// </para>
/// <para>
/// This is also settled policy rather than one session's taste: a <c>&lt;details&gt;</c> fold on AI
/// Listing was rejected and fully reverted on 2026-08-21 — "easy" means bigger and clearer, never
/// hidden. So it is asserted rather than left to be re-folded by the next person who decides a
/// once-per-phone step is furniture.
/// </para>
/// </remarks>
public class PhoneCameraSetupIsNotHiddenTests
{
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void The_iPhone_setup_is_not_folded_behind_a_details_element()
    {
        var block = TrustBlock();

        Assert.DoesNotContain("<details", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<summary", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_still_carries_the_code_and_the_link_the_phone_needs()
    {
        // Unfolding must not have cost the two things the phone actually consumes — app.js paints
        // both by id, and it does so whether or not anything is expanded.
        var block = TrustBlock();

        Assert.Contains("pb-trust-qr", block, StringComparison.Ordinal);
        Assert.Contains("pb-trust-url", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_iOS_will_not_let_software_perform_is_spelled_out_in_full()
    {
        // No page can flip this switch, so the words are the entire feature. If the trail from
        // Settings to the toggle is ever shortened to "trust the certificate", the seller is back
        // to hunting through iOS for something they were never told the name of.
        var block = TrustBlock();

        Assert.Contains("Certificate Trust", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ING Photo Box camera authority", block, StringComparison.Ordinal);
        foreach (var crumb in new[] { "Settings", "General", "About" })
            Assert.Contains(crumb, block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_stylesheet()
    {
        // The block is restyled as a card; a cached stylesheet would render the new markup with the
        // old folded rules and hide it all over again.
        AssetStamp.AtLeast(Html, "style.css?v=", 136);
    }

    [Fact]
    public void The_primary_instructions_start_with_the_certificate_free_workflow()
    {
        var panelStart = Html.IndexOf("<h3>Scan this with your iPhone</h3>", StringComparison.Ordinal);
        var trustStart = Html.IndexOf("<section class=\"pb-phone-trust\"", panelStart, StringComparison.Ordinal);
        Assert.True(panelStart >= 0 && trustStart > panelStart, "the phone instructions or optional trust block moved.");
        var primary = Html[panelStart..trustStart];

        Assert.Contains("Take a photo", primary, StringComparison.Ordinal);
        Assert.Contains("Use Photo", primary, StringComparison.Ordinal);
        Assert.Contains("no certificate, profile or browser permission is needed", primary,
                        StringComparison.OrdinalIgnoreCase);
        Assert.Contains("optional", primary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do the one-time setup first", primary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The pairing panel's trust section, isolated so the assertions cannot drift onto other markup.</summary>
    private static string TrustBlock()
    {
        var start = Html.IndexOf("pb-phone-trust", StringComparison.Ordinal);
        Assert.True(start >= 0, "the iPhone trust setup block is gone from index.html entirely.");

        // Exactly this section and nothing after it. An earlier version of this helper ran to the
        // next rail card and swept in the "Pro photo playbook" <details> that follows, which made
        // the fold assertion fail on somebody else's perfectly reasonable fold.
        var open = Html.LastIndexOf('<', start);
        var close = Html.IndexOf("</section>", start, StringComparison.OrdinalIgnoreCase);
        Assert.True(close > open, "the trust block is no longer a self-contained <section>.");
        return Html[open..close];
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository root above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
