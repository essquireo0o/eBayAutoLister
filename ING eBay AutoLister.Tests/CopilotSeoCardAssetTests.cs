using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Listing Copilot's SEO card promises a whole-listing rewrite, and the seller's one hard rule
/// is that it must not touch their photos. Both live in places nothing else checks — one in HTML,
/// one in a background job — so both are asserted here.
///
/// The card spent months headed "Rewrite every title for search" while the engine behind it was
/// already rewriting the description and filling item specifics. Nobody could tell from the screen
/// that the rest of the feature existed, and it was asked for over and over as if it were missing.
/// </summary>
public class CopilotSeoCardAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void TheCardDoesNotAdvertiseItselfAsATitleFixer()
    {
        var card = SeoCard();
        Assert.DoesNotContain("Rewrite every title for search", card);
        // What it actually rewrites has to be on the card, in the seller's words.
        foreach (var promised in new[] { "title", "subtitle", "description", "item specifics" })
            Assert.Contains(promised, card, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCardSaysThePhotosAreLeftAlone()
    {
        // The seller asked for this explicitly and repeatedly. If the promise is not on the card,
        // they have to take it on faith.
        Assert.Contains("photos are not touched", SeoCard(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListingsCanBePickedWithoutHuntingForTheScanButton()
    {
        Assert.Contains("copilot-seo-pick", Html);
        Assert.Contains("openCopilotSeoPicker", Js);
        // The card itself is a way in, not just the small button.
        Assert.Contains(".copilot-card[data-action=\"seo\"]", Js);
    }

    [Fact]
    public void BothWaysToStartAreStillThere()
    {
        // Every listing, or just the ones ticked. Losing either is losing half the feature.
        Assert.Contains("copilot-seo-apply", Html);
        Assert.Contains("copilot-seo-apply-selected", Html);
    }

    [Fact]
    public void TheRewriteKeepsTheListingsOwnPhotos()
    {
        // The guarantee itself, at the only point that matters: what goes into the draft.
        var before = new ListingData
        {
            Title = "Antminer S19 95TH/s",
            ImageUrls = ["https://i.ebayimg.com/one.jpg", "https://i.ebayimg.com/two.jpg"],
        };

        // Whatever the model returns — a different set, a longer set, nothing at all.
        var after = new ListingData
        {
            Title = "Antminer S19 95TH/s SHA-256 Bitcoin Miner — Tested, Ships from US",
            ImageUrls = ["https://example.invalid/hallucinated.jpg"],
        };

        after.ImageUrls = [.. before.ImageUrls];

        Assert.Equal(before.ImageUrls, after.ImageUrls);
        Assert.DoesNotContain("https://example.invalid/hallucinated.jpg", after.ImageUrls);
    }

    [Fact]
    public void AFinishedRewriteCanBeOpened()
    {
        // A draft the seller cannot reach is indistinguishable from a run that did nothing. The
        // filename is what carries them back to it, so it has to survive into the result.
        var withDraft = new CopilotSeoResult("110123", true, "old title", "new title", null,
            "Saved as a draft in the app", "new-title_1753800000.json");
        Assert.Equal("new-title_1753800000.json", withDraft.DraftFile);

        // Still constructible without one — an eBay Seller Hub draft has a URL instead.
        var hub = new CopilotSeoResult("110124", true, "old", "new", "https://ebay.com/sh/lst/drafts", null);
        Assert.Null(hub.DraftFile);

        Assert.Contains("draftFile = drafts.SaveDraft", ReadSource("CopilotSeoJob.cs"));
        Assert.Contains("openCopilotDrafts", Js);
        Assert.Contains("copilot-seo-open-drafts", Js);
    }

    [Fact]
    public void TheSellerIsNotSentToSellerHubForADraftThatIsNotThere()
    {
        // eBay's draft API is a Limited Release most accounts do not have. When it 404s the rewrite
        // is saved in the app instead, and the old copy still said "Publish them from eBay Seller
        // Hub" — sending the seller to look for something that was never put there.
        var openDraftsBlock = Js[Js.IndexOf("const whereText", StringComparison.Ordinal)..];
        openDraftsBlock = openDraftsBlock[..openDraftsBlock.IndexOf("const head", StringComparison.Ordinal)];

        Assert.Contains("appDrafts.length && !hubDrafts.length", openDraftsBlock);
        Assert.Contains("saved as drafts in this app", openDraftsBlock);
    }

    [Fact]
    public void TheJobOverwritesTheModelsPhotosWithTheLiveOnes()
    {
        // Reading the source is the only way to assert this without a live eBay account: the job
        // takes the photos from `current` (what eBay returned) and not from `improved`.
        var job = ReadSource("CopilotSeoJob.cs");
        Assert.Contains("improved.ImageUrls = [.. current.ImageUrls", job);
    }

    private static string SeoCard()
    {
        var start = Html.IndexOf("data-action=\"seo\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the SEO card is gone from the Listing Copilot");
        var end = Html.IndexOf("</article>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the SEO card markup is malformed");
        return Html[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
