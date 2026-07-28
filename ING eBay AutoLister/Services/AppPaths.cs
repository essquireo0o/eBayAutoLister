namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The one address this app answers on, and the one folder it keeps anything in.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of this class exist for the same reason: everything the seller connects — the
/// Anthropic key, the eBay refresh token, the saved Terapeak and Facebook browser sessions — is
/// only worth connecting once. Before this, both the port and the data folder moved on their own.
/// </para>
/// <para>
/// The folder used to be <c>ContentRootPath</c>, which was the exe's own directory for anything
/// that was not a Program Files install. That meant <c>bin\Debug</c>, a copied build, a portable
/// folder and an installed app were four different data homes, so a seller who ran a different
/// build found their API key gone and every marketplace disconnected — nothing was lost, they were
/// just looking at a different folder. <see cref="DataHome"/> is now fixed, and
/// <see cref="Migrate"/> pulls anything left behind in the old per-build locations into it once.
/// </para>
/// <para>
/// The port is fixed for a blunter reason: the eBay OAuth relay on inglisting.com redirects to
/// <c>localhost:9332</c> and nowhere else. A different port is not a smaller inconvenience than a
/// lost setting — it is a login that cannot complete. So there is no "find a free port" path here
/// and no environment override; if 9332 is taken the app says so and stops, which
/// <see cref="AppInstance"/> handles.
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>The only port this app ever binds. The eBay OAuth relay redirects here.</summary>
    public const int Port = 9332;

    /// <summary>The only URL this app is ever reached at.</summary>
    public const string BaseUrl = "http://localhost:9332";

    /// <summary>Folder name used under whichever profile root applies.</summary>
    public const string FolderName = "ING AutoLister";

    private static readonly Lazy<string> _dataHome =
        new(() => Resolve(Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()));

    /// <summary>
    /// The fixed data home for this process. Everything persisted lives under here regardless of
    /// where the exe was launched from.
    /// </summary>
    public static string DataHome => _dataHome.Value;

    /// <summary>
    /// Resolves the data home. Interactive runs — every run a seller ever starts — always land on
    /// <c>%LOCALAPPDATA%\ING AutoLister</c>.
    /// </summary>
    /// <param name="isWindowsService">
    /// True only when the SCM started the process. A service runs as LocalSystem, whose
    /// <c>%LOCALAPPDATA%</c> is a hidden system profile the seller can neither see nor put a file
    /// into, so the service uses <c>%ProgramData%\ING AutoLister</c> instead. That is the single
    /// exception to "one folder", and it is a different account rather than a different build.
    /// </param>
    public static string Resolve(bool isWindowsService) => Path.Combine(
        Environment.GetFolderPath(isWindowsService
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>Single files that carry connected-account state.</summary>
    public static readonly string[] StateFiles =
    [
        "credentials.json",         // Anthropic key, eBay tokens, Stripe, comps API
        "terapeak-session.json",    // saved eBay/Terapeak browser session
        "facebook-session.json",    // saved Facebook Marketplace browser session
    ];

    /// <summary>Folders that carry persisted state. App_Data holds the DB and work-recovery.</summary>
    public static readonly string[] StateFolders =
    [
        "App_Data",         // ing_listing_engine.db (listings, work-recovery, publish journal), analytics, caches
        "generated-photos", // photos the app made or fetched, referenced by live listing image URLs
        "photos",           // the seller's own per-model representative photo library
    ];

    /// <summary>
    /// Copies anything the old per-build locations still hold into <paramref name="dataHome"/>,
    /// and returns a short description of what moved (empty when there was nothing to do).
    /// </summary>
    /// <remarks>
    /// Copy, never move, and never overwrite: whatever is already in the fixed home is the truth,
    /// the legacy folder is only consulted for what the home does not have yet. That makes this
    /// safe to run on every startup — after the first run it finds nothing and does nothing — and
    /// means a mistake here cannot destroy a seller's only copy of anything.
    /// </remarks>
    public static IReadOnlyList<string> Migrate(string dataHome, IEnumerable<string> legacyRoots)
    {
        Directory.CreateDirectory(dataHome);
        var migrated = new List<string>();

        foreach (var root in legacyRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (SamePath(root, dataHome)) continue;

            foreach (var file in StateFiles)
            {
                var src = Path.Combine(root, file);
                var dst = Path.Combine(dataHome, file);
                if (!File.Exists(src) || File.Exists(dst)) continue;
                try
                {
                    File.Copy(src, dst);
                    migrated.Add(file);
                }
                catch
                {
                    // Locked or unreadable. The app still starts — the seller reconnects that one
                    // thing — which beats refusing to run over a file we were only being nice about.
                }
            }

            foreach (var folder in StateFolders)
            {
                var src = Path.Combine(root, folder);
                if (!Directory.Exists(src)) continue;
                var copied = CopyMissing(src, Path.Combine(dataHome, folder));
                if (copied > 0) migrated.Add($"{folder} ({copied} file{(copied == 1 ? "" : "s")})");
            }
        }

        return migrated;
    }

    /// <summary>Recursive copy of every file the destination does not already have. Returns the count.</summary>
    private static int CopyMissing(string sourceDir, string destDir)
    {
        var copied = 0;
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var src in Directory.GetFiles(sourceDir))
            {
                var dst = Path.Combine(destDir, Path.GetFileName(src));
                if (File.Exists(dst)) continue;
                try { File.Copy(src, dst); copied++; } catch { }
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
                copied += CopyMissing(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
        catch { }
        return copied;
    }

    /// <summary>True when two paths name the same folder, ignoring case and a trailing separator.</summary>
    public static bool SamePath(string a, string b)
    {
        static string Norm(string p) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
