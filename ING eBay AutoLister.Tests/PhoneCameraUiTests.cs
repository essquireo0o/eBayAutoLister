namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The phone page is a camera, not a remote-control form. A local shutter press must show the
/// frame at full size and let the seller retake it before the file enters the Photo Library.
/// </summary>
public class PhoneCameraUiTests
{
    private static readonly string Phone = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));

    [Fact]
    public void The_live_viewfinder_and_shutter_own_the_phone_screen()
    {
        Assert.Contains("height:100dvh", Phone);
        Assert.Contains("class=\"shutter\" id=\"shot\"", Phone);
        Assert.Contains("class=\"status-float busy\"", Phone);
        Assert.DoesNotContain("📸 Take the photo", Phone);
        Assert.DoesNotContain("Ready — press Snap on your computer.", Phone);
    }

    [Fact]
    public void A_phone_shot_is_reviewed_before_it_is_uploaded()
    {
        Assert.Contains("class=\"review hidden\" id=\"review\"", Phone);
        Assert.Contains("id=\"reviewimg\"", Phone);
        Assert.Contains("id=\"retake\"", Phone);
        Assert.Contains("id=\"usephoto\"", Phone);
        Assert.Contains("shoot(true);", Phone);
        Assert.Contains("if (!reviewBeforeSending)", Phone);
        Assert.Contains("const saved = await sendPhoto(pendingPhoto);", Phone);
    }

    [Fact]
    public void A_desktop_shutter_still_saves_without_waiting_for_the_phone()
    {
        Assert.Contains("await shoot(false);", Phone);
        Assert.Contains("Saved to Photo Library", Phone);
        Assert.Contains("id=\"lastphoto\"", Phone);
    }

    [Fact]
    public void Useful_phone_camera_controls_are_available_without_crowding_the_viewfinder()
    {
        Assert.Contains("class=\"controls-sheet\"", Phone);
        Assert.Contains("id=\"phone-exposure\"", Phone);
        Assert.Contains("data-focus=\"macro\"", Phone);
        Assert.Contains("data-wb=\"daylight\"", Phone);
        Assert.Contains("id=\"flipbtn\"", Phone);
        Assert.Contains("id=\"levelquick\"", Phone);
        Assert.Contains("Tap anywhere to focus", Phone);
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", relative);
        Assert.True(File.Exists(path), "missing source: " + path);
        return File.ReadAllText(path);
    }
}
