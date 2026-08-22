namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The zoom slider moved, the chip read 1.9×, and the picture did not change (owner, 2026-08-22).
/// </summary>
/// <remarks>
/// <para>
/// Two bugs, and they hid each other. The code chose between moving the lens and cropping the
/// frame by whether <c>applyConstraints</c> THREW — which was reliable when no iPhone reported a
/// zoom capability to a web page at all. iOS then started reporting one, and <c>advanced</c>
/// constraints are best-effort by specification: a lens that will not move satisfies them by
/// ignoring them and the promise resolves exactly as it does on a lens that moved. So the optical
/// branch was taken and the crop was switched OFF for a zoom that never happened.
/// </para>
/// <para>
/// Under that, the mapping was linear across the lens's reported range rather than multiplicative,
/// so on a phone reporting 1–2× a request for 1.9× moved the lens to 1.13×: honoured, and
/// invisible.
/// </para>
/// <para>
/// The fix is to stop choosing. Ask the lens for the whole thing in its own units, measure what it
/// actually did with <c>getSettings()</c>, and crop away the difference — so the total is what was
/// asked for whether the lens moves, moves partway, or does nothing.
/// </para>
/// <para>
/// Verified end to end with a fake camera driving the real 9443 server: photographs came back
/// 3840×2160 at 1×, 1920×1080 at 2× and 960×540 at 4× — exactly 2.00× and 4.00× tighter.
/// </para>
/// </remarks>
public class PhoneZoomTests
{
    private static readonly string Phone = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));
    private static readonly string Js = ReadSource(Path.Combine("wwwroot", "app.js"));

    [Fact]
    public void A_resolved_constraint_is_never_taken_as_proof_the_lens_moved()
    {
        // The measurement is the fix. Without it every assertion below is decoration.
        Assert.Contains("const got = (t.getSettings && t.getSettings().zoom);", Phone);
        Assert.Contains("MEASURED, never assumed", Phone);
    }

    [Fact]
    public void The_crop_is_set_before_the_lens_is_asked_and_survives_a_lens_that_does_nothing()
    {
        // Set first, so every path out of applyZoom — a throw, a silent no-op, a phone with no
        // zoom capability at all — still zooms the picture by the full amount.
        Assert.Contains("cropZoom = zoom;          // the answer if the lens does nothing", Phone);

        // And the old shape is gone: a bare "cropZoom = 1" on the success path of a promise.
        Assert.DoesNotContain("await t.applyConstraints({ advanced: [{ zoom: target }] }); cropZoom = 1;", Phone);
    }

    [Fact]
    public void Whatever_the_lens_will_not_do_is_done_to_the_pixels()
    {
        // The blend. A lens that goes half way leaves half the job, and this is what picks it up.
        Assert.Contains("const lensFactor = typeof got === 'number' && got > 0 ? got / lo : 1;", Phone);
        Assert.Contains("cropZoom = Math.max(1, zoom / lensFactor);", Phone);
    }

    [Fact]
    public void The_number_on_the_slider_means_what_it_says()
    {
        // Multiplicative, in the lens's own units: our 1x is the lens at its widest, so 3x is
        // three times that. The old linear interpolation across the reported range is gone.
        Assert.Contains("const target = Math.min(hi, lo * zoom);", Phone);
        Assert.DoesNotContain("lo + (hi - lo) * ((zoom - 1) / 7)", Phone);
    }

    [Fact]
    public void A_lens_button_cannot_switch_off_the_crop_on_a_promise_either()
    {
        // Same bug, second site: picking 0.5x / 1x / 2x also zeroed the crop on a resolve.
        Assert.Contains("const moved = typeof got === 'number' && Math.abs(got - target) < Math.max(0.05, (hi - lo) * 0.02);", Phone);
        Assert.Contains("if (moved) { cropZoom = 1; zoomOptical = true; reportZoom(); }", Phone);
    }

    [Fact]
    public void The_desk_is_told_which_of_the_two_it_got()
    {
        // A chip that printed the number the desk had ASKED for is how a zoom that did nothing
        // went unnoticed for as long as it did. It now prints what the phone did.
        Assert.Contains("zoomOptical: zoomOptical,", Phone);
        Assert.Contains("bool ZoomOptical = false)", Phone);
        Assert.Contains("_zoomOptical = caps.ZoomOptical;", Phone);
        Assert.Contains("pbZoomOptical ? 'lens' : 'crop'", Js);
    }

    [Fact]
    public void The_report_is_sent_when_the_kind_changes_and_not_on_every_nudge()
    {
        // Dragging a slider is a stream of values; the desk only needs to hear the one thing it
        // cannot work out for itself, and only when it changes.
        Assert.Contains("if (zoomReported === zoomOptical) return;", Phone);
    }

    [Fact]
    public void The_viewfinder_and_the_photograph_are_cropped_by_the_same_number()
    {
        // The rule that makes the viewfinder worth aiming with: both draw through window_(), so
        // what the desk sees is what the library gets.
        Assert.Contains("const w = v.videoWidth / cropZoom, h = v.videoHeight / cropZoom;", Phone);
        Assert.Contains("const q = window_();\n          cv.width = Math.round(q.sw);",
                        Phone.Replace("\r\n", "\n"));
        Assert.Contains("const q = window_();\n                const w = 640,",
                        Phone.Replace("\r\n", "\n"));
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", relative);
        Assert.True(File.Exists(path), "missing source: " + path);
        return File.ReadAllText(path);
    }
}
