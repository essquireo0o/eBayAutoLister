namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The route to a working Safari that does not pass through a certificate at all.
/// </summary>
/// <remarks>
/// <para>
/// The trusted route — authority, profile, three taps in Settings — is real and it works, and it
/// is still the only way to get a live viewfinder and a shutter button on the computer. But it
/// asks the seller to install something, iOS refuses to let any app finish that job for them, and
/// the owner has now said several times that it does not work for them.
/// </para>
/// <para>
/// Only <c>getUserMedia</c> needs the secure context. A FILE INPUT does not. So an ordinary
/// <c>&lt;input type="file" capture="environment"&gt;</c> on the plain-HTTP page opens the iPhone's
/// own camera, hands back the bytes, and asks nothing of anybody. These pin the properties that
/// make that true — every one of them is a way to accidentally put the certificate back.
/// </para>
/// </remarks>
public class PhoneCameraNoCertTests
{
    private static readonly string Source = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));
    private static readonly string Page = Between(Source, "private string CameraFreePageHtml()", "private string TrustPageHtml");

    [Fact]
    public void The_no_certificate_camera_is_served_on_the_listener_that_needs_no_certificate()
    {
        // On the plain-HTTP port, beside /trust. On the HTTPS port it would be behind the very
        // wall it exists to get around.
        Assert.Contains("web.MapGet(\"/c/{token}\"", Source, StringComparison.Ordinal);
        Assert.Contains("if (!Ok(token)) return Results.NotFound();", Source, StringComparison.Ordinal);
        Assert.Contains("ctx.Response.Headers.CacheControl = \"no-store\"", Source, StringComparison.Ordinal);
        Assert.Contains("k.ListenAnyIP(TrustPort);", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_opens_the_phones_own_camera_rather_than_asking_the_browser_for_one()
    {
        // capture="environment" is the whole mechanism: it is what makes an ordinary file input
        // open the rear camera directly instead of a file browser.
        Assert.Contains("type=\"file\" accept=\"image/*\" capture=\"environment\" hidden", Page, StringComparison.Ordinal);
        Assert.DoesNotContain("capture=\"environment\" multiple", Page, StringComparison.Ordinal);

        // And a second input WITHOUT capture, for photos already taken — the same page, because a
        // seller who has already shot the item should not be sent looking for another one.
        Assert.Contains("<input id=\"lib\" type=\"file\" accept=\"image/*\" multiple hidden>", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void It_never_asks_for_the_live_camera_which_is_what_needs_the_certificate()
    {
        // The single line that would undo this. getUserMedia on an insecure origin is not a
        // warning or a downgrade — Safari does not hand over the camera at all, and the page would
        // be dead in exactly the way the trusted route is dead before setup.
        Assert.DoesNotContain("getUserMedia", Page, StringComparison.Ordinal);
        Assert.DoesNotContain("mediaDevices", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_photograph_becomes_a_jpeg_on_the_phone_where_the_codec_already_is()
    {
        // An iPhone hands back HEIC often enough, and the AI enhance pass and the eBay upload both
        // expect a JPEG. The phone can already decode its own format; the desktop cannot.
        Assert.Contains("canvas.toBlob(r, 'image/jpeg', 0.92)", Page, StringComparison.Ordinal);
        Assert.Contains("createImageBitmap(file)", Page, StringComparison.Ordinal);

        // With a fallback for the Safari that cannot bitmap a HEIC File but can always display it.
        Assert.Contains("URL.createObjectURL(file)", Page, StringComparison.Ordinal);
        Assert.Contains("revokeObjectURL", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_photograph_lands_where_every_other_photograph_lands()
    {
        // The same token-gated endpoint the trusted camera posts to, so a shot taken this way is
        // saved by the same code, added to the same list, and shows up on the desktop panel with
        // no second path to keep working. See the /p/{token}/photo handler.
        Assert.Contains("fetch('/p/' + TOKEN + '/photo', { method: 'POST', body: shot.blob })", Page, StringComparison.Ordinal);
        Assert.Contains("web.MapPost(\"/p/{token}/photo\"", Source, StringComparison.Ordinal);

        // Still the pairing secret, not an open door: the page is rendered by the server that knows
        // the token, and the endpoint checks it in constant time exactly as before.
        Assert.Contains("System.Text.Json.JsonSerializer.Serialize(_token)", Page, StringComparison.Ordinal);
        Assert.Contains("if (!Ok(token)) return Results.NotFound();", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setup_page_offers_it_so_a_phone_that_will_not_be_trusted_is_not_stranded()
    {
        var trust = Between(Source, "private string TrustPageHtml", "// ── Why there is an authority here");

        // Above the fold and before the three Settings steps, because the seller reading it is the
        // one for whom those steps have already failed.
        Assert.Contains("href=\"/c/{{_token}}\"", trust, StringComparison.Ordinal);
        Assert.Contains("take photos with this phone", trust, StringComparison.OrdinalIgnoreCase);

        // And the way back, for the seller who does want the viewfinder after all.
        Assert.Contains("href=\"/trust\"", Page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_primary_qr_opens_the_working_no_certificate_camera_immediately()
    {
        Assert.Contains("private string LaunchUrl => $\"http://{LocalAddress()}:{TrustPort}/c/{_token}\";", Source,
                        StringComparison.Ordinal);
        Assert.Contains("var url = LaunchUrl;", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("private string LaunchUrl => $\"http://{LocalAddress()}:{TrustPort}/start\";", Source,
                              StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_what_it_costs_rather_than_pretending_the_two_routes_are_the_same()
    {
        // No live preview and no desktop shutter. A seller who is told this up front is choosing;
        // one who finds out by waiting for a viewfinder that never comes is stuck again.
        Assert.Contains("You tap the shutter instead of the computer.",
                        Between(Source, "private string TrustPageHtml", "// ── Why there is an authority here"),
                        StringComparison.Ordinal);
    }

    /// <summary>The slice of the source between two markers, so a page's assertions cannot pass on another page's text.</summary>
    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"could not find \"{start}\" in PhoneCapture.cs");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"could not find \"{end}\" after \"{start}\" in PhoneCapture.cs");
        return source[from..to];
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", relative));
    }
}
