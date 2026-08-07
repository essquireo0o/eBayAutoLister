using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Nothing in C# renders the dashboard, so nothing in C# notices when the getting-started panel
/// loses the three steps that say what the app is for, or when the first-run screen quietly stops
/// naming what setup will cost. The server can compute a perfect five-step plan and the seller will
/// never see it if the rows it addresses are not on the page.
/// </summary>
public class OnboardingAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    /// <summary>The five rows the plan writes into, in the order the seller reads them.</summary>
    private static readonly string[] TheRows = ["step1", "step2", "step3", "step4", "step5"];

    // ── The panel ────────────────────────────────────────────────────────────

    [Fact]
    public void EveryStepThePlanReturnsHasARowOnThePage()
    {
        var plan = OnboardingProgress.Build(new OnboardingProgress.Facts(false, false));

        Assert.Equal(5, plan.Steps.Count);
        foreach (var prefix in TheRows)
        {
            Assert.Contains($"id=\"{prefix}-row\"", Html, StringComparison.Ordinal);
            Assert.Contains($"id=\"{prefix}-icon\"", Html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRowsAreInTheOrderThePlanNumbersThem()
    {
        var positions = TheRows.Select(prefix => Html.IndexOf($"id=\"{prefix}-row\"", StringComparison.Ordinal)).ToArray();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p).ToArray(), positions);
    }

    [Fact]
    public void TheOptionalExtraSitsAfterAllFiveOfThem()
    {
        // Facebook was step 4 while the path stopped at "connected". An optional extra above the
        // three steps that show what the app is for is an extra the seller reads as required.
        var facebook = Html.IndexOf("id=\"step6-row\"", StringComparison.Ordinal);

        Assert.True(facebook > 0, "the optional Facebook row is gone");
        Assert.True(facebook > Html.IndexOf("id=\"step5-row\"", StringComparison.Ordinal),
            "the optional row is back above the required ones");
        // And the row app.js paints has to be the row that exists.
        Assert.Contains("markSetupStep('step6'", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("markSetupStep('step4'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeFlipStepsKeepTheirButtonsClickableAfterTheyAreDone()
    {
        // markSetupStep disables a row's `-btn` once it is done, which is right for a login you do
        // once and wrong for a screen worth reopening. These rows use `-go` so they tick and stay
        // live — a "Find Goldmines" button that greys out the moment it works is a bug.
        foreach (var prefix in new[] { "step3", "step4", "step5" })
        {
            Assert.Contains($"id=\"{prefix}-go\"", Html, StringComparison.Ordinal);
            Assert.DoesNotContain($"id=\"{prefix}-btn\"", Html, StringComparison.Ordinal);
            Assert.Contains($"id=\"{prefix}-copy\"", Html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryActionTokenOnThePageIsOneTheHandlerUnderstands()
    {
        var actions = Regex.Matches(Html, "data-onboard-action=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();

        Assert.NotEmpty(actions);
        foreach (var action in actions)
            Assert.True(action is "key" or "ebay" || action.StartsWith("page:"),
                $"runOnboardAction cannot run '{action}'");
    }

    [Fact]
    public void TheProgressBarAndItsCountAreBothOnThePage()
    {
        // The bar alone reads as "nearly there" from any width; the count is the honest version.
        foreach (var id in new[] { "setup-progress-bar", "setup-progress-fill", "setup-progress-count" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains(".setup-progress-fill", Css, StringComparison.Ordinal);
        Assert.Contains(".setup-step.is-next", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadlineAndSubLineAreAddressableSoTheServerCanWriteThem()
    {
        Assert.Contains("id=\"setup-title\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"setup-sub\"", Html, StringComparison.Ordinal);
        Assert.Contains("setText('setup-title'", Js, StringComparison.Ordinal);
        Assert.Contains("setText('setup-sub'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelIsRefreshedOnEveryReturnToTheDashboard()
    {
        // Coming back from the Opportunity Finder or a publish is exactly when a step has just been
        // earned. Without this the tick appears on the next launch, which is nobody's idea of feedback.
        Assert.Contains("loadOnboarding", Js, StringComparison.Ordinal);
        var showDashboard = Between(Js, "function showDashboard() {", "\n  }");
        Assert.Contains("loadOnboarding()", showDashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissingIsRecordedOnTheServerAndCanBeUndone()
    {
        // A dismissal that lives only in the DOM comes back on the next launch and reads as a bug;
        // one with no way back hides the three steps that say what the app is for, forever.
        Assert.Contains("/api/onboarding/dismiss", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"pg-show-onboarding\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('pg-show-onboarding'", Js, StringComparison.Ordinal);
    }

    // ── The first run ────────────────────────────────────────────────────────

    [Fact]
    public void TheWelcomeScreenExistsAndIsClosedByDefault()
    {
        var overlay = Between(Html, "<div id=\"welcome-overlay\"", "</div>");
        Assert.Contains("hidden", overlay, StringComparison.Ordinal);
        Assert.Contains("id=\"welcome-start\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"welcome-later\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWelcomeScreenSaysWhatSetupWillCostBeforeAskingForAnything()
    {
        var welcome = Between(Html, "<div id=\"welcome-overlay\"", "<div id=\"setup-overlay\"");

        // Leaving the price of an Anthropic key to be discovered on the settings screen is how a
        // free beta ends up feeling like a paywall.
        Assert.Contains("$5", welcome, StringComparison.Ordinal);
        Assert.Contains("Anthropic", welcome, StringComparison.Ordinal);
        Assert.Contains("eBay", welcome, StringComparison.Ordinal);
        Assert.Contains("free", welcome, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("minutes", welcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWelcomeScreenIsMarkedSeenTheMomentItOpens()
    {
        // Not when it closes: a seller who shuts the app mid-read must not be greeted again on
        // every launch. The flag is the server's, so clearing browser data does not undo it.
        var open = Between(Js, "function openWelcome() {", "\n  }");
        Assert.Contains("/api/onboarding/welcome-seen", open, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWelcomeScreenLeadsToTheFirstStepRatherThanBackToTheDashboard()
    {
        Assert.Contains("on('welcome-start', 'click', () => { closeWelcome(); openSetupAt('key'); });",
            Js, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStaticCopyOnThePageMatchesWhatTheServerWouldWrite()
    {
        // The fetch can fail. When it does the panel keeps the markup's own wording, so that
        // wording has to be the real thing rather than a placeholder from an older design.
        var plan = OnboardingProgress.Build(new OnboardingProgress.Facts(false, false));
        var checklist = Between(Html, "<div id=\"setup-checklist\"", "<!-- ── Masthead");

        Assert.Contains(plan.Headline, checklist, StringComparison.Ordinal);
        foreach (var step in plan.Steps.Where(s => s.Id is "priced" or "written" or "published"))
            Assert.Contains(step.Why, checklist, StringComparison.Ordinal);

        // And the promise the welcome screen opens with is the same sentence the panel opens with.
        Assert.Contains(OnboardingProgress.Promise, checklist, StringComparison.Ordinal);
        Assert.Contains(OnboardingProgress.Promise, Html[Html.IndexOf("welcome-overlay", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
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

    private static string ReadAsset(string name) =>
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
