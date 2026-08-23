namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Opportunity Finder board opened on a $10 "thin" win, three long shots and a -$48 Pass at
/// #5 on "best overall" — "a bunch of junk — if there are no opportunities that's fine but this is
/// a bunch of junk" (owner, 2026-08-20). These pin the four things that made it so and their fixes:
/// the profitability and evidence bars are on by default, the auto-relax can no longer switch them
/// off, a loser never outranks a winner however confident its comps are, and a bar the seller sets
/// is remembered. Profitability means above zero; the old $100 floor hid real positive plays.
/// </summary>
public class OpportunityBoardJunkAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void The_board_opens_with_both_strict_bars_on()
    {
        Assert.Contains("id=\"fb-arb-hide-losers\" type=\"checkbox\" checked", Html);
        Assert.Contains("Only profitable deals", Html);
        Assert.Contains("id=\"fb-arb-proven-only\" type=\"checkbox\" checked", Html);
    }

    [Fact]
    public void The_default_money_bar_keeps_every_positive_play()
    {
        Assert.Contains("const PROFITABLE_NET = 0;", Js);
        Assert.Contains("r.netProfit > PROFITABLE_NET", Js);
        Assert.DoesNotContain("const WORTH_DOING_NET = 100;", Js);
        Assert.Contains("Show ${n} break-even or losing listings", Js);
    }

    [Fact]
    public void The_auto_relax_may_loosen_preferences_but_never_the_evidence_bar_or_the_floor()
    {
        var relax = Slice(Js, "const steps = [", "];");
        Assert.Contains("'warrantyOnly'", relax);
        Assert.Contains("'fastOnly'", relax);
        // These two were the steps that let the junk back in. An empty board under them is an
        // answer, and the empty state (emptyUnderTheBarMessage) says so.
        Assert.DoesNotContain("'provenOnly'", relax);
        Assert.DoesNotContain("'hideLosers'", relax);
        Assert.Contains("emptyUnderTheBarMessage()", Js);
    }

    [Fact]
    public void A_loser_sorts_below_every_winner_whatever_its_confidence()
    {
        // Money first, then how sure we are of it, then the chosen order — a confident -$48 used
        // to sit above an estimated +$60 because confidence sorted first.
        Assert.Contains("rows.sort((a, b) => (makesMoney(b) - makesMoney(a)) || (confidenceTier(a) - confidenceTier(b)) || chosenSort(a, b));", Js);
    }

    [Fact]
    public void A_bar_the_seller_sets_is_remembered_and_counts_as_touched()
    {
        Assert.Contains("localStorage.setItem('arbFilters'", Js);
        Assert.Contains("arbFiltersTouched = arbRestoreFilters();", Js);
        // Every hand-set path saves: the checkboxes and the "untick this bar" buttons alike.
        Assert.Contains("on(id, 'change', () => { arbFiltersTouched = true; arbSaveFilters(); renderArbitrageRows(); });", Js);
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone from app.js");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}'");
        return text[start..end];
    }

    private static string ReadAsset(
        string name,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        // Isolated output keeps the running desktop app's bin directory unlocked. CallerFilePath
        // anchors this search to the checkout even when the test host changes its working folder.
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
