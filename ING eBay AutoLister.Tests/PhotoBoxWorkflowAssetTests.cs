namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Photo Box is a capture workflow, not merely a remote shutter. These checks keep the six-angle
/// coverage guide wired to the same in-memory photo set that is handed to AI Listing.
/// </summary>
public class PhotoBoxWorkflowAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadProjectFile("Program.cs");
    private static readonly string Enhancer = ReadProjectFile("Services", "PhotoEnhancer.cs");

    [Fact]
    public void StudioExplainsTheSixShotWorkflow()
    {
        Assert.Contains("Six shots. Zero guesswork.", Html);
        Assert.Contains("id=\"pb-plan-progress\"", Html);
        Assert.Contains("id=\"pb-plan-grid\"", Html);
        Assert.Contains("id=\"pb-next-shot\"", Html);
        Assert.Contains("id=\"pb-stage-guide\"", Html);
    }

    [Fact]
    public void CoverageIsDerivedFromTheListingSessionPhotos()
    {
        Assert.Contains("const pbShotPlan = [", Js);
        Assert.Contains("const count = pbSessionSnaps.length;", Js);
        Assert.Contains("pbRenderShotPlan();", Js);
        Assert.Contains("pbShotPlan[i]?.name || 'Extra'", Js);
        Assert.Contains("aria-valuenow", Js);
    }

    [Fact]
    public void WorkflowHasDistinctNextAndCompletedStates()
    {
        Assert.Contains(".pb-plan-step.is-next", Css);
        Assert.Contains(".pb-plan-step.is-done", Css);
        Assert.Contains(".pb-connect-dot.is-live", Css);
        Assert.Contains("details.pb-tips", Css);
    }

    [Fact]
    public void AiEnhanceIsOnByDefaultAndRunsAfterEverySnap()
    {
        Assert.Contains("AI Enhance after every snap", Html);
        Assert.Contains("id=\"pb-auto-enhance\"", Html);
        Assert.Contains("let pbAutoEnhance = true", Js);
        Assert.Contains("await pbEnhancePhoto(r.url)", Js);
        Assert.Contains("/api/photos/enhance", Js);
        Assert.Contains("pbEnhancedSnaps.add(listingUrl)", Js);
    }

    [Fact]
    public void EnhancementUsesAiSubjectDetectionWithAnOfflineFallback()
    {
        Assert.Contains("MapPost(\"/api/photos/enhance\"", Program);
        Assert.Contains("new_session('u2netp')", Enhancer);
        Assert.Contains("mask = np.logical_or(ai_mask, classic)", Enhancer);
        Assert.Contains("ImageEnhance.Brightness", Enhancer);
        Assert.Contains("ImageOps.autocontrast", Enhancer);
        Assert.Contains("ImageEnhance.Sharpness", Enhancer);
        Assert.Contains("canvas = 1600", Enhancer);
        Assert.Contains("subject.putalpha(subject_alpha)", Enhancer);
        Assert.Contains("studio = top * (1.0 - t) + bottom * t", Enhancer);
        Assert.Contains("result.alpha_composite(shadow", Enhancer);
        Assert.DoesNotContain("Image.new('RGB', (canvas, canvas), (255, 255, 255))", Enhancer);
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

    private static string ReadProjectFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine([dir!.FullName, "ING eBay AutoLister", .. parts]);
        Assert.True(File.Exists(path), "missing project file: " + path);
        return File.ReadAllText(path);
    }
}
