namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The cost editor on Money Made, and the two ways it refused to take a number.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the owner's own data, 2026-08-24: 78 sales, <c>unitCost</c> null on every one of
/// them, <c>costOfGoods</c> $0.00 on 72, all inherited from ten zero rows in the cost-basis store,
/// and <c>awaitingCost</c> empty. Their report was "i cant change the percentage and it does not
/// like when I put in 0 on new items". Both were real and neither was where it looked.
/// </para>
/// <para>
/// <b>Zero.</b> The inline editor short-circuited when the typed text equalled the text it was
/// prefilled with. The prefill is the rendered figure, and on 72 of 78 rows that figure was
/// "0.00" — inherited, not this sale's own. So typing 0 matched, and the save returned without
/// doing anything, silently, on nearly every row. Rendered text is not state: costSource says
/// where the number came from, and only "flip" means the sale already owns it.
/// </para>
/// <para>
/// <b>Percentage.</b> The dropship keep-% box lives in the awaiting-cost panel, which lists sales
/// with no cost at all — so a zero cost removes the percentage box permanently. The inline cell
/// accepts "40%" itself and always did; nothing said so, because its placeholder read "0.00".
/// </para>
/// </remarks>
public class EarningsCostEditorTests
{
    private static string Js => ReadAsset("app.js");

    // ── Zero ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_unchanged_guard_only_fires_when_the_sale_owns_the_figure()
    {
        // The guard must test costSource, not just the text. Without that clause, an inherited
        // "0.00" is indistinguishable from one the seller typed, and typing 0 does nothing.
        Assert.Contains("input.dataset.costsource === 'flip'", Js, StringComparison.Ordinal);

        var guard = Js.IndexOf("input.dataset.costsource === 'flip'", StringComparison.Ordinal);
        var text  = Js.IndexOf("raw === input.dataset.original", StringComparison.Ordinal);
        Assert.True(guard > 0 && text > guard,
            "the costSource clause must gate the text comparison, not sit after it");
    }

    [Fact]
    public void The_editor_is_told_where_its_number_came_from()
    {
        // Carried from the row's button through to the input it is swapped for; without either
        // half the guard above reads undefined and the fix is inert.
        Assert.Contains("data-costsource=\"${f.costSource || 'none'}\"", Js, StringComparison.Ordinal);
        Assert.Contains("data-costsource=\"${btn.dataset.costsource || 'none'}\"", Js, StringComparison.Ordinal);
    }

    // ── Percentage ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cell_says_out_loud_that_it_takes_a_percentage()
    {
        // A title attribute does not exist on a touch device, and the awaiting-cost panel that
        // used to carry the % box is empty for this seller. The placeholder is the only surface
        // left that can advertise it.
        Assert.Contains("placeholder=\"0.00 or 40%\"", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_percentage_form_still_reaches_the_same_save()
    {
        // Guarding the plumbing the placeholder now advertises: "40%" is converted against the
        // unit sale price and saved as a dollar amount, in the same call a typed figure takes.
        Assert.Contains("raw.endsWith('%')", Js, StringComparison.Ordinal);
        Assert.Contains("costFromKeepPct(Number(input.dataset.unitgross)", Js, StringComparison.Ordinal);
    }

    // ── Saying what happened ──────────────────────────────────────────────────────

    [Fact]
    public void A_zero_change_to_the_total_reads_as_an_outcome_not_a_refusal()
    {
        // "Cost saved — $0.00 of real profit added to your total" is what a successful save of a
        // zero cost used to say, and after a run of silent no-ops the seller reads that as the
        // app refusing again.
        Assert.Contains("delta === 0", Js, StringComparison.Ordinal);
        Assert.Contains("you paid nothing for this one", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("delta >= 0", Js, StringComparison.Ordinal);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
