using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The browser half of "nothing you wrote has been lost".
///
/// The server-side rules are executed in <see cref="WorkRecoveryStoreTests"/>; these pin the part
/// that lives in app.js, where every failure is silent. A save whose payload is marked clean before
/// the app confirms it, a beacon whose rejection is never read, a mirror cleared over a newer edit,
/// a recovery list nothing on screen can open — none of those throw, none show up in a log, and each
/// one costs the seller a listing that took real API spend and real minutes to write.
/// </summary>
public class WorkRecoveryAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    // ── The wiring between the two files ────────────────────────────────────

    [Theory]
    [InlineData("nl-recover-btn", "the way back to an unfinished listing")]
    [InlineData("nl-recover-panel", "where the unfinished listings are listed")]
    [InlineData("nl-save-state", "whether the listing on screen is actually stored")]
    [InlineData("recovery-banner", "the unresolved-publish warning")]
    public void EveryElementTheRecoveryPathDrivesExists(string id, string what)
    {
        Assert.True(Html.Contains($"id=\"{id}\""), $"index.html has no #{id} — {what} would render nowhere");
        Assert.True(Js.Contains($"'{id}'"), $"app.js never reaches for #{id} — {what} would sit there dead");
    }

    [Theory]
    [InlineData("nl-save-state--ok")]
    [InlineData("nl-save-state--warn")]
    [InlineData(".nl-recover-panel")]
    [InlineData(".nl-recover-btn")]
    public void EveryClassTheRecoveryPathSetsIsStyled(string cls)
        => Assert.True(Css.Contains(cls.TrimStart('.')),
            $"app.js applies {cls} and style.css does not define it — the state would be invisible");

    [Fact]
    public void TheCachedAssetsAreBumpedSoSellersActuallyGetThis()
    {
        Assert.True(Version(@"app\.js\?v=(\d+)") >= 148, "app.js ?v= was not bumped for the recovery work");
        Assert.True(Version(@"style\.css\?v=(\d+)") >= 91, "style.css ?v= was not bumped for the recovery work");
    }

    // ── The save is not clean until the app says so ─────────────────────────

    /// <summary>
    /// The bug this whole change exists to close. The old code marked the payload clean the moment
    /// the request left the browser, so a save that failed was never sent again: the seller typed
    /// their last sentence, the save failed, and the app's own bookkeeping said it was safe.
    /// </summary>
    [Fact]
    public void ThePayloadIsMarkedCleanOnlyAfterTheAppConfirmsTheSave()
    {
        var save = Function("runAutosave");
        var confirmed = save.IndexOf("data?.saved", StringComparison.Ordinal);
        Assert.True(confirmed > 0, "runAutosave no longer reads the app's answer");

        foreach (Match assignment in Regex.Matches(save, @"lastAutosavePayload\s*="))
            Assert.True(assignment.Index > confirmed,
                "lastAutosavePayload is marked clean before the app confirms the save — a failed save "
                + "would never be retried, and the seller's last edits would be lost silently");
    }

    [Fact]
    public void AFailedSaveIsRetriedRatherThanDropped()
    {
        var save = Function("runAutosave");
        Assert.Contains("scheduleAutosaveRetry()", save);
        Assert.Contains("setSaveState('local-only')", save);
    }

    [Fact]
    public void TheRetryLadderClimbsAndIsCapped()
    {
        var waits = Regex.Match(Js, @"AUTOSAVE_RETRY_MS\s*=\s*\[([^\]]+)\]").Groups[1].Value
            .Split(',').Select(s => int.Parse(s.Trim())).ToList();

        Assert.NotEmpty(waits);
        // Ascending, so a save that keeps failing stops hammering an app that is plainly not there.
        Assert.Equal(waits.OrderBy(w => w), waits);
        Assert.True(waits[0] <= 5000, "the first retry is slow enough that a single dropped request costs real work");
        Assert.True(waits[^1] <= 60_000, "the ladder tops out so far away that a recovered app waits minutes to be noticed");
    }

    // ── The unload save, which there is no second chance at ─────────────────

    /// <summary>
    /// sendBeacon returns false rather than throwing when it declines — over the size cap, or the
    /// queue is full — so a discarded save looks exactly like a delivered one to a caller that
    /// ignores the return. The listings that cross the cap are the long ones, which are the ones
    /// that cost the most to write again.
    /// </summary>
    [Fact]
    public void ARejectedBeaconFallsBackRatherThanLookingLikeASave()
    {
        var send = Function("sendAutosaveOnUnload");

        Assert.Matches(@"if\s*\(\s*navigator\.sendBeacon\(", send);
        Assert.Contains("keepalive: true", send);
    }

    [Fact]
    public void TheBeaconIsOnlyUsedUnderTheSizeTheSpecGuarantees()
    {
        var limit = int.Parse(Regex.Match(Js, @"BEACON_LIMIT_BYTES\s*=\s*(\d+)\s*\*\s*1024").Groups[1].Value) * 1024;

        // The spec's own floor is 64 KB; a payload over it is refused outright, not truncated.
        Assert.InRange(limit, 1, 64 * 1024);
        Assert.Contains("bytes <= BEACON_LIMIT_BYTES", Function("sendAutosaveOnUnload"));
    }

    // ── The copy that outlives the app ──────────────────────────────────────

    [Fact]
    public void TheWorkIsWrittenToThisDeviceBeforeAnythingIsSent()
    {
        var flush = Function("flushAutosave");
        var mirrored = flush.IndexOf("writeLocalMirror(body)", StringComparison.Ordinal);

        Assert.True(mirrored > 0, "nothing is kept on this device, so a save the app never takes is lost with it");
        Assert.True(mirrored < flush.IndexOf("sendAutosaveOnUnload", StringComparison.Ordinal),
            "the unload save is sent before the copy is written — the one path where the app may not survive to retry");
        Assert.True(mirrored < flush.IndexOf("runAutosave(body)", StringComparison.Ordinal),
            "the request goes out before the copy is written");
    }

    /// <summary>
    /// A mirror written while a save was in flight belongs to a later edit. Clearing it on that
    /// save's confirmation would throw away the newer copy — the exact loss this mechanism exists
    /// to prevent, caused by the mechanism itself.
    /// </summary>
    [Fact]
    public void TheLocalCopyIsClearedOnlyWhenItIsTheWorkThatWasConfirmed()
    {
        var clear = Function("clearLocalMirror");

        Assert.Contains("held.payload !== payload", clear);
        Assert.Contains("return", clear);
        Assert.Matches(@"removeItem\(LOCAL_MIRROR_KEY\)", clear);
    }

    [Fact]
    public void WhatThisDeviceIsStillHoldingIsPushedUpBeforeRecoveryIsOffered()
    {
        // Otherwise the one listing most at risk — the save the app never took — is the one missing
        // from the list of what there is to recover.
        Assert.Matches(@"replayLocalMirror\(\)\s*\.finally\(loadRecoverableWork\)", Js);
    }

    // ── Publish ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The comment above this call has always said "before anything is sent", and the code under it
    /// fired the save and moved straight on — two requests racing on the one path where their order
    /// decides what the seller is told afterwards.
    /// </summary>
    [Fact]
    public void ThePublishWaitsForTheSaveItSaysItMakesFirst()
        => Assert.Matches(@"await\s+flushAutosave\(false\)", Js);

    [Fact]
    public void TheDuplicateBrakeIsClaimedBeforeThePublishYields()
    {
        var claim = Js.IndexOf("publishInFlight = true", StringComparison.Ordinal);
        var firstAwait = Js.IndexOf("await flushAutosave(false)", StringComparison.Ordinal);

        Assert.True(claim > 0 && firstAwait > 0);
        Assert.True(claim < firstAwait,
            "the in-flight flag is set after an await — two quick clicks walk straight through a "
            + "guard that yields between its test and its set, and that is two live eBay listings");
    }

    // ── What the seller is shown ────────────────────────────────────────────

    [Fact]
    public void TheDashboardBannerIsKeptForPublishesTheAppCannotVouchFor()
    {
        // The banner was turned off wholesale once because it stacked a row per unfinished draft.
        // Filtering rather than switching off is what keeps it credible AND keeps it.
        Assert.Matches(@"renderRecoveryBanner\(\s*recoverableItems\.filter\(isUrgentRecovery\)\s*\)", Js);
        Assert.Contains("outcomeUnknown || item?.lastError", Function("isUrgentRecovery"));
    }

    [Fact]
    public void OrdinaryDraftsStayReachableInsteadOfBeingSwitchedOff()
    {
        // Work that cannot be reached is work that was lost, whatever the database says.
        Assert.DoesNotContain("SHOW_RECOVERY_BANNER", Js);
        Assert.Contains("renderRecoverButton(recoverableItems)", Js);
        Assert.Contains("bindRecoverButton()", Js);
    }

    /// <summary>
    /// A publish is journalled even when its draft never reached the app, so some rows carry an
    /// outcome to resolve and no listing to put back on screen. Offering Restore on one of those
    /// opens an empty form over whatever the seller was doing.
    /// </summary>
    [Fact]
    public void RestoreIsOfferedOnlyOnRowsThatHaveSomethingToRestore()
        => Assert.Matches(@"hasRestorableContent\(item\)\s*\?", Js);

    [Fact]
    public void ARecoveredDraftKeepsItsOwnRowRatherThanBecomingASecondOne()
    {
        var restore = Function("restoreWork");

        Assert.Contains("workKey = item.key", restore);
        // And the indicator opens on the truth: what is on screen is what the app is holding.
        Assert.Contains("lastAutosavePayload = item.payload", restore);
    }

    // ── Getting it back without being asked ─────────────────────────────────
    //
    // Autosave held every field from the moment it was typed, and recovering it took noticing a
    // Recover button, opening it, and choosing a row. A safety net nobody knows to reach for catches
    // nobody. These pin the rules that make restoring by itself safer than the blank form it
    // replaced — every one of them fails silently, and each failure costs a listing.

    [Fact]
    public void OpeningTheScreenPutsTheLastUnfinishedListingBack()
    {
        // On showAiSection, not inside openNewListingModal: the other routes in — a pasted
        // screenshot, a bulk import — arrive carrying their own content and must open on a blank
        // form, and hanging it on the opener would restore a draft over every one of them.
        Assert.Contains("autoRestoreLatestDraft()", Function("showAiSection"));
        Assert.DoesNotContain("autoRestoreLatestDraft", Function("openNewListingModal"));
    }

    /// <summary>
    /// The rule that decides whether this feature is worth having. A restore that lands on a form
    /// the seller has already typed into destroys work that was never at risk — a strictly worse
    /// loss than the one auto-restore exists to prevent.
    /// </summary>
    [Fact]
    public void ARestoreNeverLandsOnAFormTheSellerHasStarted()
    {
        var restore = Function("autoRestoreLatestDraft");
        var checks = Regex.Matches(restore, @"if\s*\(\s*nlFormHasContent\(\)\s*\)\s*return false;");

        Assert.True(checks.Count >= 2,
            "the form is checked once, not twice — a fetch takes long enough to type a title into, "
            + "and a draft arriving on top of it wipes out what was typed while it was in flight");

        var fetched = restore.IndexOf("/api/work/resume", StringComparison.Ordinal);
        Assert.True(fetched > 0, "autoRestoreLatestDraft no longer asks the app what to resume");
        Assert.True(checks[0].Index < fetched, "nothing is checked before the request goes out");
        Assert.True(checks[^1].Index > fetched, "nothing is re-checked after the answer comes back");
    }

    /// <summary>
    /// Content, not size. Every control on an untouched form already holds a default, so a blank tab
    /// weighs the same as a written listing — which is why the payload can never be judged by length.
    /// </summary>
    [Fact]
    public void WhetherTheFormIsEmptyIsJudgedByTheFieldsThatHoldTheSellersWork()
        => Assert.Contains("hasAutosaveContent(buildNlPayload())", Function("nlFormHasContent"));

    /// <summary>
    /// A form that cannot be read is assumed to have something in it. Being wrong that way costs a
    /// blank form; being wrong the other way costs somebody's listing.
    /// </summary>
    [Fact]
    public void AFormThatCannotBeReadIsLeftAlone()
        => Assert.Matches(@"catch\s*\{[^}]*return true;", Function("nlFormHasContent"));

    [Fact]
    public void TwoOpensInQuickSuccessionDoNotBothFillTheForm()
    {
        var restore = Function("autoRestoreLatestDraft");
        Assert.Contains("if (autoRestoreInFlight) return false;", restore);
        Assert.Contains("autoRestoreInFlight = false", restore);
    }

    /// <summary>
    /// The draft keeps the row it came from. Without that, every open of the screen forks the draft:
    /// the seller sees the same listing back but types into a new key, and the next launch offers the
    /// same work twice — once as what was restored, once as what was typed after restoring it.
    /// </summary>
    [Fact]
    public void AnAutoRestoredDraftKeepsItsOwnRowLikeAManualOneDoes()
    {
        var restore = Function("autoRestoreLatestDraft");
        Assert.Contains("restoreWork(draft, { alreadyOpen: true })", restore);
        // restoreWork is what adopts the key; the auto path deliberately goes through it rather
        // than filling the form itself. See ARecoveredDraftKeepsItsOwnRowRatherThanBecomingASecondOne.
        Assert.Contains("workKey = item.key", Function("restoreWork"));
    }

    /// <summary>
    /// The copy this device is still holding is newer than anything the app can answer with — that
    /// is the whole reason it is held. Asking first would put the older listing on screen and then
    /// autosave it back over the newer one.
    /// </summary>
    [Fact]
    public void TheRestoreWaitsForWhatThisDeviceWasStillHolding()
    {
        var restore = Function("autoRestoreLatestDraft");
        var waited = restore.IndexOf("await mirrorReplay", StringComparison.Ordinal);

        Assert.True(waited > 0, "auto-restore no longer waits for the local mirror to be pushed up");
        Assert.True(waited < restore.IndexOf("/api/work/resume", StringComparison.Ordinal),
            "the app is asked for the newest draft before this device has handed over the newer copy");
    }

    /// <summary>
    /// A seller who navigated off the AI Listing screen while the request was in flight is not
    /// dragged back onto it by an answer arriving late.
    /// </summary>
    [Fact]
    public void ADraftArrivingLateDoesNotReopenAScreenThatWasLeft()
        => Assert.Contains("$('new-listing-overlay')?.classList.contains('hidden')",
            Function("autoRestoreLatestDraft"));

    [Fact]
    public void EveryStateTheSaveLineCanBeAskedForIsOneItCanShow()
    {
        var states = Function("SAVE_STATES");

        foreach (var state in new[] { "saving", "saved", "local-only", "too-big" })
            Assert.Matches($@"'?{Regex.Escape(state)}'?\s*:\s*\{{", states);

        // 'idle' is deliberately not defined: setSaveState clears the line for anything it does not
        // know, which is what an empty form should show.
        Assert.Contains("if (!shown)", Function("setSaveState"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int Version(string pattern)
    {
        var match = Regex.Match(Html, pattern);
        Assert.True(match.Success, "no cache-busting version found for " + pattern);
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>
    /// The source of one function, by brace matching from its declaration. Scoping the assertions
    /// to a function is the difference between "the file mentions this somewhere" and "this happens
    /// on the path that matters".
    /// </summary>
    private static string Function(string name)
    {
        var start = Js.IndexOf($"function {name}(", StringComparison.Ordinal);
        if (start < 0) start = Js.IndexOf($"const {name} =", StringComparison.Ordinal);
        Assert.True(start >= 0, $"app.js no longer defines {name}");

        var open = Js.IndexOf('{', start);
        Assert.True(open > 0, $"could not find the body of {name}");

        var depth = 0;
        for (var i = open; i < Js.Length; i++)
        {
            if (Js[i] == '{') depth++;
            else if (Js[i] == '}' && --depth == 0) return Js[start..(i + 1)];
        }

        Assert.Fail($"unbalanced braces reading {name}");
        return "";
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
