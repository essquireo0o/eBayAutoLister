namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The one control in this app that spends AI on a timer, unattended, until somebody stops it.
/// </summary>
/// <remarks>
/// <para>
/// Whatnot refuses to be embedded and a browser cannot read a cross-origin video, so the live read
/// runs on a tab the seller shares with their own browser: a frame every twenty seconds, to
/// <c>/api/whatsnot/watch</c>, through the same identification the photo check uses, and the name
/// that comes back is priced by the same <c>/api/whatsnot/bid</c> a typed one is. Five links, four
/// of them outside C#, and none of them visible in a compiler error when one goes.
/// </para>
/// <para>
/// <b>The half these tests exist for is the money.</b> On the hosted build the owner's Anthropic
/// key pays for every account and each gets a handful of generations a day — five, on the live box.
/// A loop that spends one every twenty seconds empties that in a hundred seconds, takes with it the
/// generations the seller needed to WRITE a listing, and then asks for a sixth every twenty seconds
/// for the rest of the half hour and is refused every time. So the loop asks what is left before it
/// opens the share picker, spends no more than that, stops on the refusal instead of retrying it,
/// and does not spend a read on a picture it has already read. Each of those is one line to delete
/// during a tidy-up and none of them changes anything a desktop tester would ever see.
/// </para>
/// </remarks>
public class WhatsNotLiveVideoAssetTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Quota = ReadSource("Services/AiQuota.cs");

    // ── The endpoint ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_endpoint_is_mapped_and_the_browser_asks_for_it()
    {
        Assert.Contains("app.MapPost(\"/api/whatsnot/watch\"", Program, StringComparison.Ordinal);
        Assert.Contains("safePost('/api/whatsnot/watch'", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// One identification, returning the one <c>SnapIdentity</c> shape the photo check and Snap &amp;
    /// Source get. A second prompt for "what is this" would be a second opinion about identity on the
    /// one screen with no time to notice there are two of them. This frame gets its own prompt only
    /// because it is a screenshot of a selling app rather than a photograph of an object — there is
    /// an overlay to read and, when the tab was shared with sound, a host to have heard.
    /// </summary>
    [Fact]
    public void A_frame_goes_through_the_same_identification_every_other_photo_does()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/watch\"", "\n});");

        Assert.Contains("claude.IdentifyLiveLotAsync(base64, mediaType, heard, ct)", endpoint, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) { throw; }", endpoint, StringComparison.Ordinal);
        Assert.Contains("FailureTranslator.Translate(ex, FailureDomain.Ai)", endpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Claude has no ears: the Messages API takes text, images and PDFs and there is no audio content
    /// block. So "analyse the voice" has to mean transcribing the sound somewhere else and handing the
    /// model words — and the day someone tries to POST a clip straight at the vision call, this fails.
    /// </summary>
    [Fact]
    public void The_voice_reaches_the_model_as_words_never_as_audio()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/watch\"", "\n});");

        // The transcript is a string on the request, capped before it is put in front of the prompt.
        Assert.Contains("req.Heard", endpoint, StringComparison.Ordinal);
        Assert.Contains("heard = heard[^1200..]", endpoint, StringComparison.Ordinal);

        // Sound is turned into words at its own endpoint, by a service that is not the model.
        Assert.Contains("app.MapPost(\"/api/whatsnot/listen\"", Program, StringComparison.Ordinal);
        Assert.Contains("transcriber.TranscribeAsync", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// A show is hours long and every piece of audio costs money to transcribe, so listening stops
    /// when watching does — and each piece posted is a complete recording, because a MediaRecorder
    /// given a timeslice puts the container header in the first chunk only and every chunk after it
    /// is undecodable on its own.
    /// </summary>
    [Fact]
    public void Listening_starts_and_stops_with_the_watch()
    {
        Assert.Contains("audio: true", Js, StringComparison.Ordinal);
        Assert.Contains("wnStartListening(stream)", Js, StringComparison.Ordinal);

        var stop = Between(Js, "function wnStopVideoWatch(why)", "\n  }");
        Assert.Contains("wnStopListening()", stop, StringComparison.Ordinal);

        // Each cycle builds its own recorder and stops it, rather than slicing one long one.
        var cycle = Between(Js, "function wnListenCycle()", "\n  }");
        Assert.Contains("new MediaRecorder", cycle, StringComparison.Ordinal);
        Assert.DoesNotContain("rec.start(WN_LISTEN_MS)", cycle, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the screen is that one function decides what a thing is worth. A watch
    /// endpoint that priced what it had just looked at would be a second answer about money,
    /// arrived at from a photograph rather than from sales that happened.
    /// </summary>
    [Fact]
    public void Watching_the_video_prices_nothing()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/watch\"", "\n});");

        foreach (var pricing in new[] { "AnalyzeProductAsync", "advisor.Build", "board.Hold", "MaxBid", "ResalePricing" })
            Assert.DoesNotContain(pricing, endpoint, StringComparison.Ordinal);

        // Instead the browser puts the name in the box and presses the ordinary price path.
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");
        Assert.Contains("setVal('wn-item', seen)", tick, StringComparison.Ordinal);
        Assert.Contains("wnPriceItem()", tick, StringComparison.Ordinal);
    }

    /// <summary>
    /// A browser's <c>canvas.toDataURL</c> hands back <c>data:image/jpeg;base64,…</c> and the model
    /// wants the payload alone. Stripping it in the browser instead would mean two places agreeing
    /// about a prefix, and the server is the one that cannot be cached stale.
    /// </summary>
    [Fact]
    public void A_full_data_url_is_accepted_rather_than_sent_to_the_model_as_a_prefix()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/watch\"", "\n});");

        Assert.Contains("StartsWith(\"data:\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("raw[(comma + 1)..]", endpoint, StringComparison.Ordinal);
        // And an empty one is a sentence, not a 500.
        Assert.Contains("BadInputJson(\"No frame arrived\"", endpoint, StringComparison.Ordinal);
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Metered by virtue of having been written: the gate sits inside the one method every AI call
    /// funnels through, so this endpoint needs no quota code of its own and must not grow any.
    /// </summary>
    [Fact]
    public void The_read_is_metered_where_every_other_ai_call_is()
    {
        Assert.Contains("public void Reserve(string operation)", Quota, StringComparison.Ordinal);

        var claude = ReadSource("Services/ClaudeService.cs");
        Assert.Contains("Reserve(", claude, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked BEFORE the share picker. A seller with nothing left today should be told so, not walked
    /// through choosing a tab for a loop that cannot read a frame from it.
    /// </summary>
    [Fact]
    public void The_allowance_is_read_before_the_share_picker_opens()
    {
        var toggle = Between(Js, "async function wnToggleVideoWatch()", "\n  }");

        var askedAt  = toggle.IndexOf("wnVideoAllowance()", StringComparison.Ordinal);
        var pickerAt = toggle.IndexOf("getDisplayMedia({", StringComparison.Ordinal);

        Assert.True(askedAt >= 0, "The video loop no longer asks how much AI allowance is left before it starts.");
        Assert.True(pickerAt >= 0, "The video loop no longer opens the tab-share picker.");
        Assert.True(askedAt < pickerAt,
            "The allowance is now read AFTER the share picker opens, so a seller with nothing left today " +
            "is asked to choose a tab for a loop that will be refused on its first frame.");

        // And the meter it reads is the one the server exposes.
        Assert.Contains("fetch('/api/ai-quota')", Js, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/api/ai-quota\"", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap is the allowance, not the loop's own leave-it-running limit. Ninety reads against a
    /// five-a-day allowance is the whole day's AI spent in a hundred seconds.
    /// </summary>
    [Fact]
    public void The_loop_spends_no_more_than_the_allowance_it_was_given()
    {
        var toggle = Between(Js, "async function wnToggleVideoWatch()", "\n  }");
        Assert.Contains("Math.min(left, WN_WATCH_MAX_READS)", toggle, StringComparison.Ordinal);

        // Nothing left is a refusal to start at all, with the reason and a free alternative.
        Assert.Contains("No AI reads left today", toggle, StringComparison.Ordinal);

        // And the tick counts against that cap rather than the constant.
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");
        Assert.Contains("++wnVideoReads > wnVideoCap", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("++wnVideoReads > WN_WATCH_MAX_READS", tick, StringComparison.Ordinal);
    }

    /// <summary>
    /// Being out of allowance is not a read that failed. A loop that treats it as one keeps its
    /// timer and asks again every twenty seconds until the seller closes the tab, and is refused
    /// every single time.
    /// </summary>
    [Fact]
    public void Running_out_stops_the_loop_instead_of_retrying_forever()
    {
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");

        Assert.Contains("wnIsQuotaRefusal(body)", tick, StringComparison.Ordinal);

        // The stop has to happen inside the failure branch, before the ordinary "that didn't
        // happen" line — which leaves the timer running on purpose, because those are retryable.
        var refusalAt = tick.IndexOf("wnIsQuotaRefusal(body)", StringComparison.Ordinal);
        var stopAt    = tick.IndexOf("wnStopVideoWatch", refusalAt, StringComparison.Ordinal);
        var genericAt = tick.IndexOf("That video read didn't happen.", refusalAt, StringComparison.Ordinal);
        Assert.True(stopAt >= 0 && stopAt < genericAt,
            "A quota refusal no longer stops the loop — it falls through to the retryable-failure line.");

        // The sentence is the server's, and the kind it keys on is the one the server sends.
        Assert.Contains("body.failure?.whatToDo", tick, StringComparison.Ordinal);
        Assert.Contains("'AiQuotaExhausted'", Js, StringComparison.Ordinal);
        Assert.Contains("FailureKind.AiQuotaExhausted", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read is worth spending on a new lot and worthless on the one the card is already answering.
    /// The old loop spent one every twenty seconds regardless, which on the hosted allowance meant
    /// the whole day went on a single item while the host was still describing it.
    /// </summary>
    [Fact]
    public void An_unchanged_picture_does_not_cost_a_read()
    {
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");

        Assert.Contains("wnFrameSignature()", tick, StringComparison.Ordinal);
        Assert.Contains("wnFrameChange(sig, wnVideoSig) < WN_FRAME_SAME", tick, StringComparison.Ordinal);

        // The skip has to come BEFORE the read is counted, or it saves nothing.
        var skipAt  = tick.IndexOf("wnVideoSkips++", StringComparison.Ordinal);
        var countAt = tick.IndexOf("++wnVideoReads", StringComparison.Ordinal);
        Assert.True(skipAt >= 0 && skipAt < countAt,
            "The unchanged-frame skip now happens after the read has been counted and sent.");
    }

    /// <summary>
    /// The threshold is a guess about someone else's camera, so it is never the last word: a locked
    /// -off overhead shot on a table would otherwise be skipped forever and the screen would sit
    /// there looking like it was watching.
    /// </summary>
    [Fact]
    public void A_wrong_threshold_cannot_stop_the_loop_reading_forever()
    {
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");
        Assert.Contains("wnVideoSkips < WN_FRAME_MAX_SKIPS", tick, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a frame that reached the model becomes the one to compare against. Recording the
    /// signature of a frame whose read failed would make the next identical frame look like old
    /// news, and the lot would never be read at all.
    /// </summary>
    [Fact]
    public void A_frame_whose_read_failed_is_not_remembered_as_read()
    {
        var tick = Between(Js, "async function wnVideoTick()", "\n  }");

        var okAt      = tick.IndexOf("if (!res.ok)", StringComparison.Ordinal);
        var rememberAt = tick.IndexOf("wnVideoSig = sig", StringComparison.Ordinal);
        Assert.True(okAt >= 0 && rememberAt > okAt,
            "The frame signature is now recorded before the response is known to be good, so a failed " +
            "read makes the next identical frame look like one already seen.");
    }

    // ── Stopping ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three ways out, and the screen-share and the AI spend have to end on all of them. Switching
    /// tabs holds this screen in the DOM, so "hidden" is not "stopped" unless something says so.
    /// </summary>
    [Fact]
    public void It_stops_on_the_share_ending_on_typing_over_the_item_and_on_leaving_the_screen()
    {
        Assert.Contains("t.addEventListener('ended', () => wnStopVideoWatch(", Js, StringComparison.Ordinal);
        Assert.Contains("you typed over the item", Js, StringComparison.Ordinal);
        Assert.Contains("onHide: () => wnStopVideoWatch('')", Js, StringComparison.Ordinal);

        // And stopping actually releases the camera-bar share, rather than just cancelling the timer.
        var stop = Between(Js, "function wnStopVideoWatch(why)", "\n  }");
        Assert.Contains("getTracks().forEach(t => t.stop())", stop, StringComparison.Ordinal);
        Assert.Contains("clearInterval(wnVideoTimer)", stop, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_button_the_capture_surface_and_the_status_line_are_all_on_the_page()
    {
        Assert.Contains("id=\"wn-video-btn\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-video-el\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-video-canvas\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-video-status\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('wn-video-btn', 'click', wnToggleVideoWatch)", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// #wn-say is the one live region on this screen. A second one announcing a frame read every
    /// twenty seconds during a live sale leaves a screen reader saying nothing usable.
    /// </summary>
    [Fact]
    public void The_status_line_is_visual_only_and_not_a_second_live_region()
    {
        var line = Between(Html, "id=\"wn-video-status\"", ">");

        Assert.DoesNotContain("aria-live", line, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"alert\"", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// app.js changed in this change, so every returning browser has to be told to fetch it — the
    /// live video read is entirely in that file, and a cached copy is a button that does nothing.
    /// </summary>
    [Fact]
    public void The_script_is_stamped_past_this_change()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 149);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" is no longer in the file.");

        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"\"{end}\" no longer follows \"{start}\".");

        return text[from..to];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
