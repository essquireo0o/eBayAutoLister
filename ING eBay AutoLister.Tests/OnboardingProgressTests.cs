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

    // ── A saved key is not a working key ─────────────────────────────────────
    //
    // Step 1 used to tick on a key being saved. A key that lost a character in the paste, a key
    // that has been revoked, and a key on an account with no credit all save exactly as cleanly as
    // a working one — so the tester got a green tick, spent five minutes on the next two steps, and
    // met the real failure at the first analysis.

    private static OnboardingProgress.Facts WithKey(string state) => new(
        HasAiKey: true, EbayConnected: false,
        AiKeyState: state, AiKeyCheckedAt: new DateTimeOffset(2026, 8, 7, 15, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(AiKeyCheck.Rejected)]
    [InlineData(AiKeyCheck.NoCredit)]
    public void AKeyAnthropicWillNotTakeDoesNotTickStepOne(string state)
    {
        var plan = OnboardingProgress.Build(WithKey(state));

        var key = Step(plan, "key");
        Assert.False(key.Done);
        Assert.Equal("next", key.State);
        Assert.Equal(0, plan.Done);
        Assert.False(plan.SetupComplete);
        Assert.Equal("key", plan.NextStepId);
    }

    [Theory]
    [InlineData(AiKeyCheck.Rejected)]
    [InlineData(AiKeyCheck.NoCredit)]
    public void ABrokenKeySaysWhichFailureItIsAndWhereToFixIt(string state)
    {
        var plan = OnboardingProgress.Build(WithKey(state));
        var key = Step(plan, "key");

        // A red row that only says "there is a problem" sends the seller looking. This one names
        // the failure and links to the page that fixes that particular one.
        Assert.NotEqual("", key.Problem);
        Assert.Contains(AiKeyCheck.Describe(state).Headline, key.Problem, StringComparison.Ordinal);
        Assert.StartsWith("https://console.anthropic.com/", key.FixLink, StringComparison.Ordinal);
        Assert.NotEqual("", key.FixLinkLabel);

        // And the panel leads with it, rather than with the same sales line about what the AI does
        // over a key the AI has already refused — but with the short form, because the row below
        // already carries the whole paragraph and the link.
        Assert.Equal("Your Claude key isn't working yet", plan.Headline);
        Assert.Equal(AiKeyCheck.Describe(state).Headline, plan.Sub);
        Assert.NotEqual(key.Problem, plan.Sub);
    }

    [Fact]
    public void ARejectedKeyAndAnEmptyOneAreNotTheSameThing()
    {
        var missing = OnboardingProgress.Build(Fresh());
        var rejected = OnboardingProgress.Build(WithKey(AiKeyCheck.Rejected));

        // Both leave step 1 outstanding; only one of them has something gone wrong to explain.
        Assert.Equal("", Step(missing, "key").Problem);
        Assert.NotEqual("", Step(rejected, "key").Problem);
        Assert.NotEqual(missing.Headline, rejected.Headline);
    }

    [Theory]
    [InlineData(AiKeyCheck.Untested)]
    [InlineData(AiKeyCheck.Unreachable)]
    [InlineData(null)]
    public void AnAnswerThatProvesNothingLeavesStepOneExactlyAsItWas(string? state)
    {
        // The load-bearing case. A tester on a train, or a key never tested at all, must keep the
        // behaviour every install had before this check existed — a saved key ticks step 1.
        var plan = OnboardingProgress.Build(Fresh() with { HasAiKey = true, AiKeyState = state });

        var key = Step(plan, "key");
        Assert.True(key.Done);
        Assert.Equal("", key.Problem);
        Assert.Equal("Key saved.", key.Note);
    }

    [Fact]
    public void ATestedKeyGetsTheStrongerSentenceAndTheDate()
    {
        var plan = OnboardingProgress.Build(WithKey(AiKeyCheck.Works));
        var key = Step(plan, "key");

        Assert.True(key.Done);
        Assert.Equal("", key.Problem);
        // "Saved" and "works" were the two claims this step was conflating. A tested key says so,
        // with the date — a tick with no date reads as a claim.
        Assert.Contains("Anthropic answered", key.Note, StringComparison.Ordinal);
        Assert.Contains("Aug 7", key.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatWorksWithNoDateStillReadsAsASentence()
    {
        var plan = OnboardingProgress.Build(
            Fresh() with { HasAiKey = true, AiKeyState = AiKeyCheck.Works });

        Assert.EndsWith(".", Step(plan, "key").Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyStateIsOnThePlanForTheScreensOutsideThePanel()
    {
        // The settings pill and the dashboard chip read one answer rather than each keeping their
        // own — a green chip over a rejected key was the same lie in a second place.
        Assert.Equal(AiKeyCheck.Rejected, OnboardingProgress.Build(WithKey(AiKeyCheck.Rejected)).AiKeyState);
        Assert.Equal(AiKeyCheck.Untested, OnboardingProgress.Build(Fresh()).AiKeyState);
    }

    [Fact]
    public void ABrokenKeyDoesNotUntickAnythingThatWasActuallyEarned()
    {
        // A seller whose key expired after a month has still priced, written and published. The
        // milestones are evidence of separate things; only step 1 is in question.
        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var plan = OnboardingProgress.Build(Everything(when) with { AiKeyState = AiKeyCheck.Rejected });

        Assert.False(Step(plan, "key").Done);
        Assert.True(Step(plan, "priced").Done);
        Assert.True(Step(plan, "written").Done);
        Assert.True(Step(plan, "published").Done);
        Assert.Equal(4, plan.Done);
        Assert.False(plan.FirstFlipComplete);
    }

    [Fact]
    public void ABrokenKeyOnAnUntouchedInstallStillIsNotAWelcome()
    {
        // ShowWelcome asks "has this install ever done anything", and pasting a key is something.
        Assert.False(OnboardingProgress.Build(WithKey(AiKeyCheck.Rejected)).ShowWelcome);
    }
}
