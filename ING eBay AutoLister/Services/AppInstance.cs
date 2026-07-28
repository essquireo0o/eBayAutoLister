using System.Net.NetworkInformation;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>Who is holding <see cref="AppPaths.Port"/> right now.</summary>
public enum PortOwner
{
    /// <summary>Nothing is listening — this process can bind it.</summary>
    Free,

    /// <summary>Another copy of this app is already serving. Open the browser at it, don't start a second server.</summary>
    ThisApp,

    /// <summary>Some other program owns the port. Say so and stop; do not go looking for a different one.</summary>
    Foreign,
}

/// <summary>
/// Answers "is ING AutoLister already running?" before anything tries to bind a port.
/// </summary>
/// <remarks>
/// <para>
/// The tempting behaviour when a port is busy is to pick another one. For this app that is the
/// worst available option: the eBay OAuth relay redirects to <c>localhost:9332</c>, so an app that
/// quietly moved to some other port is an app whose eBay login can never finish, and the seller has
/// no way to tell that from a broken relay. So there are exactly three outcomes here and none of
/// them is a different port — bind it, focus the copy that already has it, or stop and say why.
/// </para>
/// <para>
/// Identification is deliberately two-step. A listening socket says nothing about who owns it, so a
/// second copy of this app and an unrelated program that happened to take 9332 look identical until
/// something asks. <c>/api/app/instance</c> is the ask.
/// </para>
/// </remarks>
public static class AppInstance
{
    /// <summary>Value of the <c>app</c> field in the identity response. Anything else is not us.</summary>
    public const string IdentityMarker = "ING AutoLister";

    /// <summary>Identity endpoint. Answers before setup is complete, so it works on a fresh install.</summary>
    public const string IdentityPath = "/api/app/instance";

    /// <summary>
    /// Fallback probe for builds installed before the identity endpoint existed. A 200 from the
    /// app's own setup endpoint is weak evidence on its own, which is why it is only consulted
    /// after <see cref="IdentityPath"/> has failed to answer.
    /// </summary>
    public const string LegacyProbePath = "/api/setup/status";

    /// <summary>True when something already holds the given TCP port on this machine.</summary>
    public static bool IsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == port);
        }
        catch
        {
            // No listener table (locked-down box, unusual network stack). Fall back to "free" and
            // let the bind itself be the test — Program handles a failed bind the same clean way.
            return false;
        }
    }

    /// <summary>
    /// Works out who owns the port. <paramref name="probe"/> performs one GET against the given
    /// path and returns the body on success, or null on any failure/non-success status.
    /// </summary>
    public static async Task<PortOwner> DetectAsync(bool portListening, Func<string, Task<string?>> probe)
    {
        if (!portListening) return PortOwner.Free;

        if (IsOurIdentity(await probe(IdentityPath))) return PortOwner.ThisApp;
        if (await probe(LegacyProbePath) is not null) return PortOwner.ThisApp;

        return PortOwner.Foreign;
    }

    /// <summary>True when a response body is this app's identity document.</summary>
    public static bool IsOurIdentity(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("app", out var app)
                && app.ValueKind == JsonValueKind.String
                && app.GetString() == IdentityMarker;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// What to tell the seller when something else owns the port. Names the port and the fact that
    /// moving is not an option, because "port in use" on its own reads like something the app
    /// should have worked around.
    /// </summary>
    public static string ForeignPortMessage(int port) =>
        $"Port {port} is being used by another program.\r\n\r\n" +
        $"ING AutoLister only ever runs on http://localhost:{port} — eBay sign-in redirects there, " +
        "so it can't move to a different port.\r\n\r\n" +
        "Close whatever is using that port and start ING AutoLister again.";
}
