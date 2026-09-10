using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The uninstaller has to know every place the app puts something. The installer and the app
/// each name their own locations; this keeps the uninstaller's copy of those names from
/// drifting away from them, because a leftover nobody notices is exactly how "I uninstalled
/// it" stops being true (2026-09-10, "I want to be 110% that the uninstaller uninstalled
/// every file").
/// </summary>
public class UninstallerCoverageTests
{
    private static readonly string Script = ReadSource("Uninstall-INGAutoLister.ps1");
    private static readonly string Batch = ReadSource("Uninstall-INGAutoLister.bat");
    private static readonly string Wix = ReadSource("installer.wxs");

    [Fact]
    public void The_batch_file_runs_the_script_and_no_longer_walks_Win32_Product()
    {
        // Win32_Product re-validates every MSI on the machine and can trigger repairs of
        // unrelated products. The Add/Remove registry entry is the right index.
        Assert.Contains("Uninstall-INGAutoLister.ps1", Batch);
        Assert.DoesNotContain("Win32_Product", Batch);
        // The script may explain why it avoids the class; it must not query it.
        Assert.DoesNotContain("Get-WmiObject Win32_Product", Script);
        Assert.DoesNotContain("Get-CimInstance Win32_Product", Script);
        Assert.DoesNotContain("-Class Win32_Product", Script);
        Assert.Contains("CurrentVersion\\Uninstall", Script);
    }

    [Fact]
    public void The_data_folder_the_app_actually_uses_is_the_one_offered_for_deletion()
    {
        // AppPaths.FolderName is where every interactive run persists; the service variant
        // lives under ProgramData with the same name.
        Assert.Contains($"'{AppPaths.FolderName}'", Script);
        Assert.Contains("LOCALAPPDATA", Script);
        Assert.Contains("ProgramData", Script);
        // Deletion of the seller's data is opt-in, never silent.
        Assert.Contains("Read-Host", Script);
        Assert.Contains("[switch]$RemoveData", Script);
        Assert.Contains("[switch]$KeepData", Script);
    }

    [Fact]
    public void Everything_the_installer_declares_is_checked_after_the_MSI_runs()
    {
        // Each of these is a line in installer.wxs. If one is renamed there, it must be
        // renamed here, or the uninstaller will report PASS over a leftover.
        Assert.Contains("Name=\"ING Photo Box phone camera\"", Wix);
        Assert.Contains("'ING Photo Box phone camera'", Script);

        Assert.Contains("Name=\"INGAutoLister\"", Wix);           // HKLM Run value
        Assert.Contains("'INGAutoLister'", Script);

        Assert.Contains("Key=\"Software\\INGMining\\AutoLister\"", Wix);
        Assert.Contains("HKCU:\\SOFTWARE\\INGMining", Script);

        Assert.Contains("startport=9332", Wix);
        Assert.Contains("$ReservedPort  = 9332", Script);

        Assert.Contains("Name=\"ING Mining\"", Wix);              // Program Files and Start Menu folders
        Assert.Contains("'ING Mining\\ING AutoLister'", Script);
        Assert.Contains("Start Menu\\Programs\\ING Mining", Script);
    }

    [Fact]
    public void The_script_verifies_after_removing_and_fails_loudly()
    {
        Assert.Contains("[5] Verification", Script);
        Assert.Contains("exit 1", Script);
        // The audit path must be reachable without elevation, so the seller can look first.
        Assert.Contains("[switch]$Audit", Script);
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
