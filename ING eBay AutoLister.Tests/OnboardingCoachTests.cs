using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The checklist is on the dashboard; the three steps that show what the app is <em>for</em> happen
/// on other screens. So "Find Goldmines →" handed a first-day tester a page of thirty controls with
/// the instruction left behind on the page they came from — the right screen and no idea which part
/// of it is the point, which is the commonest way to lose a beta tester who has already done the
/// setup.
///
/// These pin the rail that carries the instruction across: which screen each step is done on, what
/// to do once you are standing there, what will tick it — and, on the front end, that the rail
/// exists, knows when to keep quiet, and never claims a step the server has not.
/// </summary>
public class OnboardingCoachTests
{
    private static readonly string Html = Asset("index.html");
    private static readonly string Js = Asset("app.js");
    private static readonly string Css = Asset("style.css");

    private static OnboardingProgress.Plan Fresh() =>
        OnboardingProgress.Build(new OnboardingProgress.Facts(false, false));

    private static OnboardingProgress.Step Step(string id) => Fresh().Steps.Single(s => s.Id == id);

    // ── What the server sends ────────────────────────────────────────────────

    [Fact]
    public void EveryStepSaysWhatToDoOnceYouAreStandingOnTheScreen()
    {
        // Why sells the step from the dashboard. Here is read by someone already on the page, and
        // a step with one and not the other is half a path.
        foreach (var step in Fresh().Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Here), $"step '{step.Id}' has no instruction");
            Assert.NotEqual(step.Why, step.Here);
        }
    }

    [Fact]
    public void EveryStepSaysWhatWillTickIt()
    {
        // Nothing on this path is ticked by the seller claiming it, which is only a virtue if they
        // can find out what the app is watching for. Otherwise a row that stays unticked after they
        // did the thing reads as a broken checklist rather than as a step not finished.
        foreach (var step in Fresh().Steps)
            Assert.False(string.IsNullOrWhiteSpace(step.Proof), $"step '{step.Id}' never says what ticks it");
    }

    [Fact]
    public void TheScreenAStepIsDoneOnIsTheScreenItsButtonOpens()
    {
        // Two ways of saying the same thing is one too many. Page is parsed from Action so the
        // server can re-point a step at a different screen and the rail follows it.
        foreach (var step in Fresh().Steps)
        {
            if (step.Action.StartsWith("page:", StringComparison.Ordinal))
                Assert.Equal(step.Action["page:".Length..], step.Page);
            else
                Assert.Equal("", step.Page);
        }
    }

    [Fact]
    public void TheTwoLoginsBelongToNoScreenAndTheThreeFlipStepsEachBelongToOne()
    {
        // The logins are a modal and a top-bar button, and they block every screen in the app
        // equally — so the rail carries them wherever the seller is, which is the answer to the
        // other first-day question: why is nothing on this page working.
        Assert.Equal("", Step("key").Page);
        Assert.Equal("", Step("ebay").Page);

        foreach (var id in new[] { "priced", "written", "published" })
            Assert.False(string.IsNullOrWhiteSpace(Step(id).Page), $"step '{id}' has no screen to be done on");
    }

    [Fact]
    public void EveryScreenAStepPointsAtIsOneTheAppCanOpen()
    {
        // A rail that says "go to the Goldmines screen" and a workspace with no such page is a
        // button that does nothing, which is worse than no button.
        var registry = Between(Js, "const WORKSPACE_PAGES = {", "\n  };");

        foreach (var step in Fresh().Steps.Where(s => s.Page.Length > 0))
            Assert.True(registry.Contains($"\n    {step.Page}:", StringComparison.Ordinal),
                $"step '{step.Id}' points at '{step.Page}', which is not a page the workspace knows");
    }

    // ── What the page does with it ───────────────────────────────────────────

    [Fact]
    public void TheRailIsOnThePageAndStartsClosed()
    {
        var rail = Between(Html, "<aside id=\"onboard-coach\"", "</aside>");

        Assert.Contains("hidden", Between(Html, "<aside id=\"onboard-coach\"", ">"), StringComparison.Ordinal);
        foreach (var id in new[] { "coach-bead", "coach-eyebrow", "coach-title", "coach-copy", "coach-proof",
                                   "coach-go", "coach-checklist", "coach-dismiss" })
            Assert.Contains($"id=\"{id}\"", rail, StringComparison.Ordinal);

        Assert.Contains(".onboard-coach", Css, StringComparison.Ordinal);
        Assert.Contains(".onboard-coach.is-win", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailSitsOverTheFeatureScreensAndUnderTheModals()
    {
        // Found the hard way. Eighteen of the app's views render through .opportunity-overlay,
        // which is fixed, opaque and z-index 900 — a rail below that is invisible on exactly the
        // screens it exists for, and its ✕ cannot be clicked. Above the modals and it would sit
        // over the settings dialog its own "Enter Key →" button opens.
        var coach = ZIndex(".onboard-coach {");
        var screens = ZIndex(".opportunity-overlay {");
        var modals = ZIndex(".modal-overlay {");

        Assert.True(coach > screens, $"the rail ({coach}) renders behind the feature screens ({screens})");
        Assert.True(coach < modals, $"the rail ({coach}) renders over the modals ({modals})");
    }

    [Fact]
    public void TheRailLetsGoOfTheSidebarGutterWhereTheScreensDo()
    {
        // Below 1120px the sidebar stops being a column and the feature screens take the full
        // width with it. A rail still holding that gutter floats three hundred pixels in from a
        // left edge that no longer belongs to anything.
        Assert.Contains("left: var(--sidebar-w)", Between(Css, ".onboard-coach {", "}"), StringComparison.Ordinal);

        var release = Css.IndexOf(".onboard-coach { left: 0; }", StringComparison.Ordinal);
        Assert.True(release > 0, "the rail never lets go of the sidebar gutter");
        // And it lets go at the width the screens do, not at one of its own choosing.
        var media = Css.LastIndexOf("@media", release, StringComparison.Ordinal);
        Assert.Contains("max-width: 1120px", Css[media..release], StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailIsWrittenFromEveryFieldTheServerSends()
    {
        var render = Between(Js, "function refreshCoach() {", "\n  }");

        foreach (var field in new[] { "next.here", "next.proof", "next.page", "next.number", "next.title" })
            Assert.Contains(field, render, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailKeepsQuietWhereItWouldBeNoise()
    {
        var render = Between(Js, "function refreshCoach() {", "\n  }");

        // The dashboard has the checklist itself on it. A dismissed panel stays dismissed. And a
        // seller who has done all five is done being coached — scaffolding that stays is a defect.
        Assert.Contains("page !== 'dashboard'", render, StringComparison.Ordinal);
        Assert.Contains("plan.dismissed", render, StringComparison.Ordinal);
        Assert.Contains("plan.firstFlipComplete", render, StringComparison.Ordinal);
        Assert.Contains("coachHiddenForSession", render, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailFollowsTheSellerBetweenScreens()
    {
        // It is the screen the seller is on that decides what the rail says, so switching tabs has
        // to re-ask. Without this the rail keeps giving directions to the page they just left.
        Assert.Contains("refreshCoach()", Between(Js, "function activateWorkspaceTab(tab) {", "\n  }"),
            StringComparison.Ordinal);
        Assert.Contains("refreshCoach()", Between(Js, "function markWorkspaceTabOpen(page) {", "\n  }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AStepThatTicksWhileTheSellerWatchesIsSaidOutLoud()
    {
        // The first sold-comp search, the first draft, the first live listing. All three happen on
        // a screen the dashboard cannot see, so this is the one place they can be announced.
        var noted = Between(Js, "function noteEarnedSteps(plan) {", "\n  }");

        Assert.Contains("coachWin", noted, StringComparison.Ordinal);
        // And the first plan of a session proves nothing — everything on it was already true when
        // the app opened. Congratulating a seller of six weeks on every launch is the failure here.
        Assert.Contains("onboardPlan?.steps", noted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWinIsReadAgainstThePreviousPlanBeforeItIsReplaced()
    {
        var render = Between(Js, "function renderOnboarding(plan) {", "\n  }");
        var noted = render.IndexOf("noteEarnedSteps(plan)", StringComparison.Ordinal);
        var replaced = render.IndexOf("onboardPlan = plan", StringComparison.Ordinal);

        Assert.True(noted > 0 && replaced > noted,
            "the previous plan is overwritten before anything compares against it");
    }

    [Fact]
    public void TheRailOnlyAsksTheServerWhileItIsOnScreen()
    {
        // The three earned steps are earned from a dozen call sites the rail does not own, so it
        // asks the one endpoint that watches all of them — but only while it is visible with a step
        // still open, and never for a hidden tab. /api/onboarding is a read of one local table.
        var poll = Between(Js, "function syncCoachPolling(on) {", "\n  }");

        Assert.Contains("loadOnboarding()", poll, StringComparison.Ordinal);
        Assert.Contains("document.hidden", poll, StringComparison.Ordinal);
        Assert.Contains("clearInterval", poll, StringComparison.Ordinal);
        // Stopped on the way out, or a dismissed rail keeps polling for the life of the process.
        Assert.Contains("syncCoachPolling(false)", Between(Js, "function refreshCoach() {", "\n  }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryButtonOnTheRailIsWired()
    {
        var bind = Between(Js, "function bindSetupChecklist() {", "\n  }");

        Assert.Contains("on('coach-go', 'click', () => runOnboardAction(coachAction))", bind, StringComparison.Ordinal);
        Assert.Contains("on('coach-checklist'", bind, StringComparison.Ordinal);
        Assert.Contains("on('coach-dismiss'", bind, StringComparison.Ordinal);
    }

    [Fact]
    public void HidingTheRailForOneRunIsNotTheSameAsDismissingThePanel()
    {
        // The panel's ✕ is recorded on the server and is final. The rail's silences one run of the
        // app: a tester who waved it away on Tuesday while chasing something else should have it
        // back on Wednesday, still with two steps to go.
        var bind = Between(Js, "function bindSetupChecklist() {", "\n  }");

        Assert.Contains("coachHiddenForSession = true", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("on('coach-dismiss', 'click', () => setOnboardingDismissed", bind, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is not in the file");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{from}' is not followed by '{to}'");
        return source[start..end];
    }

    /// <summary>The z-index a rule declares, so the three that have to stack can be compared.</summary>
    private static int ZIndex(string rule)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            Between(Css, rule, "}"), @"z-index:\s*(\d+)");
        Assert.True(match.Success, $"'{rule}' declares no z-index");
        return int.Parse(match.Groups[1].Value);
    }

    private static string Asset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
