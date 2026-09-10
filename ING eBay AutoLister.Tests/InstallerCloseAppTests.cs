namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The installer stops the running app before it touches its files. The app is a tray icon with no
/// window, so Windows Installer's Restart Manager could neither close it nor name it, and every
/// upgrade over a running copy ended in "The setup was unable to automatically close all requested
/// applications" (2026-09-10). These pin the three pieces that replace that: the Util extension's
/// close-application step, its place in the sequence, and Restart Manager staying out of the way.
/// </summary>
public class InstallerCloseAppTests
{
    private static readonly string Wxs = ReadRepoFile("installer.wxs");
    private static readonly string Build = ReadRepoFile("build-installer.ps1");

    [Fact]
    public void The_installer_terminates_the_running_app_itself()
    {
        var close = Slice(Wxs, "<util:CloseApplication", "/>");
        Assert.Contains(@"Target=""AutoListerB1.exe""", close);
        // Terminate, not just ask: the tray window ignores WM_CLOSE, so asking alone is the old dialog again.
        Assert.Contains(@"TerminateProcess=""0""", close);
        Assert.Contains(@"RebootPrompt=""no""", close);
        Assert.Contains(@"xmlns:util=""http://wixtoolset.org/schemas/v4/wxs/util""", Wxs);
    }

    [Fact]
    public void The_app_is_stopped_before_the_old_version_is_removed()
    {
        // RemoveExistingProducts runs the OLD product's uninstall; against a live exe that uninstall
        // queues a delete-on-reboot of the path the NEW exe is about to occupy. The default place the
        // extension puts this step (just before InstallFiles) is too late.
        Assert.Contains(@"<Custom Action=""override Wix4CloseApplications_X64"" Before=""RemoveExistingProducts""", Wxs);
        Assert.Contains(@"Schedule=""afterInstallInitialize""", Slice(Wxs, "<MajorUpgrade", "/>"));
    }

    [Fact]
    public void Restart_manager_is_disabled_so_it_cannot_show_its_dead_end_dialog()
    {
        Assert.Contains(@"<Property Id=""MSIRESTARTMANAGERCONTROL"" Value=""Disable"" />", Wxs);
    }

    [Fact]
    public void The_build_loads_the_util_extension_the_close_step_needs()
    {
        Assert.Contains("-ext WixToolset.Util.wixext", Build);
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone from installer.wxs");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}' in installer.wxs");
        return text[start..end];
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
