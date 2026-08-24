using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The fixed data home and the one-time pull-forward from the old per-build folders.
/// </summary>
/// <remarks>
/// The bug behind all of this: the data folder used to be the exe's own directory, so every build
/// output, copy and install was a separate set of credentials and saved marketplace sessions. The
/// seller's key was never lost — a different build was reading a different folder. These tests hold
/// the two properties that make that impossible to happen again: the home does not depend on where
/// the exe sits, and bringing old data forward can never overwrite what the home already has.
/// </remarks>
public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"apppaths_{Guid.NewGuid():N}");

    private string Dir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string dir, string relativePath, string content)
    {
        var full = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── The home itself ──────────────────────────────────────────────────────

    [Fact]
    public void Interactive_home_is_the_named_folder_under_local_appdata()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ING AutoLister");

        Assert.Equal(expected, AppPaths.Resolve(isWindowsService: false));
    }

    [Fact]
    public void Service_home_is_program_data_because_local_system_has_no_usable_profile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ING AutoLister");

        Assert.Equal(expected, AppPaths.Resolve(isWindowsService: true));
    }

    [Fact]
    public void Home_does_not_depend_on_the_working_directory()
    {
        // The whole point: a build in bin\Debug, a copy on the desktop and an installed exe all
        // resolve to the same folder, so none of them can look like it has "lost" the API key.
        var before = Directory.GetCurrentDirectory();
        var first  = AppPaths.Resolve(isWindowsService: false);
        try
        {
            Directory.SetCurrentDirectory(Dir("somewhere-else"));
            Assert.Equal(first, AppPaths.Resolve(isWindowsService: false));
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }

    [Fact]
    public void Port_is_the_one_the_ebay_oauth_relay_redirects_to()
    {
        Assert.Equal(9332, AppPaths.Port);
        Assert.Equal("http://localhost:9332", AppPaths.BaseUrl);
    }

    // ── Migration ────────────────────────────────────────────────────────────

    [Fact]
    public void Brings_forward_credentials_and_saved_sessions_a_build_folder_still_holds()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, "credentials.json",      "{\"AnthropicApiKey\":\"sk-live\"}");
        WriteFile(legacy, "terapeak-session.json", "terapeak");
        WriteFile(legacy, "facebook-session.json", "facebook");

        var migrated = AppPaths.Migrate(home, [legacy]);

        Assert.Equal("{\"AnthropicApiKey\":\"sk-live\"}", File.ReadAllText(Path.Combine(home, "credentials.json")));
        Assert.Equal("terapeak", File.ReadAllText(Path.Combine(home, "terapeak-session.json")));
        Assert.Equal("facebook", File.ReadAllText(Path.Combine(home, "facebook-session.json")));
        Assert.Contains("credentials.json", migrated);
    }

    [Fact]
    public void Never_overwrites_what_the_home_already_has()
    {
        // A stale build's credentials.json must not be able to stamp on the live one — this is the
        // difference between a safe startup step and one that can destroy a seller's connections.
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(home,   "credentials.json", "current");
        WriteFile(legacy, "credentials.json", "stale");

        AppPaths.Migrate(home, [legacy]);

        Assert.Equal("current", File.ReadAllText(Path.Combine(home, "credentials.json")));
    }

    [Fact]
    public void Copies_rather_than_moves_so_the_legacy_folder_still_has_its_data()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, "credentials.json", "keys");

        AppPaths.Migrate(home, [legacy]);

        Assert.True(File.Exists(Path.Combine(legacy, "credentials.json")));
    }

    [Fact]
    public void Brings_forward_the_database_and_photo_folders_including_nested_ones()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, Path.Combine("App_Data", "ing_listing_engine.db"), "db");
        WriteFile(legacy, Path.Combine("App_Data", "analytics.json"),        "stats");
        WriteFile(legacy, Path.Combine("generated-photos", "a.jpg"),         "photo");
        WriteFile(legacy, Path.Combine("photos", "S19_95TH", "front.jpg"),   "model photo");

        AppPaths.Migrate(home, [legacy]);

        Assert.Equal("db",          File.ReadAllText(Path.Combine(home, "App_Data", "ing_listing_engine.db")));
        Assert.Equal("stats",       File.ReadAllText(Path.Combine(home, "App_Data", "analytics.json")));
        Assert.Equal("photo",       File.ReadAllText(Path.Combine(home, "generated-photos", "a.jpg")));
        Assert.Equal("model photo", File.ReadAllText(Path.Combine(home, "photos", "S19_95TH", "front.jpg")));
    }

    // Migration runs on EVERY startup, so mirroring the legacy tree's shape rather than its contents
    // is not a migration, it is a resurrection. This is why the Photo Library kept showing four
    // empty model folders after they were deleted: PhotoLibrary swept them, and the next restart
    // copied the empty shells back out of a bin\Debug\...\photos an old build had left behind.
    [Fact]
    public void An_empty_legacy_folder_is_not_recreated_in_the_home()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "photos", "L7"));
        Directory.CreateDirectory(Path.Combine(legacy, "photos", "S19_95TH"));

        AppPaths.Migrate(home, [legacy]);

        Assert.False(Directory.Exists(Path.Combine(home, "photos", "L7")));
        Assert.False(Directory.Exists(Path.Combine(home, "photos", "S19_95TH")));
    }

    // ...and a folder the seller deleted stays deleted, however many times the app restarts.
    [Fact]
    public void A_folder_deleted_from_the_home_does_not_come_back_on_the_next_startup()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, Path.Combine("photos", "L7", "front.jpg"), "model photo");

        AppPaths.Migrate(home, [legacy]);                                  // first run: brings it over
        Assert.True(Directory.Exists(Path.Combine(home, "photos", "L7")));

        Directory.Delete(Path.Combine(home, "photos", "L7"), recursive: true);
        AppPaths.Migrate(home, [legacy]);                                  // second run

        // The photograph is legitimately restored - it is a file the home no longer has, which is
        // exactly what this migration is for. What must not happen is an EMPTY shell reappearing.
        Assert.True(File.Exists(Path.Combine(home, "photos", "L7", "front.jpg")));
    }

    // The nested empty case: a legacy tree that holds one real photo and three empty siblings
    // brings the photo and leaves the siblings behind.
    [Fact]
    public void Only_the_folders_that_hold_something_are_brought_forward()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, Path.Combine("photos", "photo-box", "shot.jpg"), "real");
        foreach (var empty in new[] { "L7", "S19_95TH", "S19j_Pro" })
            Directory.CreateDirectory(Path.Combine(legacy, "photos", empty));

        AppPaths.Migrate(home, [legacy]);

        Assert.True(File.Exists(Path.Combine(home, "photos", "photo-box", "shot.jpg")));
        Assert.Equal(["photo-box"], Directory.GetDirectories(Path.Combine(home, "photos"))
            .Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Merges_a_folder_without_touching_files_the_home_already_has()
    {
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(home,   Path.Combine("App_Data", "ing_listing_engine.db"), "live db");
        WriteFile(legacy, Path.Combine("App_Data", "ing_listing_engine.db"), "old db");
        WriteFile(legacy, Path.Combine("App_Data", "gem-radar.json"),        "radar");

        AppPaths.Migrate(home, [legacy]);

        Assert.Equal("live db", File.ReadAllText(Path.Combine(home, "App_Data", "ing_listing_engine.db")));
        Assert.Equal("radar",   File.ReadAllText(Path.Combine(home, "App_Data", "gem-radar.json")));
    }

    [Fact]
    public void Second_run_finds_nothing_left_to_do()
    {
        // Migration runs on every startup, so "already migrated" has to be silent and free.
        var home   = Dir("home");
        var legacy = Dir("legacy");
        WriteFile(legacy, "credentials.json", "keys");
        WriteFile(legacy, Path.Combine("App_Data", "ing_listing_engine.db"), "db");

        Assert.NotEmpty(AppPaths.Migrate(home, [legacy]));
        Assert.Empty(AppPaths.Migrate(home, [legacy]));
    }

    [Fact]
    public void The_first_legacy_folder_listed_wins()
    {
        // The exe's own folder is offered before the service's, so a seller running the app
        // themselves picks up their own build's data rather than the background service's.
        var home    = Dir("home");
        var nearest = Dir("nearest");
        var further = Dir("further");
        WriteFile(nearest, "credentials.json", "nearest");
        WriteFile(further, "credentials.json", "further");

        AppPaths.Migrate(home, [nearest, further]);

        Assert.Equal("nearest", File.ReadAllText(Path.Combine(home, "credentials.json")));
    }

    [Fact]
    public void Skips_a_legacy_folder_that_is_the_home_itself()
    {
        // True for the Windows service, whose data home and "legacy service folder" are one path.
        var home = Dir("home");
        WriteFile(home, "credentials.json", "keys");

        var migrated = AppPaths.Migrate(home, [home, home + Path.DirectorySeparatorChar, home.ToUpperInvariant()]);

        Assert.Empty(migrated);
        Assert.Equal("keys", File.ReadAllText(Path.Combine(home, "credentials.json")));
    }

    [Fact]
    public void Ignores_legacy_folders_that_are_not_there()
    {
        var home = Dir("home");

        var migrated = AppPaths.Migrate(home, [Path.Combine(_root, "gone"), "", "   "]);

        Assert.Empty(migrated);
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void Creates_the_home_when_it_is_a_first_run()
    {
        var home = Path.Combine(_root, "brand-new");

        AppPaths.Migrate(home, []);

        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void SamePath_ignores_case_and_a_trailing_separator()
    {
        var home = Dir("home");

        Assert.True(AppPaths.SamePath(home, home + Path.DirectorySeparatorChar));
        Assert.True(AppPaths.SamePath(home, home.ToUpperInvariant()));
        Assert.False(AppPaths.SamePath(home, Dir("other")));
    }
}
