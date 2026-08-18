namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The installer's port-9332 reservation. On PCs with Hyper-V/WSL/Docker, Windows sets aside
/// random blocks of ports at every boot, and when a block lands on 9332 the app cannot bind the
/// one port the eBay OAuth relay redirects to — "port in use" with nothing in netstat. The MSI
/// install is the app's only elevated moment, so the reservation happens there; these pin the
/// wiring so an installer edit cannot quietly drop it.
/// </summary>
public class InstallerPortReservationTests
{
    private static readonly string Wxs = ReadRepoFile("installer.wxs");

    [Fact]
    public void The_install_reserves_9332_as_a_persistent_administered_exclusion()
    {
        Assert.Contains("netsh int ipv4 add excludedportrange protocol=tcp startport=9332 numberofports=1 store=persistent", Wxs);
        Assert.Contains(@"Custom Action=""ReservePort9332"" After=""InstallFiles"" Condition=""NOT REMOVE""", Wxs);
    }

    [Fact]
    public void The_reservation_can_reclaim_a_port_already_inside_a_dynamic_block()
    {
        // A plain add fails on the machines that need it most — where a dynamic block already
        // covers 9332. The fallback bounces winnat: stopping it releases the dynamic blocks so
        // the add lands, and starting it brings Hyper-V/WSL networking straight back.
        Assert.Contains("net stop winnat", Wxs);
        Assert.Contains("net start winnat", Wxs);
    }

    [Fact]
    public void The_reservation_runs_elevated_and_never_blocks_the_install()
    {
        // Deferred + no-impersonate is the elevated half of the install; Return="ignore" because
        // a machine where netsh fails must still get the app — the app's own startup message
        // (AppInstance.BindFailureMessage) names the manual fix for that case.
        var action = Slice(Wxs, @"<CustomAction Id=""ReservePort9332""", "/>");
        Assert.Contains(@"Execute=""deferred""", action);
        Assert.Contains(@"Impersonate=""no""", action);
        Assert.Contains(@"Return=""ignore""", action);
    }

    [Fact]
    public void A_real_uninstall_gives_the_reservation_back_but_an_upgrade_keeps_it()
    {
        Assert.Contains("netsh int ipv4 delete excludedportrange protocol=tcp startport=9332 numberofports=1 store=persistent", Wxs);
        // NOT UPGRADINGPRODUCTCODE: a major upgrade runs the old version's uninstall, and the new
        // version still wants the port — only a seller actually leaving gives it back.
        Assert.Contains(@"Custom Action=""UnreservePort9332"" Before=""RemoveFiles"" Condition=""REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE""", Wxs);
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
