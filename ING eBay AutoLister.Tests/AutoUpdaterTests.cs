using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The automatic updater's judgement calls, the ones that decide whether an install happens under a
/// seller's feet: what counts as them being busy, which copy is allowed to update itself, what the
/// release must carry before its installer is trusted, and the installer's own part in bringing the
/// app back afterwards.
/// </summary>
public class AutoUpdaterTests
{
    [Theory]
    [InlineData("POST", "/api/listings", true)]
    [InlineData("PUT", "/api/settings", true)]
    [InlineData("DELETE", "/api/drafts/3", true)]
    [InlineData("GET", "/", true)]                   // opening the app
    [InlineData("GET", "/index.html", true)]
    [InlineData("GET", "/api/update/status", false)] // the banner asking
    [InlineData("GET", "/api/radar/status", false)]  // an open tab polling
    [InlineData("HEAD", "/api/app/build", false)]
    public void Writes_and_page_opens_are_activity_and_polling_is_not(string method, string path, bool expected)
        => Assert.Equal(expected, AutoUpdater.CountsAsActivity(method, path));

    [Fact]
    public void Only_the_copy_in_program_files_updates_itself()
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
        Assert.True(AutoUpdater.IsInstalledCopy(@"C:\Program Files\ING Mining\ING AutoLister\AutoListerB1.exe", roots));
        Assert.True(AutoUpdater.IsInstalledCopy(@"c:\program files\ING Mining\ING AutoLister\AutoListerB1.exe", roots));
        Assert.False(AutoUpdater.IsInstalledCopy(@"C:\Users\dev\source\repos\app\bin\Debug\net10.0-windows\AutoListerB1.exe", roots));
        Assert.False(AutoUpdater.IsInstalledCopy(@"C:\Program FilesX\evil\AutoListerB1.exe", roots));
        Assert.False(AutoUpdater.IsInstalledCopy(null, roots));
        Assert.False(AutoUpdater.IsInstalledCopy(@"C:\Program Files\x.exe", [""]));
    }

    [Fact]
    public void The_release_installer_and_its_digest_are_read_from_the_release()
    {
        const string json = """
            {"tag_name":"v2.6.5","name":"v2.6.5",
             "assets":[
               {"name":"notes.txt","browser_download_url":"https://x/notes.txt","digest":"sha256:00","size":3},
               {"name":"ING-AutoLister-Setup.msi",
                "browser_download_url":"https://github.com/essquireo0o/eBayAutoLister/releases/download/v2.6.5/ING-AutoLister-Setup.msi",
                "digest":"sha256:99C05FF2E23A8F2334802BA51D328545C6467D57BF7C450778FC38748FA4FDA4",
                "size":55181860}]}
            """;
        var s = UpdateChecker.Parse(json, "2.6.4");
        Assert.True(s.UpdateAvailable);
        Assert.Equal("2.6.5", s.Latest);
        Assert.Equal("https://github.com/essquireo0o/eBayAutoLister/releases/download/v2.6.5/ING-AutoLister-Setup.msi", s.InstallerUrl);
        Assert.Equal("99c05ff2e23a8f2334802ba51d328545c6467d57bf7c450778fc38748fa4fda4", s.InstallerSha256);
        Assert.Equal(55181860L, s.InstallerBytes);
    }

    [Fact]
    public void A_release_without_a_verifiable_installer_is_announced_but_carries_nothing_to_install()
    {
        var noAssets = UpdateChecker.Parse("""{"tag_name":"v9.0.0","name":"v9"}""", "2.6.5");
        Assert.True(noAssets.UpdateAvailable);
        Assert.Null(noAssets.InstallerUrl);
        Assert.Null(noAssets.InstallerSha256);

        var wrongHash = UpdateChecker.Parse("""
            {"tag_name":"v9.0.0","assets":[{"name":"ING-AutoLister-Setup.msi","browser_download_url":"https://x/a.msi","digest":"md5:abc"}]}
            """, "2.6.5");
        Assert.Equal("https://x/a.msi", wrongHash.InstallerUrl);
        Assert.Null(wrongHash.InstallerSha256);

        var noTag = UpdateChecker.Parse("""{"name":"draft"}""", "2.6.5");
        Assert.False(noTag.UpdateAvailable);
        Assert.Null(noTag.Latest);
    }

    [Fact]
    public void The_installer_brings_the_app_back_after_an_automatic_install()
    {
        // A passive install has no finish screen and has just terminated the app; without this row
        // an automatic update ends with the seller's tray icon simply gone.
        var wxs = ReadRepoFile("installer.wxs");
        Assert.Contains(@"<Property Id=""AUTOUPDATE"" Secure=""yes"" />", wxs);
        Assert.Contains(@"<Custom Action=""LaunchApplication"" After=""InstallFinalize"" Condition=""AUTOUPDATE=1 AND NOT Installed AND NOT REMOVE"" />", wxs);
    }

    [Fact]
    public void The_updater_is_wired_into_the_desktop_app_only()
    {
        var program = ReadRepoFile(Path.Combine("ING eBay AutoLister", "Program.cs"));
        Assert.Contains("builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoUpdater>());", program);
        Assert.Contains("autoUpdater.TrackRequest(ctx.Request.Method", program);
        Assert.Contains("app.MapPost(\"/api/update/install\"", program);

        // Each of the three lives inside a desktop-only block: the hosted app has no installer.
        foreach (var marker in new[] { "AddSingleton<AutoUpdater>()", "autoUpdater.TrackRequest", "/api/update/install" })
        {
            var at = program.IndexOf(marker, StringComparison.Ordinal);
            var guard = program.LastIndexOf("#if !HOSTED", at, StringComparison.Ordinal);
            var close = program.LastIndexOf("#endif", at, StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard > close, $"{marker} is not inside a #if !HOSTED block");
        }
    }

    private static string ReadRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, name);
        Assert.True(File.Exists(path), "missing repo file: " + path);
        return File.ReadAllText(path);
    }
}
