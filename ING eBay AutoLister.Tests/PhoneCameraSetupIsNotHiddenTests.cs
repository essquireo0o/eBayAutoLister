namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The ordinary iPhone workflow is native-camera capture, not certificate setup.
/// </summary>
public class PhoneCameraSetupIsNotHiddenTests
{
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void The_primary_action_says_what_it_does()
    {
        Assert.Contains("Use iPhone camera", Html, StringComparison.Ordinal);
        Assert.Contains("Scan to take photos", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_visible_steps_use_the_phones_normal_camera()
    {
        var block = PhoneBlock();
        Assert.Contains("opens its normal rear camera", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Each one appears in the photo strip", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No certificate and no setup", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Certificate_install_controls_are_not_in_the_desktop_flow()
    {
        var block = PhoneBlock();
        Assert.DoesNotContain("pb-trust-qr", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Certificate Trust Settings", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VPN & Device Management", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_browser_fetches_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 172);
    }

    private static string PhoneBlock()
    {
        var start = Html.IndexOf("<div id=\"pb-phone-panel\"", StringComparison.Ordinal);
        var end = Html.IndexOf("</aside>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the iPhone camera panel is missing");
        return Html[start..end];
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
