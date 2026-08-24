using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The owner's dashboard is reached with a key in a query string, not with a session cookie —
/// there is no owner ACCOUNT to sign in as. The hosted build is closed by default (HostedAuth's
/// FallbackPolicy requires an authenticated user on every endpoint that does not opt out), so
/// without an explicit AllowAnonymous the policy answers before AdminKeyMatches is ever reached.
/// </summary>
/// <remarks>
/// Found live on 2026-08-24: <c>/api/owner/stats</c> answered 401 to the CORRECT key from
/// app.inglisting.com, and <c>/owner</c> served the marketing page. Both are asserted from source
/// because Program.cs is top-level statements — the mini-apps in HostedAuthTests wire the policy up
/// around stand-in endpoints, and a stand-in cannot notice that a real route forgot to opt out.
/// CalibrationEndpoints already carries the same opt-out for the same reason.
/// </remarks>
public class OwnerDashboardReachableTests
{
    private static readonly string Program = ReadSource("ING eBay AutoLister/Program.cs");

    [Theory]
    [InlineData("/api/owner/stats", "the dashboard's data")]
    [InlineData("/owner", "the dashboard page itself")]
    public void The_owner_endpoints_open_on_the_key_alone_rather_than_on_a_session(string route, string what)
    {
        var body = HandlerFor(route);

        Assert.True(body.Contains(".AllowAnonymous()", StringComparison.Ordinal),
            $"{route} ({what}) does not carry AllowAnonymous, so the hosted build's closed-by-default "
            + "policy refuses it before the admin key is read — the owner cannot open their own dashboard.");

        // The key is still the credential, and it is still checked before anything is returned.
        Assert.True(body.Contains("AdminKeyMatches", StringComparison.Ordinal),
            $"{route} is anonymous and no longer checks the admin key. That is not a dashboard, it is a leak.");

        // Anonymous means the per-IP budget is the only thing between the key and a guessing loop.
        Assert.True(body.Contains(".RateLimitLikeAuth()", StringComparison.Ordinal),
            $"{route} is anonymous without a rate limit, so a 128-bit key can be tried as fast as the network allows.");
    }

    /// <summary>The mapping for one route, from its <c>app.MapGet</c> to the <c>});</c> that ends it.</summary>
    private static string HandlerFor(string route)
    {
        var start = Program.IndexOf($"app.MapGet(\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Program.cs no longer maps {route} at all.");

        // The first line that STARTS at column zero with "})" — the close of the MapGet call.
        // Every "});" inside the handler is indented, and the real one is not followed by ";"
        // at all once builder calls are chained onto it, which is the case being asserted.
        // The first line that STARTS at column zero with "})" — the close of the MapGet call.
        // Every "});" inside the handler is indented, and the real one is not followed by ";" at
        // all once builder calls are chained onto it, which is the case being asserted.
        var end = Program.IndexOf("\n})", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of the {route} handler.");

        // Past "});" to the end of that line, which is where the builder calls are chained.
        var lineEnd = Program.IndexOf('\n', end + 1);
        return Program[start..(lineEnd < 0 ? Program.Length : lineEnd)];
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
