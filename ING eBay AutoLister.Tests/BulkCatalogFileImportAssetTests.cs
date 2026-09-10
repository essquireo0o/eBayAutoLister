namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Locks the three layers of picture/PDF catalog intake together. A permissive file picker alone
/// is not a feature: the paste has to be intercepted in the bulk card, PDFs have to reach Claude
/// as document blocks, and every extracted product has to enter the existing quick-fill pipeline.
/// </summary>
public class BulkCatalogFileImportAssetTests
{
    private static readonly string Html    = ReadWebAsset("index.html");
    private static readonly string Js      = ReadWebAsset("app.js");
    private static readonly string Program = ReadProjectFile("Program.cs");
    private static readonly string Claude  = ReadProjectFile("Services", "ClaudeService.cs");

    [Fact]
    public void Bulk_catalog_card_offers_paste_drop_and_browse_for_images_and_pdfs()
    {
        Assert.Contains("Paste a catalog URL, picture, or PDF", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-bulk-file-input\" type=\"file\" accept=\"image/*,application/pdf,.pdf\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-bulk-drop-zone\"", Html, StringComparison.Ordinal);
        Assert.Contains("input?.addEventListener('paste'", Js, StringComparison.Ordinal);
        Assert.Contains("e.stopPropagation();", Js, StringComparison.Ordinal);
        Assert.Contains("zone?.addEventListener('drop'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploaded_catalogs_are_extracted_then_each_product_uses_quick_fill()
    {
        Assert.Contains("'/api/bulk-import/extract-products'", Js, StringComparison.Ordinal);
        Assert.Contains("kind: 'name'", Js, StringComparison.Ordinal);
        Assert.Contains("'/api/quick-fill'", Js, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/bulk-import/extract-products\"", Program, StringComparison.Ordinal);
        Assert.Contains("AnalyzeSupplierFileAsync(req.ImageBase64, mime)", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refresh_in_the_middle_of_a_catalog_resumes_it_rather_than_ending_it()
    {
        // 2026-09-10: "when you hit refresh ... the task you were doing ends." The loop ran only in
        // the tab. Now the job is on disk before the first item and after every item, a load with
        // items left opens the AI page and carries on, and the drafts already saved are kept —
        // resuming must never go through the clear-all that a fresh import starts with.
        Assert.Contains("const NL_BULK_JOB_KEY = 'nlBulkJob';", Js, StringComparison.Ordinal);
        Assert.Contains("for (; job.next < work.length; job.next++, nlBulkJobWrite(job))", Js, StringComparison.Ordinal);

        var init = Slice(Js, "const refreshedPage = location.hash.slice(1);", "history.replaceState(null, '', location.pathname + location.search);");
        Assert.Contains("if (nlBulkPending()) {", init, StringComparison.Ordinal);
        Assert.Contains("handleNav('ai');", init, StringComparison.Ordinal);
        Assert.Contains("nlBulkResume();", init, StringComparison.Ordinal);

        var run = Slice(Js, "async function nlBulkRun(job)", "async function nlAiModify()");
        Assert.DoesNotContain("clearAllSavedDrafts", run, StringComparison.Ordinal);
        Assert.Contains("nlBulkJobClear();", run, StringComparison.Ordinal);
        Assert.Contains("await loadAllDraftsAsTabs();", run, StringComparison.Ordinal);

        // A finished or week-old job is not "in progress".
        var pending = Slice(Js, "function nlBulkPending()", "async function nlBulkResume()");
        Assert.Contains("job.next >= job.work.length", pending, StringComparison.Ordinal);
        Assert.Contains("NL_BULK_JOB_MAX_AGE_MS", pending, StringComparison.Ordinal);
    }

    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"could not find \"{start}\" in app.js");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"could not find \"{end}\" after \"{start}\" in app.js");
        return text[from..to];
    }

    [Fact]
    public void Extracted_supplier_cost_survives_saved_drafts_and_tab_switches()
    {
        Assert.Contains("unitCost: Number(p.wholesaleCostUsd)", Js, StringComparison.Ordinal);
        Assert.Contains("data: { ...data, imageUrls, unitCost: item.unitCost", Js, StringComparison.Ordinal);
        Assert.Contains("if (Object.hasOwn(d, 'unitCost')) set('nl-unit-cost'", Js, StringComparison.Ordinal);
        Assert.Contains("unitCost: $('nl-unit-cost')?.value === '' ? null", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_a_catalog_file_starts_the_import_instead_of_leaving_it_at_ready()
    {
        Assert.Contains("setTimeout(() => nlBulkImport(), 0);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_catalogs_are_sent_as_documents_not_images()
    {
        Assert.Contains("new DocumentContent", Claude, StringComparison.Ordinal);
        Assert.Contains("new DocumentSource { MediaType = \"application/pdf\", Data = base64Image }", Claude, StringComparison.Ordinal);
        Assert.Contains("new ImageContent", Claude, StringComparison.Ordinal);
    }

    private static string ReadWebAsset(string name) =>
        ReadProjectFile("wwwroot", name);

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "ING eBay AutoLister", .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
