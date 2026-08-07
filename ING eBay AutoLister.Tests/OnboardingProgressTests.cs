using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The first five minutes decide whether a beta tester comes back, and until now the dashboard's
/// answer to "what now?" stopped at "you are connected" — a configured app that has shown them
/// nothing. These pin the path that runs past setup: what it says, in what order, and — the part
/// that matters most — what it refuses to claim the seller has done.
/// </summary>
public class OnboardingProgressTests
{
    private static OnboardingProgress.Facts Fresh() => new(HasAiKey: false, EbayConnected: false);

    private static OnboardingProgress.Facts Everything(DateTimeOffset when) => new(
        HasAiKey: true, EbayConnected: true,
        PricedAt: when, WrittenAt: when, PublishedAt: when);

    private static OnboardingProgress.Step Step(OnboardingProgress.Plan plan, string id) =>
        plan.Steps.Single(step => step.Id == id);

    // ── The path itself ──────────────────────────────────────────────────────

    [Fact]
    public void ThePathRunsPastSetupIntoTheFlip()
    {
        var plan = OnboardingProgress.Build(Fresh());

        // Two logins, then the three things the app exists to do — in the order they happen.
        Assert.Equal(["key", "ebay", "priced", "written", "published"],
            plan.Steps.Select(step => step.Id).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], plan.Steps.Select(step => step.Number).ToArray());
    }

    [Fact]
    public void EveryStepSaysWhyItIsWorthDoing()
    {
        var plan = OnboardingProgress.Build(Fresh());

        // The whole point of the rewrite: a tester who reads only these five lines has read what
        // the app is for. A step with a title and no reason is the checklist this replaced.
        foreach (var step in plan.Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Why), $"step '{step.Id}' has no reason");
            Assert.False(string.IsNullOrWhiteSpace(step.ActionLabel), $"step '{step.Id}' has no button");
            Assert.False(string.IsNullOrWhiteSpace(step.Action), $"step '{step.Id}' has no action");
        }
    }

    [Fact]
    public void EveryActionIsOneTheFrontEndKnowsHowToRun()
    {
        var plan = OnboardingProgress.Build(Fresh());

        // runOnboardAction in app.js understands exactly these three forms. A fourth would render
        // as a button that does nothing, which is worse than no button.
        foreach (var step in plan.Steps)
            Assert.True(step.Action is "key" or "ebay" || step.Action.StartsWith("page:"),
                $"step '{step.Id}' has an action the UI cannot run: {step.Action}");
    }

    // ── What is done, and what is only configured ────────────────────────────

    [Fact]
    public void AFreshInstallHasDoneNothingAndIsHonestAboutIt()
    {
        var plan = OnboardingProgress.Build(Fresh());

        Assert.Equal(0, plan.Done);
        Assert.Equal(5, plan.Total);
        Assert.Equal(0, plan.PercentComplete);
        Assert.False(plan.SetupComplete);
        Assert.False(plan.FirstFlipComplete);
        Assert.All(plan.Steps, step => Assert.False(step.Done));
    }

    [Fact]
    public void BothLoginsDoneIsSetupCompleteAndNotAFinishedFlip()
    {
        // The old checklist's finish line, and the reason this type exists: a seller here has
        // configured everything and still watched the app do nothing.
        var plan = OnboardingProgress.Build(Fresh() with { HasAiKey = true, EbayConnected = true });

        Assert.True(plan.SetupComplete);
        Assert.False(plan.FirstFlipComplete);
        Assert.Equal(2, plan.Done);
        Assert.Equal(40, plan.PercentComplete);
        Assert.Equal("priced", plan.NextStepId);
    }

    [Fact]
    public void AllFiveIsTheOnlyThingThatCountsAsAFinishedFlip()
    {
        var plan = OnboardingProgress.Build(Everything(DateTimeOffset.UtcNow));

        Assert.True(plan.FirstFlipComplete);
        Assert.Equal(5, plan.Done);
        Assert.Equal(100, plan.PercentComplete);
        Assert.Null(plan.NextStepId);
        Assert.All(plan.Steps, step => Assert.Equal("done", step.State));
    }

    [Fact]
    public void APublishedListingWithNoKeyStillLeavesTheKeyOutstanding()
    {
        // Milestones are evidence of separate things, not a ladder. A seller who published by hand
        // from an imported draft has genuinely published, and still has no AI key — the panel must
        // not tick the earlier rows just because a later one happened.
        var plan = OnboardingProgress.Build(Fresh() with { PublishedAt = DateTimeOffset.UtcNow });

        Assert.True(Step(plan, "published").Done);
        Assert.False(Step(plan, "key").Done);
        Assert.False(Step(plan, "priced").Done);
        Assert.Equal("key", plan.NextStepId);
        Assert.Equal(1, plan.Done);
    }

    // ── Exactly one thing to do now ──────────────────────────────────────────

    [Fact]
    public void ExactlyOneStepIsTheNextOne()
    {
        foreach (var facts in new[]
        {
            Fresh(),
            Fresh() with { HasAiKey = true },
            Fresh() with { HasAiKey = true, EbayConnected = true },
            Fresh() with { HasAiKey = true, EbayConnected = true, PricedAt = DateTimeOffset.UtcNow },
        })
        {
            var plan = OnboardingProgress.Build(facts);
            Assert.Single(plan.Steps, step => step.State == "next");
            Assert.Equal(plan.NextStepId, plan.Steps.Single(step => step.State == "next").Id);
        }
    }

    [Fact]
    public void TheNextStepIsTheFirstOneNotDone()
    {
        var plan = OnboardingProgress.Build(Fresh() with { HasAiKey = true });

        Assert.Equal("ebay", plan.NextStepId);
        Assert.Equal("done", Step(plan, "key").State);
        Assert.Equal("later", Step(plan, "priced").State);
    }

    [Fact]
    public void TheSubLineIsAlwaysTheReasonForTheNextStep()
    {
        // The headline says where the seller is; the line under it says why the next thing is worth
        // doing. Restating "you have 3 of 5 steps left" there would waste the only sentence on the
        // panel that is allowed to argue for the app.
        var plan = OnboardingProgress.Build(Fresh() with { HasAiKey = true, EbayConnected = true });

        Assert.Equal(Step(plan, "priced").Why, plan.Sub);
    }

    [Fact]
    public void AFinishedPathSaysSoRatherThanPointingAtANextStep()
    {
        var plan = OnboardingProgress.Build(Everything(DateTimeOffset.UtcNow));

        Assert.DoesNotContain(plan.Steps, step => step.State == "next");
        Assert.Contains("loop", plan.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(plan.Sub));
    }

    // ── Done rows carry their date ───────────────────────────────────────────

    [Fact]
    public void ADoneMilestoneSaysWhenItHappened()
    {
        var when = new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero);
        var plan = OnboardingProgress.Build(Fresh() with { PricedAt = when });

        // A tick with no date reads as a claim. This one is checkable.
        Assert.Contains(when.ToLocalTime().ToString("MMM d"), Step(plan, "priced").Note);
    }

    [Fact]
    public void APendingMilestoneClaimsNothing()
    {
        var plan = OnboardingProgress.Build(Fresh());

        foreach (var id in new[] { "priced", "written", "published" })
            Assert.Equal("", Step(plan, id).Note);
    }

    // ── The first-run screen ─────────────────────────────────────────────────

    [Fact]
    public void TheWelcomeScreenOpensOnAGenuinelyUntouchedInstall()
    {
        Assert.True(OnboardingProgress.Build(Fresh()).ShowWelcome);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("ebay")]
    [InlineData("priced")]
    [InlineData("written")]
    [InlineData("published")]
    [InlineData("seen")]
    [InlineData("dismissed")]
    public void AnythingAtAllHavingHappenedSuppressesTheWelcomeScreen(string what)
    {
        // Greeting a seller of six weeks with "welcome, here's what this is" — after an update, or
        // after they cleared their browser data — is the failure mode this guards against.
        var when = DateTimeOffset.UtcNow;
        var facts = what switch
        {
            "key"       => Fresh() with { HasAiKey = true },
            "ebay"      => Fresh() with { EbayConnected = true },
            "priced"    => Fresh() with { PricedAt = when },
            "written"   => Fresh() with { WrittenAt = when },
            "published" => Fresh() with { PublishedAt = when },
            "seen"      => Fresh() with { WelcomeSeen = true },
            _           => Fresh() with { Dismissed = true },
        };

        Assert.False(OnboardingProgress.Build(facts).ShowWelcome);
    }

    [Fact]
    public void DismissingHidesThePanelWithoutForgettingTheProgress()
    {
        // Dismissal is a preference, not a reset — "show the getting-started steps again" in
        // Settings has to come back to the same five rows in the same state.
        var plan = OnboardingProgress.Build(Fresh() with { HasAiKey = true, Dismissed = true });

        Assert.True(plan.Dismissed);
        Assert.Equal(1, plan.Done);
        Assert.True(Step(plan, "key").Done);
    }

    // ── The milestone vocabulary ─────────────────────────────────────────────

    [Fact]
    public void OnlyTheThreeKnownMilestonesAreAccepted()
    {
        Assert.Equal(["priced", "written", "published"], OnboardingProgress.Milestones.All);
        Assert.True(OnboardingProgress.Milestones.IsKnown("published"));
        Assert.True(OnboardingProgress.Milestones.IsKnown("  Published  "));
        Assert.False(OnboardingProgress.Milestones.IsKnown("publish"));
        Assert.False(OnboardingProgress.Milestones.IsKnown(""));
        Assert.False(OnboardingProgress.Milestones.IsKnown(null));
    }

    [Fact]
    public void NormalizeReturnsTheCanonicalSpellingOrNothing()
    {
        Assert.Equal("priced", OnboardingProgress.Milestones.Normalize("PRICED"));
        Assert.Null(OnboardingProgress.Milestones.Normalize("sold"));
    }
}
