using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The listing edit drawer opened its description straight into the raw markup — the seller who
/// clicked a listing to change a price was shown
/// <c>&lt;div style="font-family:Arial..."&gt;&lt;h1 style="color:#111;"&gt;</c> where the words
/// should have been. The New Listing form had solved this long before with three tabs (Edit Text,
/// Edit HTML, Preview), and the drawer now uses the same markup and the same code.
///
/// Nothing in C# renders either screen, so nothing in C# notices when a tab is dropped or the raw
/// HTML box becomes the default again. These lock both: the three tabs exist on both screens, the
/// readable tab is the one that opens, and the two screens go on sharing one implementation.
/// </summary>
public class DescriptionTabsAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    /// <summary>The tabs, in the order the seller reads them.</summary>
    private static readonly string[] TheTabs = ["text", "edit", "preview"];

    [Theory]
    [InlineData("f")]   // the listing edit drawer
    [InlineData("nl")]  // the New Listing form
    public void BothScreensOfferAllThreeWaysToEditTheDescription(string prefix)
    {
        var bar = TabBar(prefix);
        Assert.Equal(TheTabs, Regex.Matches(bar, "data-desc-tab=\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value).ToArray());

        // The labels matter as much as the ids — "Edit HTML" is the tab the seller avoids.
        foreach (var label in new[] { "Edit Text", "Edit HTML", "Preview" })
            Assert.Contains(label, bar, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("nl")]
    public void TheReadableTabIsTheOneThatOpens(string prefix)
    {
        // The whole point of the change: raw HTML is a tab you can reach, not the tab you land on.
        var active = Regex.Match(TabBar(prefix), "<button class=\"desc-tab active\" data-desc-tab=\"([a-z]+)\"");
        Assert.True(active.Success, $"no tab is marked active in the {prefix}- description tab bar");
        Assert.Equal("text", active.Groups[1].Value);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("nl")]
    public void TheRawHtmlBoxIsHiddenBehindItsTabAndTheReadableOneIsNot(string prefix)
    {
        // Each editor lives in its own wrapper, and it is the wrapper's `hidden` class that decides
        // what the drawer shows first. The HTML wrapper starts hidden; the text wrapper does not.
        Assert.Contains($"""<div id="{prefix}-desc-edit-wrap" class="hidden">""", Html, StringComparison.Ordinal);
        Assert.Contains($"""<div id="{prefix}-desc-preview-wrap" class="hidden">""", Html, StringComparison.Ordinal);
        Assert.Contains($"""<div id="{prefix}-desc-text-wrap">""", Html, StringComparison.Ordinal);

        // And all three views are actually there to switch between.
        foreach (var id in new[] { $"{prefix}-description", $"{prefix}-desc-text", $"{prefix}-desc-preview" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDrawersDescriptionIsStillTheListingDescriptionField()
    {
        // f-description is what buildPayload sends to eBay. Wrapping it in tabs must not have
        // turned it into a different field, or left it without its label.
        var description = Section("""<label>Listing Description</label>""", "</details>");
        Assert.Contains("<textarea id=\"f-description\"", description, StringComparison.Ordinal);
        Assert.Contains("data-desc-prefix=\"f\"", description, StringComparison.Ordinal);
        Assert.Contains("""description: $('f-description').value""", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void OneImplementationDrivesBothScreens()
    {
        // Two copies of the tab logic would drift, and the drawer's copy is the one that would be
        // forgotten. Both screens go through the same prefixed entry point.
        Assert.Contains("initDescTabs('nl')", Js, StringComparison.Ordinal);
        Assert.Contains("initDescTabs('f')", Js, StringComparison.Ordinal);

        // Nothing may reach across the two tab bars at once: an unscoped `.desc-tab` query would
        // switch the drawer's tabs while the seller is clicking the New Listing form's.
        Assert.DoesNotContain("document.querySelectorAll('.desc-tab')", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('.desc-tab", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void WhateverTheSellerTypedIsWhatGetsSaved()
    {
        // Plain-text edits live in the text box until they are folded back into the HTML. Both
        // payload builders fold first, so saving straight from the text tab cannot lose the words.
        Assert.Contains("descCommitText('f')", Js, StringComparison.Ordinal);
        Assert.Contains("descCommitText('nl')", Js, StringComparison.Ordinal);

        // And loading a listing fills all three tabs rather than just the HTML box.
        Assert.Contains("descSetHtml('f', d.description", Js, StringComparison.Ordinal);
        Assert.Contains("descSetHtml('nl', d.description", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewRendersTheHtmlWithoutRunningIt()
    {
        // The description is written by Claude, or comes back from eBay, or is pasted in from
        // another listing — Preview is rendering markup nobody on this side wrote.
        Assert.Contains("descSanitizeHtml(", Js, StringComparison.Ordinal);

        // Parsing happens inside a <template>, whose contents belong to an inert document. The
        // obvious `div.innerHTML = html` is not equivalent: an <img src=x onerror=…> in a live
        // document's element fires as soon as the load fails, before a single tag is stripped.
        var parser = Section("function descParse(html) {", "\n  }");
        Assert.Contains("document.createElement('template')", parser, StringComparison.Ordinal);
        Assert.Contains("tpl.content.ownerDocument.createElement", parser, StringComparison.Ordinal);

        // And every description read goes through it, not around it.
        foreach (var reader in new[] { "function descSanitizeHtml(html)", "function nlHtmlToText(html)" })
            Assert.Contains("descParse(", Section(reader, "\n  }"), StringComparison.Ordinal);
        Assert.Contains("descParse(originalHtml)", Js, StringComparison.Ordinal);

        var sanitizer = Section("function descSanitizeHtml(html) {", "\n  }");
        foreach (var executable in new[] { "script", "iframe", "object", "embed" })
            Assert.Contains(executable, sanitizer, StringComparison.Ordinal);
        Assert.Contains("attr.startsWith('on')", sanitizer, StringComparison.Ordinal);   // onerror, onload…
        Assert.Contains("javascript:", sanitizer, StringComparison.Ordinal);

        // The preview is the only thing filtered — the textarea keeps the HTML eBay will receive.
        Assert.Contains("preview.innerHTML = descSanitizeHtml(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCharacterBudgetMatchesWhatTheRewriteActuallyWrites()
    {
        // The SEO template lands at 6-7k characters against a 9,000 ceiling. While the counter
        // said 4,000 it flagged every AI-written description as over budget, which taught the
        // seller to ignore it.
        var claude = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "ClaudeService.cs"));
        Assert.Contains("Max 9000 characters total", claude, StringComparison.Ordinal);

        Assert.Contains("const DESC_MAX_CHARS = 9000;", Js, StringComparison.Ordinal);
        Assert.DoesNotContain(" / 4,000'", Js, StringComparison.Ordinal);

        foreach (var prefix in new[] { "f", "nl" })
            Assert.Contains($"""id="{prefix}-desc-count">0 / 9,000<""", Html, StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 4000", Html, StringComparison.Ordinal);
    }

    /// <summary>The one description tab bar belonging to <paramref name="prefix"/>.</summary>
    private static string TabBar(string prefix)
    {
        var marker = $"""<div class="desc-tab-bar" data-desc-prefix="{prefix}">""";
        var start = Html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the {prefix}- description tab bar is gone");
        var end = Html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the {prefix}- description tab bar markup is malformed");
        return Html[start..end];
    }

    private static string Section(string from, string to)
    {
        var source = Html.Contains(from, StringComparison.Ordinal) ? Html : Js;
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone");
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
