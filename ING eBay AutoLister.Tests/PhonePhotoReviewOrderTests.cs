namespace ING_eBay_AutoLister.Tests;

/// <summary>The photograph is the result, so review precedes the controls used to make another.</summary>
public class PhonePhotoReviewOrderTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void Captured_photo_review_is_immediately_after_the_viewfinder_and_before_controls()
    {
        var stage = Html.IndexOf("id=\"pb-stage\"", StringComparison.Ordinal);
        var review = Html.IndexOf("class=\"pb-review\"", StringComparison.Ordinal);
        var zoom = Html.IndexOf("id=\"pb-zoombar\"", StringComparison.Ordinal);
        var camera = Html.IndexOf("id=\"pb-camera\"", StringComparison.Ordinal);

        Assert.True(stage >= 0 && stage < review && review < zoom && zoom < camera,
            "Photo review must sit below the viewfinder and ahead of zoom and camera controls.");
    }

    [Fact]
    public void A_single_result_is_a_large_complete_photo_not_a_cropped_thumbnail()
    {
        Assert.Contains(".pb-filmstrip:has(.pb-shot:only-child) .pb-shot", Css, StringComparison.Ordinal);
        Assert.Contains("width: min(100%, 760px)", Css, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_review_is_hidden_and_the_new_stylesheet_is_forced_fresh()
    {
        Assert.Contains(".pb-review:has(#pb-filmstrip.hidden) { display: none; }", Css, StringComparison.Ordinal);
        AssetStamp.AtLeast(Html, "style.css?v=", 137);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository root above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
