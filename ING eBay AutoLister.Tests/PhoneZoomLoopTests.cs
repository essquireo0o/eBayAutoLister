namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Photo Box picture zoomed in and then straight back out (2026-09-09, "my screen zooms in
/// and zooms out"). These pin the shape of the fix in the phone page and its listener, because
/// the loop was made of two halves that each looked reasonable on its own.
/// </summary>
/// <remarks>
/// <para>
/// The phone told the desk about a zoom only by re-sending its capabilities, and only when the
/// KIND changed (lens vs crop). Capabilities bump the settings sequence; every settings reply
/// carries the desk's zoom number, which the desk had not changed; the phone re-applied that
/// stale number on every reply. A pinch to 3x therefore announced "lens", was answered with "1x",
/// obeyed, flipped back to "crop", announced that, and went round again.
/// </para>
/// <para>
/// The fix is two gates. The phone reports where it IS to an endpoint that records the number
/// without bumping the sequence, and the phone re-applies a polled zoom only when the desk's
/// number actually moved. Either half alone leaves a path back into the loop.
/// </para>
/// </remarks>
public class PhoneZoomLoopTests
{
    private static readonly string Source = ReadSource(Path.Combine("ING eBay AutoLister", "Services", "PhoneCapture.cs"));

    // ── The listener records a report without turning it into an instruction ────────────────

    [Fact]
    public void The_phone_has_a_zoom_report_endpoint_of_its_own()
    {
        Assert.Contains("web.MapPost(\"/p/{token}/zoom\"", Source);
        Assert.Contains("public sealed record PhoneZoomReport(double Zoom, bool Optical = false)", Source);
    }

    [Fact]
    public void A_zoom_report_never_bumps_the_settings_sequence()
    {
        // This is the whole fix on the server side. A sequence bump makes the phone's long-poll
        // answer at once with the desk's zoom — the number the phone just said it had moved away from.
        var route = Between(Source, "web.MapPost(\"/p/{token}/zoom\"", "web.Map");
        Assert.DoesNotContain("_settingsSeq", route);
        Assert.DoesNotContain("_commandReady.Set()", route);
        Assert.Contains("_zoom = Math.Clamp(report.Zoom, 1.0, 8.0)", route);
        Assert.Contains("_zoomOptical = report.Optical", route);
    }

    // ── The phone page ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_polled_zoom_is_applied_only_when_the_desk_actually_changed_it()
    {
        // Every settings reply carries the whole camera. Torch, exposure, a caps refresh: each one
        // used to re-apply the desk's old zoom and undo whatever the hand had just done.
        var poll = Between(Source, "async function poll() {", "if (j.shoot || j.command === 'shoot')");
        Assert.Contains("Math.abs(j.zoom - deskZoom) > 0.001", poll);
        Assert.DoesNotContain("if (typeof j.zoom === 'number') await applyZoom(j.zoom);", poll);
    }

    [Fact]
    public void The_phone_reports_its_zoom_to_the_report_endpoint_not_through_caps()
    {
        var report = Between(Source, "function reportZoom() {", "async function applyTorch");
        Assert.Contains("'/p/' + TOKEN + '/zoom'", report);
        Assert.DoesNotContain("sendCaps", report);
        // The desk echoes this number back on its next settings bump; it must not read as new.
        Assert.Contains("deskZoom = zoom;", report);
    }

    [Fact]
    public void The_report_carries_the_number_and_the_kind()
    {
        // Sent whenever either changes. Kind-only reporting is what left the desk's slider at 1x
        // while the phone sat at 3x, which is also why the stale number was there to send back.
        var report = Between(Source, "function reportZoom() {", "async function applyTorch");
        Assert.Contains("zoom.toFixed(2) + (zoomOptical ? 'L' : 'C')", report);
        Assert.Contains("JSON.stringify({ zoom: zoom, optical: zoomOptical })", report);
    }

    [Fact]
    public void A_lens_button_that_moved_the_lens_updates_the_zoom_it_is_now_at()
    {
        // 2x on the phone left `zoom` at 1 while the lens sat at 2. The next reply's "1x" then
        // moved it straight back, and the chip on the desk read 1.0x over a doubled picture.
        var lens = Between(Source, "async function applyLens(which) {", "async function applyFacing");
        Assert.Contains("zoom = clamp(got / (lo || 1), 1, 8);", lens);
        Assert.Contains("reportZoom();", lens);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}'");
        return text[start..end];
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
