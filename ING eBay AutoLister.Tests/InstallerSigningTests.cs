namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Release signing is a shipping invariant. An unsigned MSI shows Unknown publisher, cannot carry
/// publisher reputation to the next version, and can be blocked outright by Smart App Control.
/// </summary>
public class InstallerSigningTests
{
    private static readonly string Build = ReadRepoFile("build-installer.ps1");
    private static readonly string Publish = ReadRepoFile("publish-update.ps1");

    [Fact]
    public void The_ing_binaries_are_signed_before_wix_packages_them()
    {
        var signExe = Build.IndexOf("Sign-ReleaseFile \"$distDir\\AutoListerB1.exe\"", StringComparison.Ordinal);
        var signDll = Build.IndexOf("Sign-ReleaseFile \"$distDir\\AutoListerB1.dll\"", StringComparison.Ordinal);
        var wix = Build.IndexOf("& wix build", StringComparison.Ordinal);

        Assert.True(signExe >= 0 && signDll > signExe && wix > signDll,
            "the ING-owned executable and assembly must be signed before WiX packages them");
    }

    [Fact]
    public void The_msi_is_signed_after_wix_and_every_signature_is_sha256_timestamped()
    {
        var wix = Build.IndexOf("& wix build", StringComparison.Ordinal);
        var signMsi = Build.IndexOf("Sign-ReleaseFile $msiPath", wix, StringComparison.Ordinal);

        Assert.True(wix >= 0 && signMsi > wix, "the completed MSI must be signed after WiX builds it");
        Assert.Contains("/fd\", \"SHA256\"", Build, StringComparison.Ordinal);
        Assert.Contains("/tr\", $TimestampUrl, \"/td\", \"SHA256\"", Build, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", Build, StringComparison.Ordinal);
    }

    [Fact]
    public void A_public_release_fails_closed_when_signing_is_missing_or_invalid()
    {
        Assert.Contains("Release signing is not configured", Build, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowUnsigned", Build, StringComparison.Ordinal);
        // Unsigned packaging is an explicit, named emergency override; it must never become the
        // default branch or be inferred when signing is unavailable.
        Assert.Contains("[switch]$AllowUnsigned", Publish, StringComparison.Ordinal);
        Assert.Contains("if ($AllowUnsigned)", Publish, StringComparison.Ordinal);
        var normalizedPublish = Publish.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("} else {\n    & pwsh -File \"$root\\build-installer.ps1\" -Version $Version\n}",
                        normalizedPublish, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", Publish, StringComparison.Ordinal);
        Assert.Contains("An unsigned or invalid MSI must never be uploaded", Publish, StringComparison.Ordinal);
        Assert.Contains("Installer has no trusted timestamp", Publish, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_supported_trusted_identity_sources_are_wired()
    {
        Assert.Contains("ING_ARTIFACT_SIGNING_DLIB", Build, StringComparison.Ordinal);
        Assert.Contains("ING_ARTIFACT_SIGNING_METADATA", Build, StringComparison.Ordinal);
        Assert.Contains("/dlib", Build, StringComparison.Ordinal);
        Assert.Contains("/dmdf", Build, StringComparison.Ordinal);
        Assert.Contains("ING_CODESIGN_THUMBPRINT", Build, StringComparison.Ordinal);
        Assert.Contains("1.3.6.1.5.5.7.3.3", Build, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, name));
    }
}
