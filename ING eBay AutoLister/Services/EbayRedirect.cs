namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The public address eBay sends a seller back to when they finish the consent screen.
/// </summary>
/// <remarks>
/// <para>
/// This is not the <c>redirect_uri</c> that goes on the wire. eBay's authorization endpoint takes a
/// <b>RuName</b> in that parameter — an opaque token — and the URL it stands for is registered in
/// eBay's developer console, where this process cannot see it and cannot change it. So the app has
/// never known where a sign-in comes back to; it has only ever assumed, and the assumption was
/// <c>http://localhost:9332</c> written into <see cref="AppPaths.BaseUrl"/>.
/// </para>
/// <para>
/// That assumption is true of the desktop build and false of a hosted one, and it is load-bearing in
/// three places that face a person: the log line that says where the sign-in will return, the
/// <c>/api/ebay/status</c> field the connection doctor reads, and the sentence that tells somebody
/// which URL to register against their RuName. A hosted deployment telling its owner to register
/// localhost is worse than saying nothing — it is a confident instruction that cannot work.
/// </para>
/// <para>
/// So the address is configuration, defaulting to exactly what the desktop build has always been
/// registered to. A desktop build that sets nothing names the relay on inglisting.com, word for
/// word as it did before this type existed, and nothing about that flow changes.
/// </para>
/// </remarks>
public sealed class EbayRedirect
{
    /// <summary>The endpoint eBay's authorization code arrives at directly. See Program.cs.</summary>
    public const string CallbackPath = "/api/ebay/callback";

    /// <summary>Configuration key. As an environment variable: <c>Ebay__RedirectUri</c>.</summary>
    public const string Setting = "Ebay:RedirectUri";

    /// <summary>The flat environment variable name, for hosts whose settings screen has no colons.</summary>
    public const string EnvironmentVariable = "EBAY_REDIRECT_URI";

    /// <summary>
    /// What the desktop build's RuName has always been registered to: the PHP relay on
    /// inglisting.com, which exchanges the code and then bounces the browser to
    /// <c>http://localhost:9332/api/ebay/finish</c>. Not this app's own callback path — the desktop
    /// build cannot be the registered URL, because <c>localhost</c> is not a place eBay can reach.
    /// </summary>
    public const string DesktopDefault = "https://inglisting.com" + CallbackPath;

    /// <summary>The desktop default as an instance, for the callers that have no configuration.</summary>
    public static EbayRedirect Desktop { get; } = new(DesktopDefault, false, null);

    private EbayRedirect(string uri, bool configured, string? problem)
    {
        Uri = uri;
        IsConfigured = configured;
        Problem = problem;
    }

    /// <summary>The absolute URL eBay returns the seller to.</summary>
    public string Uri { get; }

    /// <summary>True when a deployment set this deliberately rather than inheriting the default.</summary>
    public bool IsConfigured { get; }

    /// <summary>
    /// Set when a value was configured and could not be used, in which case <see cref="Uri"/> is the
    /// default. Reported rather than thrown: a malformed redirect must not stop a server from
    /// starting, because every other thing the app does still works and the seller can be told.
    /// </summary>
    public string? Problem { get; }

    public static EbayRedirect FromConfiguration(IConfiguration configuration)
    {
        var raw = (configuration[Setting] ?? configuration[EnvironmentVariable] ?? "").Trim();
        if (raw.Length == 0) return Desktop;

        if (!System.Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            return new(DesktopDefault, false,
                $"{Setting} is set to something that is not an absolute http(s) URL, so " +
                $"{DesktopDefault} is being used instead. Set it to the full URL eBay returns to, " +
                $"for example https://app.example.com{CallbackPath}.");
        }

        // Trailing slash removed so that a value pasted with one and a value pasted without one are
        // the same registration. eBay matches the accepted URL as a string.
        return new(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/'), true, null);
    }
}
