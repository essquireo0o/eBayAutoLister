using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Deciding who holds port 9332 before anything binds it.
/// </summary>
/// <remarks>
/// There is no outcome here that starts the app on a different port. The eBay OAuth relay redirects
/// to <c>localhost:9332</c>, so an instance that quietly moved is one whose eBay sign-in can never
/// complete — a failure the seller would read as "eBay is broken". These tests pin the three
/// answers: bind it, focus the copy that already has it, or stop.
/// </remarks>
public class AppInstanceTests
{
    private static string Identity(string app = AppInstance.IdentityMarker) =>
        $$"""{"app":"{{app}}","port":9332,"pid":1234}""";

    /// <summary>A probe that answers the listed paths and returns null (failed/non-200) for the rest.</summary>
    private static Func<string, Task<string?>> Serving(params (string Path, string Body)[] responses) =>
        path => Task.FromResult(
            responses.FirstOrDefault(r => r.Path == path) is { Body: not null } hit ? hit.Body : null);

    [Fact]
    public async Task Free_when_nothing_is_listening()
    {
        var probed = false;
        var owner  = await AppInstance.DetectAsync(portListening: false, probe: _ =>
        {
            probed = true;
            return Task.FromResult<string?>(null);
        });

        Assert.Equal(PortOwner.Free, owner);
        Assert.False(probed); // no socket, nothing to ask
    }

    [Fact]
    public async Task This_app_when_the_identity_endpoint_answers_with_our_marker()
    {
        var owner = await AppInstance.DetectAsync(
            portListening: true,
            probe: Serving((AppInstance.IdentityPath, Identity())));

        Assert.Equal(PortOwner.ThisApp, owner);
    }

    [Fact]
    public async Task This_app_when_an_older_build_only_answers_the_legacy_probe()
    {
        // Installed builds from before the identity endpoint existed still have to be recognised —
        // otherwise upgrading turns "already running" into "another program has your port".
        var owner = await AppInstance.DetectAsync(
            portListening: true,
            probe: Serving((AppInstance.LegacyProbePath, "{\"configured\":true}")));

        Assert.Equal(PortOwner.ThisApp, owner);
    }

    [Fact]
    public async Task Foreign_when_something_is_listening_but_nothing_answers()
    {
        var owner = await AppInstance.DetectAsync(portListening: true, probe: Serving());

        Assert.Equal(PortOwner.Foreign, owner);
    }

    [Fact]
    public async Task Foreign_when_another_program_answers_both_paths_with_its_own_json()
    {
        var owner = await AppInstance.DetectAsync(
            portListening: true,
            probe: Serving(
                (AppInstance.IdentityPath, """{"app":"Some Other Dev Server","port":9332}""")));

        Assert.Equal(PortOwner.Foreign, owner);
    }

    [Fact]
    public async Task Identity_is_asked_before_the_legacy_probe()
    {
        var asked = new List<string>();
        await AppInstance.DetectAsync(portListening: true, probe: path =>
        {
            asked.Add(path);
            return Task.FromResult<string?>(path == AppInstance.IdentityPath ? Identity() : "ok");
        });

        Assert.Equal([AppInstance.IdentityPath], asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>It works!</html>")]          // some other web server's landing page
    [InlineData("[]")]                              // valid JSON, wrong shape
    [InlineData("""{"app":123}""")]                 // right key, wrong type
    [InlineData("""{"app":"ing autolister"}""")]    // near miss is still not us
    [InlineData("""{"port":9332}""")]
    public void Not_our_identity(string? body) => Assert.False(AppInstance.IsOurIdentity(body));

    [Fact]
    public void Our_identity_survives_extra_fields()
    {
        Assert.True(AppInstance.IsOurIdentity(
            """{"app":"ING AutoLister","port":9332,"url":"http://localhost:9332","pid":7,"dataHome":"C:\\x","version":"1.0.0.0"}"""));
    }

    [Fact]
    public void Foreign_message_names_the_port_and_says_moving_is_not_an_option()
    {
        var message = AppInstance.ForeignPortMessage(AppPaths.Port);

        Assert.Contains("9332", message);
        Assert.Contains("eBay", message);
    }

    [Fact]
    public void Port_is_not_reported_as_listening_when_it_is_free()
    {
        // Port 0 is never a listening socket, so this exercises the real listener-table lookup
        // without depending on what happens to be running on the machine.
        Assert.False(AppInstance.IsPortListening(0));
    }
}
