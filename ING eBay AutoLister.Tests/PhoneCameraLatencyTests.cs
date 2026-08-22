namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The phone is the camera, and until 2026-08-22 it was a camera you could not aim.
/// </summary>
/// <remarks>
/// <para>
/// Every wait in that path was a sleep. The phone sent a viewfinder frame on a one-second timer;
/// the desktop asked for a frame on its own, unsynchronised one-second timer; the phone's command
/// poll woke ten times a second to ask whether the shutter had been pressed; and the shutter woke
/// eight times a second to ask whether a photograph had arrived. None of those numbers were the
/// cost of the network. Stacked, they put a frame on screen up to two seconds after the lens saw
/// it, moving once a second when it got there — you moved the phone, waited, and found out where
/// it had been pointing.
/// </para>
/// <para>
/// Measured after the change, with a fake camera standing in for the phone and the real 9443
/// server in the middle: 20 frames a second at the desktop, 54ms between frames, and a shutter
/// that returns a saved photograph in about 90ms.
/// </para>
/// <para>
/// These are source checks rather than timing checks on purpose. A timing assertion on a build
/// agent measures the agent; what actually has to hold is that no one puts a timer back.
/// </para>
/// </remarks>
public class PhoneCameraLatencyTests
{
    private static readonly string Phone = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Js = ReadSource(Path.Combine("wwwroot", "app.js"));

    // ── The viewfinder is pushed, not asked for ──────────────────────────────────────────────

    [Fact]
    public void The_phone_does_not_sleep_a_second_between_viewfinder_frames()
    {
        // The exact line that made it a one-frame-a-second camera.
        Assert.DoesNotContain("await new Promise(r => setTimeout(r, 1000));", Phone);

        // Paced by the link instead: send, wait for the send to land, send the next.
        Assert.Contains("const MIN_FRAME_MS = 40;", Phone);
        Assert.Contains("if (spent < MIN_FRAME_MS)", Phone);
    }

    [Fact]
    public void A_frame_the_camera_has_not_redrawn_is_never_sent_twice()
    {
        // Encoding and shipping an identical frame costs a JPEG and a request to say nothing.
        Assert.Contains("const stamp = (v.currentTime * 1000) | 0;", Phone);
        Assert.Contains("stamp !== lastFrame", Phone);
    }

    [Fact]
    public void The_desktop_holds_one_connection_open_instead_of_polling()
    {
        Assert.Contains("/api/photobox/phone/stream", Program);
        Assert.Contains("multipart/x-mixed-replace; boundary=", Program);

        // A viewfinder served from a cache, or held in a proxy's buffer, is not a viewfinder.
        Assert.Contains("no-store, no-cache, must-revalidate", Program);
        Assert.Contains("X-Accel-Buffering", Program);

        // Flushed per frame: a frame sitting in a write buffer has not been delivered, and
        // removing exactly that kind of wait is the whole point of the endpoint.
        Assert.Contains("await body.FlushAsync(ct);", Program);

        Assert.Contains("img.src = '/api/photobox/phone/stream?t=' + Date.now();", Js);
    }

    [Fact]
    public void The_old_once_a_second_path_survives_as_the_fallback()
    {
        // Multipart in an <img> is the only place that transport still lives, and a browser could
        // drop it. A poor viewfinder beats a blank box, so the timer stays reachable — as a
        // fallback armed by silence, never as the normal path.
        Assert.Contains("function pbPollPhonePreview()", Js);
        Assert.Contains("setInterval(tick, 1000)", Js);
        Assert.Contains("img.onerror = () => { if (!started) pbPollPhonePreview(); };", Js);
        Assert.Contains("if (!started) pbPollPhonePreview(); }, 6000)", Js);
    }

    [Fact]
    public void Closing_the_viewfinder_lets_go_of_the_stream()
    {
        // The connection is only closed by the <img> releasing it. Left alone, the server keeps a
        // request open and keeps writing frames into a picture nobody is looking at.
        Assert.Contains("pbPhonePreview.stream.removeAttribute('src');", Js);
    }

    // ── The shutter waits on the event, not on a clock ───────────────────────────────────────

    [Fact]
    public void Nothing_in_the_phone_path_ticks_any_more()
    {
        Assert.DoesNotContain("await Task.Delay(150, ct);", Phone);          // the command poll
        Assert.DoesNotContain("await Task.Delay(120, deadline.Token);", Phone);  // the shutter
    }

    [Fact]
    public void Every_command_the_poll_is_waiting_for_wakes_it()
    {
        // A signal nobody sets is a timeout wearing a disguise, so each of these is checked at
        // the point that makes it true: the shutter, both recording commands, and the settings
        // that have to reach the lens now (zoom, torch, lens choice).
        Assert.Contains("_commandReady.Set();   // the phone is holding a poll open", Phone);
        Assert.Contains("_command = \"record-start\";\n        _commandReady.Set();",
                        Phone.Replace("\r\n", "\n"));
        Assert.Contains("_command = \"record-stop\";\n        _commandReady.Set();",
                        Phone.Replace("\r\n", "\n"));
        Assert.Contains("_settingsSeq++;\n        // A zoom nudge, a lens change or a torch has to reach the lens now.",
                        Phone.Replace("\r\n", "\n"));
        Assert.Contains("_shotArrived.Set();", Phone);
    }

    [Fact]
    public void The_waiter_is_taken_before_the_state_is_read()
    {
        // The one way to get this shape wrong: read the state, then take the waiter, and sleep
        // through anything that landed in between. Each wait says so where it does it.
        Assert.Contains("var waiter = _previewReady.Waiter;", Phone);
        Assert.Contains("var waiter = _commandReady.Waiter;", Phone);
        Assert.Contains("var landed = _shotArrived.Waiter;", Phone);
        Assert.Contains("Before the read, always", Phone);
    }

    [Fact]
    public void A_frame_that_wins_the_race_does_not_leave_a_timer_running()
    {
        // Task.WhenAny abandons the loser rather than cancelling it. At twenty frames a second an
        // uncancelled Task.Delay per frame is a timer queue that only grows.
        Assert.Equal(3, Occurrences(Phone.Replace("\r\n", "\n"), "wake.Cancel();"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
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
