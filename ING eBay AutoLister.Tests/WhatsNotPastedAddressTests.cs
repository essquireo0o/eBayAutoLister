using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The live screen's item box sits beside the tab showing the auction, so it gets the show's URL.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the owner's screen 2026-08-24: they pasted
/// <c>https://www.whatnot.com/live/b059f792-fbd1-4811-af64-54cc653999e8</c> into "What's on screen"
/// and the card came back <b>CAN'T PRICE IT</b>, over a sold search for
/// <c>https www com/live/b059f792-fbd1-4811-af64-54cc653999e8</c> — the tokeniser having split the
/// scheme and host into words and struck "whatnot" out as noise.
/// </para>
/// <para>
/// It was never going to price it. No eBay listing has ever been titled after a Whatnot link, so
/// that search fails identically however many times it runs, and each run spends a live lookup out
/// of the daily allowance to learn something knowable before asking.
/// </para>
/// <para>
/// The refusals matter as much as the catches: an item name that merely looks technical must still
/// be priced, because a box that rejects real lots is worse than one that searches a URL.
/// </para>
/// </remarks>
public class WhatsNotPastedAddressTests
{
    // ── What gets caught ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.whatnot.com/live/b059f792-fbd1-4811-af64-54cc653999e8")]
    [InlineData("https://whatnot.com/live/abc-123")]
    [InlineData("http://www.whatnot.com/live/abc-123?ref=share")]
    [InlineData("www.whatnot.com/live/abc-123")]
    public void The_shows_own_address_is_recognised_as_the_show(string typed)
    {
        var read = WhatnotShowParser.ReadTypedAddress(typed);

        Assert.True(read.IsAddress);
        Assert.True(read.IsWhatnotShow);
    }

    [Theory]
    [InlineData("https://www.ebay.com/itm/123456789")]
    [InlineData("https://example.com/thing")]
    [InlineData("www.google.com/search?q=antminer")]
    public void Any_other_address_is_still_an_address_and_still_not_an_item(string typed)
    {
        var read = WhatnotShowParser.ReadTypedAddress(typed);

        Assert.True(read.IsAddress);
        Assert.False(read.IsWhatnotShow);
    }

    // ── What must still be priced ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Bitmain Antminer S19j Pro 104TH")]
    [InlineData("S19j-Pro-104TH")]                       // hyphens are not a scheme
    [InlineData("Apple Watch Ultra 2 49mm Titanium")]
    [InlineData("1 oz .9999 Fine Gold Bar")]
    [InlineData("whatnot.com")]                          // a word someone could be selling
    [InlineData("Lot of 3 — NO RESERVE 🔥")]
    [InlineData("")]
    [InlineData(null)]
    public void An_item_name_is_left_alone(string? typed)
    {
        var read = WhatnotShowParser.ReadTypedAddress(typed);

        Assert.False(read.IsAddress);
        Assert.False(read.IsWhatnotShow);
    }

    // A name with a space in it is a name, whatever else it contains. This is the guard that keeps
    // the check from ever eating a real lot: hosts do not have spaces, lot names nearly always do.
    [Fact]
    public void Anything_with_a_space_in_it_is_a_name()
    {
        Assert.False(WhatnotShowParser.ReadTypedAddress("see https://example.com for details").IsAddress);
        Assert.False(WhatnotShowParser.ReadTypedAddress("https://example.com and more").IsAddress);
    }

    // Not http(s) is not a web address for this purpose — nothing here fetches it, so the only
    // question is whether a sold search would be nonsense, and these are just odd names.
    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://files.example.com/x")]
    public void Only_a_web_address_counts(string typed) =>
        Assert.False(WhatnotShowParser.ReadTypedAddress(typed).IsAddress);

    // ── The endpoint actually asks ────────────────────────────────────────────────

    // The check is worthless if /api/whatsnot/bid does not run it before building the query, and
    // the guard sits in the middle of a long handler where it is easy to lose in a merge.
    [Fact]
    public void The_bid_endpoint_refuses_an_address_before_it_searches()
    {
        var program = ReadProjectFile("Program.cs");

        var guard = program.IndexOf("WhatnotShowParser.ReadTypedAddress(title)", StringComparison.Ordinal);
        var query = program.IndexOf("LiveSearchQuery.Build(title)", StringComparison.Ordinal);

        Assert.True(guard > 0, "the bid endpoint no longer checks for a pasted address");
        Assert.True(query > 0, "the bid endpoint no longer builds a search from the title");
        Assert.True(guard < query, "the address check must run BEFORE the sold query is built");
        Assert.Contains("That is the show's address, not the lot", program, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", name);
        Assert.True(File.Exists(path), "missing project file: " + path);
        return File.ReadAllText(path);
    }
}
