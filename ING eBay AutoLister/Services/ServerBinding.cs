namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the web server actually bound, checked against the one address eBay's sign-in relay will
/// ever send a seller back to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppPaths.Port"/> is a promise made to something outside this process: the relay on
/// inglisting.com redirects to <c>http://localhost:9332</c> and has no idea what this app bound.
/// Asking for that port is not the same as getting it — <c>ASPNETCORE_URLS</c>, a launchSettings
/// profile, or a hosting change can move the binding, and the app comes up looking perfectly
/// healthy on the wrong address. Everything works except the one thing that cannot be retried
/// locally: the browser comes back from eBay to a port nothing is listening on, and the seller sees
/// a dead tab with no clue why.
/// </para>
/// <para>
/// So the binding is asserted after the server starts, from the addresses the server itself
/// reports, and a mismatch stops the app with a message that names the fix. The check is a pure
/// function so <c>ServerBindingTests</c> can pin it without a socket.
/// </para>
/// </remarks>
public sealed class ServerBinding
{
    private readonly object _sync = new();
    private IReadOnlyList<string> _addresses = [];
    private string? _problem;
    private bool _verified;

    /// <summary>Addresses the server reported, once <see cref="Record"/> has run.</summary>
    public IReadOnlyList<string> Addresses { get { lock (_sync) return _addresses; } }

    /// <summary>Null when the binding is right; otherwise the sentence to show the seller.</summary>
    public string? Problem { get { lock (_sync) return _problem; } }

    /// <summary>False until the server has started and been asked what it bound.</summary>
    public bool Verified { get { lock (_sync) return _verified; } }

    /// <summary>True only when the check has actually run and found the expected port.</summary>
    public bool IsCorrect { get { lock (_sync) return _verified && _problem is null; } }

    public void Record(IEnumerable<string>? addresses, int expectedPort)
    {
        var list = (addresses ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var problem = Check(list, expectedPort);
        lock (_sync)
        {
            _addresses = list;
            _problem = problem;
            _verified = true;
        }
    }

    /// <summary>
    /// Returns null when at least one bound address is on <paramref name="expectedPort"/>, and
    /// otherwise the message to fail with. Never "binding error" — every branch says what is wrong
    /// and what to do about it, because the symptom the seller reports is "the eBay login page just
    /// goes nowhere", which points at eBay rather than at a port.
    /// </summary>
    public static string? Check(IReadOnlyList<string> addresses, int expectedPort)
    {
        const string why =
            "eBay's sign-in relay only ever sends you back to http://localhost:";

        if (addresses.Count == 0)
            return $"The web server started but reported no address, so there is no way to confirm it is on port {expectedPort}. " +
                   $"{why}{expectedPort}, so a sign-in started now would come back to nothing. " +
                   "Close any other copy of ING AutoLister, then start the app again.";

        if (addresses.Any(a => PortOf(a) == expectedPort)) return null;

        var bound = string.Join(", ", addresses);
        return $"ING AutoLister is serving on {bound} instead of port {expectedPort}. " +
               $"{why}{expectedPort}, so eBay sign-in cannot finish on this address. " +
               "Clear any ASPNETCORE_URLS or urls setting for this app (and the launchSettings.json profile if you " +
               "are starting it from Visual Studio), then start the app again.";
    }

    /// <summary>
    /// The port in a listen address. Kestrel reports things like <c>http://localhost:9332</c> and
    /// <c>http://[::]:9332</c>; a bare <c>http://localhost</c> means port 80, which is exactly the
    /// silent mismatch this class exists to catch.
    /// </summary>
    public static int? PortOf(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
            return uri.Port;
        if (uri is not null && uri.Scheme is "http" or "https") return uri.Port;

        // "*:9332" and "localhost:9332" are not absolute URIs but do turn up in urls settings.
        var colon = address.LastIndexOf(':');
        return colon >= 0 && int.TryParse(address[(colon + 1)..].TrimEnd('/'), out var p) ? p : null;
    }
}
