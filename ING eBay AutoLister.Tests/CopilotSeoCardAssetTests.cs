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
    public void PickingListingsIsTheOnlyThingTheSellerHasToDo()
    {
        var card = SeoCard();
        // The picker, its select-all, its running count, and a start button — all on the card, so
        // the whole job is tick-then-go with no form to fill in afterwards.
        Assert.Contains("copilot-seo-picker", card);
        Assert.Contains("copilot-seo-list", card);
        Assert.Contains("copilot-seo-all", card);
        Assert.Contains("copilot-seo-selcount", card);
        Assert.Contains("copilot-seo-apply", card);

        // The count has to move as boxes are ticked, or "Select all" is the only usable option.
        Assert.Contains("syncCopilotSeoSelection", Js);
        Assert.Contains("' selected'", Js);

        // Progress while it runs comes from the existing poll, not from a second start path.
        Assert.Contains("/api/copilot/improve-seo/status", Js);
        Assert.Contains("/api/copilot/improve-seo/start", Js);
    }

    [Fact]
    public void TheRewritePassIsToldToFillTheBlanksAndInventNothing()
    {
        // The half-empty item specifics are where the missing views are, and a wrong one is worse
        // than a blank: it brings the wrong buyer and ends in an item-not-as-described return. Both
        // halves of that have to survive in the prompt.
        var claude = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "ClaudeService.cs"));
        var seoPrompt = claude[claude.IndexOf("public async Task<ListingData> ImproveSeoAsync", StringComparison.Ordinal)..];
        seoPrompt = seoPrompt[..seoPrompt.IndexOf("return improved;", StringComparison.Ordinal)];

        Assert.Contains("NEVER INVENT", seoPrompt);
        Assert.Contains("LEAVE IT OUT rather than guess", seoPrompt);
        Assert.Contains("that is MISSING", seoPrompt);

        // Every seller-controlled text field is in front of the model, not just the title.
        foreach (var field in new[] { "ConditionDescription", "req.Mpn", "req.Subtitle", "req.Brand", "req.ItemSpecifics" })
            Assert.Contains(field, seoPrompt);

        // And the money and the postage are named as untouchable.
        Assert.Contains("DO NOT CHANGE", seoPrompt);
        Assert.Contains("[.. req.ImageUrls ?? []]", seoPrompt);
    }

    [Theory]
    // A different set, a longer set, a shorter set, nothing at all — the answer is the same each
    // time: the buyer sees the photos that were already on the listing.
    [InlineData("https://example.invalid/hallucinated.jpg")]
    [InlineData("https://i.ebayimg.com/one.jpg", "https://i.ebayimg.com/two.jpg", "https://example.invalid/extra.jpg")]
    [InlineData("https://i.ebayimg.com/two.jpg", "https://i.ebayimg.com/one.jpg")]
    [InlineData("https://i.ebayimg.com/one.jpg")]
    [InlineData]
    public void TheRewriteKeepsTheListingsOwnPhotos(params string[] whateverTheModelReturned)
    {
        // The guarantee itself, at the only point that matters: what goes into the draft.
        var current = new ListingData
        {
            Title = "Antminer S19 95TH/s",
            ImageUrls = ["https://i.ebayimg.com/one.jpg", "https://i.ebayimg.com/two.jpg"],
        };

        var improved = new ListingData
        {
            Title = "Antminer S19 95TH/s SHA-256 Bitcoin Miner — Tested, Ships from US",
            ImageUrls = [.. whateverTheModelReturned],
        };

        CopilotSeoJob.KeepSellerTerms(current, improved);

        // Same URLs, same order, none added, none dropped.
        Assert.Equal(new[] { "https://i.ebayimg.com/one.jpg", "https://i.ebayimg.com/two.jpg" }, improved.ImageUrls);
        Assert.DoesNotContain("https://example.invalid/hallucinated.jpg", improved.ImageUrls);
        Assert.DoesNotContain("https://example.invalid/extra.jpg", improved.ImageUrls);

        // And a later edit to the rewrite cannot reach back into the live listing's own list.
        improved.ImageUrls.Add("https://example.invalid/later.jpg");
        Assert.Equal(2, current.ImageUrls.Count);
    }

    [Fact]
    public void TheRewriteLeavesTheMoneyAndThePostageAlone()
    {
        // An SEO pass has no business moving a price, and "the model usually leaves it alone" is not
        // a guarantee. A silently changed price is the worst thing this feature could do.
        var current = new ListingData
        {
            Title = "Antminer S19 95TH/s",
            Price = 749.99m,
            Quantity = 3,
            BestOfferEnabled = true,
            AutoAcceptPrice = 700m,
            AutoDeclinePrice = 500m,
            QuantityLimitPerBuyer = 2,
            PackageType = "BULKY_GOODS",
            WeightLbs = 31,
            WeightOz = 8,
            PackageLengthIn = 18,
            PackageWidthIn = 14,
            PackageHeightIn = 12,
            HandlingTimeBusinessDays = 3,
            ItemLocationPostalCode = "33101",
            ItemLocationCountry = "US",
            PrivateListing = true,
            CharityDonationPercentage = 10,
            CharityId = "1234",
        };

        // A model that decided to "help" with every one of them.
        var improved = new ListingData
        {
            Title = "Bitmain Antminer S19 95TH/s Bitcoin ASIC Miner",
            Price = 199.00m,
            Quantity = 1,
            BestOfferEnabled = false,
            AutoAcceptPrice = null,
            AutoDeclinePrice = null,
            QuantityLimitPerBuyer = null,
            PackageType = "LETTER",
            WeightLbs = 1,
            WeightOz = 0,
            PackageLengthIn = 6,
            PackageWidthIn = 4,
            PackageHeightIn = 2,
            HandlingTimeBusinessDays = 1,
            ItemLocationPostalCode = "90210",
            ItemLocationCountry = "CN",
            PrivateListing = false,
            CharityDonationPercentage = 0,
            CharityId = "",
        };

        CopilotSeoJob.KeepSellerTerms(current, improved);

        Assert.Equal(749.99m, improved.Price);
        Assert.Equal(3, improved.Quantity);
        Assert.True(improved.BestOfferEnabled);
        Assert.Equal(700m, improved.AutoAcceptPrice);
        Assert.Equal(500m, improved.AutoDeclinePrice);
        Assert.Equal(2, improved.QuantityLimitPerBuyer);
        Assert.Equal("BULKY_GOODS", improved.PackageType);
        Assert.Equal(31, improved.WeightLbs);
        Assert.Equal(8, improved.WeightOz);
        Assert.Equal(18, improved.PackageLengthIn);
        Assert.Equal(14, improved.PackageWidthIn);
        Assert.Equal(12, improved.PackageHeightIn);
        Assert.Equal(3, improved.HandlingTimeBusinessDays);
        Assert.Equal("33101", improved.ItemLocationPostalCode);
        Assert.Equal("US", improved.ItemLocationCountry);
        Assert.True(improved.PrivateListing);
        Assert.Equal(10, improved.CharityDonationPercentage);
        Assert.Equal("1234", improved.CharityId);

        // The search fields it was asked to rewrite are of course left rewritten.
        Assert.Equal("Bitmain Antminer S19 95TH/s Bitcoin ASIC Miner", improved.Title);
    }

    [Fact]
    public void TheDraftCarriesNoBusinessPolicyOfItsOwn()
    {
        // Policies are not on ListingData, so the rewrite has nothing to say about them — the draft
        // has to come out with them unset, and fall back to the account's saved policies the way any
        // other draft does. Asserted through the same JSON round-trip the job uses, because that is
        // where a stray policy id would appear if one ever got added to the model.
        var improved = new ListingData { Title = "Bitmain Antminer S19 95TH/s", Price = 749.99m, Quantity = 3 };

        var draft = System.Text.Json.JsonSerializer.Deserialize<PostListingRequest>(
            System.Text.Json.JsonSerializer.Serialize(improved))!;

        Assert.Null(draft.FulfillmentPolicyId);
        Assert.Null(draft.PaymentPolicyId);
        Assert.Null(draft.ReturnPolicyId);
        Assert.Equal(749.99m, draft.Price);
        Assert.Equal(3, draft.Quantity);
    }

    [Fact]
    public void FillingTheEmptyItemSpecificsIsWorthADraft()
    {
        // Item specifics are the half-empty field that costs the seller the search filters, so a
        // pass that only filled those in still has to count as a change — judging on the title alone
        // would throw away most of what the rewrite was paid for.
        var before = new ListingData
        {
            Title = "Bitmain Antminer S19 95TH/s Bitcoin ASIC Miner",
            Description = "<div><p>Working miner.</p></div>",
            ItemSpecifics = new Dictionary<string, string> { ["Brand"] = "Bitmain" },
        };

        var after = new ListingData
        {
            Title = before.Title,
            Description = before.Description,
            ItemSpecifics = new Dictionary<string, string>
            {
                ["Brand"] = "Bitmain",
                ["Model"] = "Antminer S19",
                ["Hash Rate"] = "95 TH/s",
            },
        };

        Assert.True(CopilotSeoJob.ContentChanged(before, after));

        // And the seller is told which ones, rather than seeing an unchanged title and assuming
        // nothing happened.
        var changes = CopilotSeoDiff.Describe(before, after);
        Assert.Contains("2 item specifics filled in", changes.Headline);
        Assert.Contains(changes.Lines, l => l.Field == "Model" && l.Kind == "filled" && l.After == "Antminer S19");
        Assert.Contains(changes.Lines, l => l.Field == "Hash Rate" && l.Kind == "filled");
    }

    [Fact]
    public void TheSummaryShowsTheDescriptionAndSpecifics_NotJustTheTitle()
    {
        // The panel promises the full list of changes. A title diff on its own hides the two things
        // that took the most work.
        var before = new ListingData
        {
            Title = "antminer s19 miner L@@K",
            Description = "used miner, works",
            ItemSpecifics = new Dictionary<string, string> { ["Brand"] = "bitmain" },
        };

        var after = new ListingData
        {
            Title = "Bitmain Antminer S19 95TH/s Bitcoin ASIC Miner SHA-256",
            Subtitle = "Tested and running — ships from the US",
            Description = "<div style=\"padding:8px\"><h2>Bitmain Antminer S19</h2><p>" + new string('x', 900) + "</p></div>",
            ConditionDescription = "Light shelf wear on the casing.",
            Brand = "Bitmain",
            Mpn = "S19-95T",
            ItemSpecifics = new Dictionary<string, string>
            {
                ["Brand"] = "Bitmain",
                ["Model"] = "Antminer S19",
            },
        };

        var changes = CopilotSeoDiff.Describe(before, after);

        Assert.Contains("new title", changes.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("description rewritten", changes.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Photos unchanged", changes.Headline);

        Assert.Contains(changes.Lines, l => l.Field == "Description" && l.After.Contains("formatted HTML"));
        Assert.Contains(changes.Lines, l => l.Field == "Subtitle" && l.Kind == "filled");
        Assert.Contains(changes.Lines, l => l.Field == "Condition description" && l.Kind == "filled");
        Assert.Contains(changes.Lines, l => l.Field == "MPN" && l.Kind == "filled");
        Assert.Contains(changes.Lines, l => l.Field == "Brand" && l.Kind == "changed");

        // The description's own HTML never travels in the summary — sixty of those on every poll is
        // half a megabyte every two and a half seconds.
        Assert.DoesNotContain(changes.Lines, l => l.After.Contains("<div"));

        // A rewrite that changed nothing says so rather than inventing a change.
        Assert.Equal("Nothing changed", CopilotSeoDiff.Describe(before, before).Headline);
    }

    [Fact]
    public void ASpecificThatDisappearsIsReportedRatherThanHidden()
    {
        // A dropped specific is a buyer filter the listing falls out of. It is the one outcome here
        // the seller most needs to catch before publishing.
        var before = new ListingData
        {
            ItemSpecifics = new Dictionary<string, string> { ["Brand"] = "Bitmain", ["Model"] = "S19" },
        };
        var after = new ListingData
        {
            ItemSpecifics = new Dictionary<string, string> { ["Brand"] = "Bitmain" },
        };

        var changes = CopilotSeoDiff.Describe(before, after);
        Assert.Contains(changes.Lines, l => l.Field == "Model" && l.Kind == "removed" && l.Before == "S19");
        Assert.Contains("1 removed", changes.Headline);
    }

    [Fact]
    public void TheRunCanBeStopped()
    {
        // Cancel exists on the server and always has; for months nothing on screen could reach it,
        // so a seller who started an eighty-listing sweep by mistake could only close the tab and
        // let it keep spending.
        Assert.Contains("copilot-seo-stop", Html);
        Assert.Contains("/api/copilot/improve-seo/cancel", Js);
        // And it says what it actually does — the listing in progress is finished, not thrown away.
        Assert.Contains("Stop after this listing", Html);
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
