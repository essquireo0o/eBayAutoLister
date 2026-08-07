namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The five things that turn a fresh install into a sale, and the order they happen in.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard has always had a setup checklist, and it stopped at "connected". That is the
/// wrong finish line. A beta tester who enters a Claude key and logs into eBay has a configured
/// app and has still not seen it do anything — the screen behind the checklist is a sidebar of
/// thirty features with no way to tell which one is the point. "Setup complete" was being reported
/// as if it were the achievement, and the first five minutes are the whole of what a tester
/// decides on.
/// </para>
/// <para>
/// So the path runs past setup and into the flip: price something against real sold comps, let the
/// AI write the listing, publish it. Each of those is a sentence about money rather than a sentence
/// about configuration, and each is ticked from evidence the app already has — a comp lookup that
/// returned comps, an analysis that produced a draft, a publish that came back with a listing id.
/// Nothing here is ticked by the seller claiming it, and nothing is ticked by opening a screen.
/// </para>
/// <para>
/// This type is pure: it takes a facts record and returns the plan. Where the facts come from —
/// credentials.json for the two logins, <see cref="OnboardingStore"/> for the three milestones —
/// is <c>Program.cs</c>'s business, which is what lets every state below be pinned in a test
/// without a key, a token or a database.
/// </para>
/// </remarks>
public static class OnboardingProgress
{
    /// <summary>Ids of the three milestones that are earned rather than configured.</summary>
    public static class Milestones
    {
        /// <summary>A sold-comp lookup came back with at least one real comparable sale.</summary>
        public const string Priced = "priced";

        /// <summary>An AI analysis produced a listing draft.</summary>
        public const string Written = "written";

        /// <summary>A publish returned a live eBay listing.</summary>
        public const string Published = "published";

        /// <summary>Every id this app will record. Anything else is a typo and is refused.</summary>
        public static readonly string[] All = [Priced, Written, Published];

