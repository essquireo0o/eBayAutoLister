using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// <see cref="HostedAuthTests"/> proves the auth pipeline refuses a stranger. This proves the app
/// is the thing standing behind it.
/// </summary>
/// <remarks>
/// <para>
/// Program.cs is a 10,000-line top-level file that binds a port, opens a browser and starts a tray
/// icon; no test can run it. So the four calls that put the 189 endpoints behind a sign-in are
/// pinned here by reading the source, along with the order they have to be in — which is not a
/// style preference:
/// </para>
/// <list type="bullet">
///   <item><c>AddAccounts</c> before <c>builder.Build()</c>, or there is no policy to apply.</item>
///   <item><c>UseSignedInUser</c> before the static mounts, or the middleware that would serve a
///         seller's photograph has already run by the time anyone asks who is looking.</item>
///   <item><c>RequireSignIn</c> after the static assets and before the endpoints, or the sign-in
///         page needs a sign-in to render.</item>
/// </list>
/// <para>
/// A future edit that moves one of these past another leaves the app compiling, starting and
/// serving — and quietly public. This is the test that notices.
/// </para>
/// </remarks>
public class HostedAuthWiringTests
{
    private static readonly string Program = ReadSource("Program.cs");

    [Fact]
    public void Program_registers_the_accounts_before_the_app_is_built()
    {
        Assert.InRange(At("HostedAuth.AddAccounts(builder)"), 0, At("var app = builder.Build()"));
    }

    [Fact]
    public void Program_reads_the_session_before_any_static_file_is_served()
    {
        Assert.InRange(At("HostedAuth.UseSignedInUser(app)"), At("app.UseCors()"), At("app.UseStaticFiles"));
    }

    [Fact]
    public void Program_closes_the_endpoints_after_the_ui_assets_and_before_the_endpoints()
    {
        var lastStaticMount = Program.LastIndexOf("app.UseStaticFiles", StringComparison.Ordinal);

        Assert.InRange(At("HostedAuth.RequireSignIn(app)"), lastStaticMount, Program.Length);
        Assert.InRange(At("HostedAuth.RequireSignIn(app)"), 0, FirstMappedApiEndpointAfterTheGate());
    }

    [Fact]
    public void Program_maps_the_sign_in_endpoints()
    {
        Assert.Contains("HostedAuth.MapAccountEndpoints(app)", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The brute-force defences are inside <see cref="HostedAuth.AddAccounts"/> and
    /// <see cref="HostedAuth.UseSignedInUser"/>, so Program.cs calling those is what installs them.
    /// This is the guard against someone adding a second, unlimited sign-in endpoint alongside
    /// them — the throttle is not a middleware and would not cover one.
    /// </summary>
    [Fact]
    public void Program_maps_no_sign_in_of_its_own()
    {
        Assert.DoesNotContain("MapPost(\"/api/auth/", Program, StringComparison.Ordinal);
        Assert.DoesNotContain("UserStore.Verify", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same guard, one layer down. <see cref="PerUserCredentialsTests"/> proves the per-user
    /// store keeps two sellers' eBay accounts apart; this proves the app hands out that store
    /// rather than constructing the single-file one itself. A <c>new CredentialsStore(path)</c>
    /// back in Program.cs would compile, start, and give the whole userbase one eBay account.
    /// </summary>
    [Fact]
    public void Program_hands_out_credentials_through_the_per_user_gate()
    {
        At("PerUserCredentials.AddCredentials(builder");

        Assert.DoesNotContain("AddSingleton(new CredentialsStore(", Program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<CredentialsStore>", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The desktop build is the shipping product and it is not getting a login screen. This is the
    /// guard on that: the switch is a compile-time constant read in one place, and every method on
    /// <see cref="HostedAuth"/> is a no-op when it is false — which, in this build, it is.
    /// </summary>
    [Fact]
    public void The_desktop_build_is_not_a_hosted_build()
    {
        Assert.False(HostedAuth.IsHostedBuild);
    }

    private static int At(string snippet)
    {
        var index = Program.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Program.cs no longer calls {snippet} — the hosted build is open to the internet.");
        return index;
    }

    /// <summary>
    /// The first endpoint mapped after the auth gate. Used as the upper bound rather than the very
    /// first <c>app.Map</c> in the file, because <see cref="HostedAuth.MapAccountEndpoints"/>'s own
    /// allow-listed endpoints are mapped on the line below the gate by design.
    /// </summary>
    private static int FirstMappedApiEndpointAfterTheGate()
    {
        var index = Program.IndexOf("app.MapGet(\"/api/", StringComparison.Ordinal);
        Assert.True(index >= 0, "Program.cs maps no /api endpoints — this test is looking at the wrong file.");
        return index;
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
