namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A QR scan pairs a browser with this installation. Rebuilding or updating the executable may
/// interrupt the connection for a moment, but must not turn that pairing into a dead link.
/// </summary>
public class PhoneCameraPersistenceTests
{
    private static readonly string Phone = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));
    private static readonly string Program = ReadSource("Program.cs");

    [Fact]
    public void The_qr_secret_is_saved_in_the_fixed_user_data_home()
    {
        Assert.Contains("Path.Combine(AppPaths.DataHome, \"phone-camera-pairing.json\")", Phone);
        Assert.Contains("RandomNumberGenerator.GetBytes(32)", Phone);
        Assert.Contains("File.Move(temporary, PairingPath, true);", Phone);
    }

    [Fact]
    public void Starting_the_desktop_app_restores_an_enabled_pairing()
    {
        Assert.Contains("ResumeIfEnabledAsync(CancellationToken.None)", Program);
        Assert.Contains("if (saved is null || !saved.Enabled || !ValidToken(saved.Token)) return null;", Phone);
        Assert.Contains("StartListenerAsync(saved.Token.ToLowerInvariant(), ct)", Phone);
    }

    [Fact]
    public void Ordinary_shutdown_keeps_the_pairing_but_disconnect_disables_it()
    {
        Assert.Contains("public async Task DisableAsync()", Phone);
        Assert.Contains("WritePairing(new(token.ToLowerInvariant(), false))", Phone);
        Assert.Contains("await phone.DisableAsync();", Program);
        Assert.Contains("public async ValueTask DisposeAsync() => await StopAsync();", Phone);
        Assert.DoesNotContain("IdleTimeout", Phone);
    }

    [Fact]
    public void The_open_phone_page_retries_and_reintroduces_its_camera_after_an_update()
    {
        Assert.Contains("if (!r.ok) throw new Error('camera listener is restarting');", Phone);
        Assert.Contains("Lost the connection to your computer — retrying", Phone);
        Assert.Contains("settingsSeq = -1;", Phone);
        Assert.Contains("await sendCaps();", Phone);
        Assert.Contains("Reconnected — ready for the next photo.", Phone);
        Assert.DoesNotContain("This link has expired. Scan the code again.", Phone);
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