        public static bool IsKnown(string? id) =>
            !string.IsNullOrWhiteSpace(id) &&
            All.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>The canonical spelling, or null when the id is not one this app records.</summary>
        public static string? Normalize(string? id) =>
            All.FirstOrDefault(known => string.Equals(known, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="HasAiKey">A Claude/Anthropic key is saved. Whether it <em>works</em> is <paramref name="AiKeyState"/>.</param>
    /// <param name="EbayConnected">eBay has issued a token this install still holds.</param>
    /// <param name="PricedAt">When the seller first priced something against sold comps, if ever.</param>
    /// <param name="WrittenAt">When the AI first wrote a listing for them, if ever.</param>
    /// <param name="PublishedAt">When they first published one, if ever.</param>
    /// <param name="Dismissed">The seller closed the panel. Final — the plan is still computed, it just isn't shown.</param>
    /// <param name="WelcomeSeen">The one-time first-run screen has been shown once.</param>
    /// <param name="AiKeyState">
    /// The last thing Anthropic said about the saved key — see <see cref="AiKeyCheck"/>. Defaults to
    /// untested, which is what every install said before the check existed and is treated exactly as
    /// the old behaviour: a saved key ticks step 1.
    /// </param>
    /// <param name="AiKeyCheckedAt">When that was established, for the dated line on a done row.</param>
    /// <param name="EbayLinkState">
    /// The last thing eBay said about the stored sign-in — see <see cref="EbayLinkCheck"/>. Defaults
    /// to untested, which is what every install said before the check existed and is treated exactly
    /// as the old behaviour: a held token ticks step 2.
    /// </param>
    /// <param name="EbayLinkCheckedAt">When that was established, for the dated line on a done row.</param>
    public sealed record Facts(
        bool HasAiKey,
        bool EbayConnected,
        DateTimeOffset? PricedAt = null,
        DateTimeOffset? WrittenAt = null,
        DateTimeOffset? PublishedAt = null,
        bool Dismissed = false,
        bool WelcomeSeen = false,
        string? AiKeyState = null,
        DateTimeOffset? AiKeyCheckedAt = null,
        string? EbayLinkState = null,
        DateTimeOffset? EbayLinkCheckedAt = null);

    /// <param name="Id">Stable id. The UI keys rows off this, never off the number.</param>
    /// <param name="Number">Where it sits in the path, 1-based, for the numbered bead on the row.</param>
    /// <param name="Title">What to do, in the imperative.</param>
    /// <param name="Why">
    /// Why it is worth doing, in money. This is the half the old checklist did not have: a tester
    /// reading only these five lines has read what the app is for.
    /// </param>
    /// <param name="ActionLabel">The button.</param>
    /// <param name="Action">
    /// What the button does, as a token the front end resolves: <c>key</c> opens the settings modal
    /// on the key field, <c>ebay</c> starts the eBay sign-in, <c>page:x</c> opens feature screen x.
    /// </param>
    /// <param name="State">One of <c>done</c>, <c>next</c>, <c>later</c>. Exactly one step is <c>next</c>.</param>
    /// <param name="Note">Shown in place of <paramref name="Why"/> once done — what proved it, and when.</param>
    /// <param name="Problem">
    /// Empty on every healthy step. Non-empty means the step looks finished from the outside and
    /// is not — the seller entered a key and Anthropic will not take it — and the row says so
    /// instead of showing a green tick over a broken setup.
    /// </param>
    /// <param name="FixLink">Where to go to fix <paramref name="Problem"/>, or empty.</param>
    /// <param name="FixLinkLabel">What to call that link.</param>
    public sealed record Step(
        string Id,
        int Number,
        string Title,
        string Why,
        string ActionLabel,
        string Action,
        bool Done,
        string State,
        string Note,
        string Problem = "",
        string FixLink = "",
        string FixLinkLabel = "");

    /// <param name="PercentComplete">Required steps only. The optional extras never move this bar.</param>
    /// <param name="SetupComplete">
    /// Both logins done — the old finish line, kept because the eBay-only screens depend on it. A key
    /// Anthropic has refused does not count: that is a screen away from working, not set up.
    /// </param>
    /// <param name="FirstFlipComplete">All five. The seller has seen the app do the thing it exists to do.</param>
    /// <param name="NextStepId">The single next thing, or null when there is nothing left.</param>
    /// <param name="ShowWelcome">
    /// True only on a genuinely untouched install: nothing configured, nothing earned, never shown.
    /// An existing seller who updates the app must never be shown a "welcome, here's what this is".
    /// </param>
    /// <param name="AiKeyState">
    /// The last thing Anthropic said about the key, so the pages outside this panel — the settings
    /// pill, the dashboard chip — can read one answer rather than each keeping their own.
    /// </param>
    /// <param name="EbayLinkState">The same, for what eBay last said about the stored sign-in.</param>
    public sealed record Plan(
        IReadOnlyList<Step> Steps,
        int Done,
        int Total,
        int PercentComplete,
        bool SetupComplete,
        bool FirstFlipComplete,
        string Headline,
        string Sub,
        string? NextStepId,
        string AiKeyState,
        bool Dismissed,
        bool ShowWelcome,
        string EbayLinkState = EbayLinkCheck.Untested);

    /// <summary>
    /// The one sentence a tester should be able to repeat after five minutes. Kept here rather than
    /// in the markup so the welcome screen and the panel headline cannot drift apart.
    /// </summary>
    public const string Promise =
        "Find something worth buying, price it against what actually sold, let the AI write the listing, "
        + "and publish it to eBay — with the profit after fees on screen before you commit.";

    public static Plan Build(Facts facts)
    {
        // A saved key is not a working key. Anthropic refusing it, or the account behind it having
        // no credit, are the two failures this app can prove — and a tick over either of them sends
        // the tester off to spend five minutes on steps 3-5 before anything tells them why nothing
        // works. Everything else the check can return (a timeout, a rate limit, never asked) leaves
        // step 1 exactly as it behaved before the check existed.
        var keyVerdict = AiKeyCheck.Describe(facts.AiKeyState, facts.AiKeyCheckedAt);
        var keyBroken = facts.HasAiKey && AiKeyCheck.IsDefinitive(keyVerdict.State);
        var keyDone = facts.HasAiKey && !keyBroken;

        // A tested key earns the stronger sentence, because "saved" and "works" are the two things
        // this step was conflating in the first place.
        var keyNote =
            !keyDone ? ""
            : keyVerdict.State != AiKeyCheck.Works ? "Key saved."
            : Stamp("Key saved, and Anthropic answered when we tested it", facts.AiKeyCheckedAt) is { Length: > 0 } dated
                ? dated
                : "Key saved, and Anthropic answered when we tested it.";

        // And a held token is not a working connection, for the same reason and with a worse
        // ending: a revoked grant, an expired refresh token and a consent screen where the selling
        // permissions were never granted all look like "something is stored" from disk, and the
        // failure they cause arrives at step 5 — the publish, the last and most expensive step of
        // the path to reach. Same rule as the key: only an answer that says something about this
        // seller's account may untick the row. eBay being down says nothing about it.
        var ebayVerdict = EbayLinkCheck.Describe(facts.EbayLinkState, facts.EbayLinkCheckedAt);
        var ebayBroken = facts.EbayConnected && EbayLinkCheck.IsDefinitive(ebayVerdict.State);
        var ebayDone = facts.EbayConnected && !ebayBroken;

        var ebayNote =
            !ebayDone ? ""
            : ebayVerdict.State != EbayLinkCheck.Works ? "eBay is connected."
            : Stamp("eBay is connected, and it answered when we tested it", facts.EbayLinkCheckedAt) is { Length: > 0 } ebayDated
                ? ebayDated
                : "eBay is connected, and it answered when we tested it.";

        var steps = new List<Step>
        {
            Make("key", 1,
                "Enter your Claude (Anthropic) API key",
                "This is the AI. It reads your photo and writes the title, item specifics, description and a "
                    + "suggested price. A key costs about $5 of credit and that covers hundreds of listings.",
                "Enter Key →", "key",
                keyDone, keyNote,
                keyBroken ? $"{keyVerdict.Headline} {keyVerdict.WhatToDo}" : "",
                keyBroken ? keyVerdict.Link : "",
                keyBroken ? keyVerdict.LinkLabel : ""),

            Make("ebay", 2,
                "Connect eBay",
                "Your own account, so the app can read what you already have listed, publish new listings, and "
                    + "later import what each one actually sold for.",
                "Log into eBay →", "ebay",
                ebayDone, ebayNote,
                ebayBroken ? $"{ebayVerdict.Headline} {ebayVerdict.WhatToDo}" : ""),

            Make("priced", 3,
                "Price one item against real sold comps",
                "Guessing the price is where the money goes. Type anything you own or might buy and the app "
                    + "shows what the same thing has actually sold for — minus eBay's cut, the label and the "
                    + "box, so the number on screen is what you would keep.",
                "Find Goldmines →", "page:opportunity",
                facts.PricedAt is not null, Stamp("Priced against sold comps", facts.PricedAt)),

            Make("written", 4,
                "Let the AI write a listing",
                "A photo or a product name goes in and a finished listing comes out — title, item specifics, "
                    + "description, category and price. This is the hour per listing you stop spending.",
                "Start a draft →", "page:ai",
                facts.WrittenAt is not null, Stamp("The AI wrote your first listing", facts.WrittenAt)),

            Make("published", 5,
                "Publish it to eBay",
                "The whole loop, end to end. From the moment it is live the app tracks what it cost you, what "
                    + "it sells for and what you actually kept.",
                "Open a draft →", "page:ai",
                facts.PublishedAt is not null, Stamp("Published to eBay", facts.PublishedAt)),
        };

        var done = steps.Count(step => step.Done);
        var total = steps.Count;
        var next = steps.FirstOrDefault(step => !step.Done);

        // Exactly one row is the one to do now. Without this every pending row looks equally
        // urgent, which on a six-row panel is the same as none of them being urgent.
        if (next is not null)
            steps[steps.IndexOf(next)] = next with { State = "next" };

        // "Set up" now means the key works, not that a key exists. Every eBay-only screen that reads
        // this flag was already asking the question this way; it just had no way to find out.
        var setupComplete = keyDone && ebayDone;
        var flipComplete = done == total;

        return new Plan(
            Steps: steps,
            Done: done,
            Total: total,
            PercentComplete: (int)Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero),
            SetupComplete: setupComplete,
            FirstFlipComplete: flipComplete,
            // The key first when both are broken. It is step 1, it is the one a tester is most
            // likely to have got wrong on day one, and a headline that names two problems at once
            // reads as an app that is broken rather than as two things to go and fix in order.
            Headline: keyBroken ? "Your Claude key isn't working yet"
                : ebayBroken ? "Your eBay connection isn't working yet"
                : Headline(done, setupComplete, flipComplete),
            // Which failure it is, and no more. The row below carries the same sentence plus what
            // to do about it and the link that does it — printing the whole paragraph twice on one
            // screen makes both copies read as boilerplate.
            Sub: keyBroken ? keyVerdict.Headline
                : ebayBroken ? ebayVerdict.Headline
                : Sub(next, flipComplete),
            NextStepId: next?.Id,
            AiKeyState: keyVerdict.State,
            Dismissed: facts.Dismissed,
            // Every clause matters. A tester who has already pasted a key, or who is being shown
            // this panel after using the app for a week, is not new and does not get a welcome.
            ShowWelcome: !facts.WelcomeSeen
                && !facts.Dismissed
                && !facts.HasAiKey
                && !facts.EbayConnected
                && facts.PricedAt is null
                && facts.WrittenAt is null
                && facts.PublishedAt is null,
            EbayLinkState: ebayVerdict.State);
    }

    private static Step Make(string id, int number, string title, string why,
        string actionLabel, string action, bool done, string note,
        string problem = "", string fixLink = "", string fixLinkLabel = "") =>
        new(id, number, title, why, actionLabel, action, done, done ? "done" : "later", note,
            problem, fixLink, fixLinkLabel);

    private static string Headline(int done, bool setupComplete, bool flipComplete) =>
        flipComplete    ? "That's the whole loop — you've done it once."
        : setupComplete ? "Set up. Now the three minutes that show you what it's for."
        : done == 0     ? "Start here — about five minutes to your first listing"
                        : "Two logins, then the part that makes money";

    private static string Sub(Step? next, bool flipComplete)
    {
        // The panel's last job is to say what it was for, then get out of the way.
        if (flipComplete)
            return "Sourcing, pricing, writing and publishing — you've now done each one once. Everything else "
                 + "in the sidebar hangs off those four. Close this panel when you're ready.";

        return next?.Why ?? Promise;
    }

    /// <summary>"Done" lines carry the date, because a tick with no date reads as a claim.</summary>
    private static string Stamp(string what, DateTimeOffset? when) =>
        when is null ? "" : $"{what} on {when.Value.ToLocalTime():MMM d}.";
}
