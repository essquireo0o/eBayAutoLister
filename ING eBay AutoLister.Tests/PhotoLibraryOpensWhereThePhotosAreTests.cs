using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// "When I take a picture on my phone it does not transfer to the photo library" (owner,
/// 2026-08-23). It transferred every time. The screen opened on an empty folder.
/// </summary>
/// <remarks>
/// <para>
/// The Photo Library selected <c>plFolders[0]</c> when nothing was chosen — the first name
/// alphabetically. On this app's own seed folders that is <c>L7</c>, and L7 is empty. So the
/// seller shot a lot on their phone, opened the Photo Library to look at it, and read "No photos
/// in this model yet" while forty-nine of their photographs sat one folder down under
/// <c>photo-box</c>. Confirmed on disk before anything was changed: the files were there, correct
/// size, stamped with the minute they were taken.
/// </para>
/// <para>
/// A feature that works perfectly and cannot be seen is indistinguishable from one that is broken,
/// and the seller is right to report it as broken.
/// </para>
/// </remarks>
public sealed class PhotoLibraryOpensWhereThePhotosAreTests : IDisposable
{
    private readonly string _root;
    private readonly PhotoLibrary _library;

    public PhotoLibraryOpensWhereThePhotosAreTests()
    {
        // PhotoLibrary hangs its folders off the content root, so the throwaway root is handed
        // over the same way the app hands over its own.
        var content = Path.Combine(Path.GetTempPath(), $"photo_library_{Guid.NewGuid():N}");
        Directory.CreateDirectory(content);
        _root = Path.Combine(content, "photos");
        Directory.CreateDirectory(_root);
        _library = new PhotoLibrary(new ScratchEnvironment(content));
    }

    /// <summary>The one property PhotoLibrary reads, and a temp directory to point it at.</summary>
    private sealed class ScratchEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Testing";
    }

    private void Shoot(string folder, string name, DateTime takenUtc)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
        File.SetLastWriteTimeUtc(file, takenUtc);
    }

    [Fact]
    public void A_folder_reports_when_it_last_received_a_photograph()
    {
        var taken = new DateTime(2026, 8, 23, 12, 8, 16, DateTimeKind.Utc);
        Shoot("photo-box", "a.jpg", taken.AddMinutes(-3));
        Shoot("photo-box", "b.jpg", taken);

        var folder = _library.GetAllFolders().Single(f => f.ModelKey == "photo-box");

        Assert.Equal(2, folder.ImageCount);
        // The NEWEST, not the first or the last read off the directory — it is the answer to
        // "which folder was I just filling".
        Assert.Equal(taken, folder.NewestAtUtc);
    }

    [Fact]
    public void An_empty_folder_has_no_date_at_all()
    {
        Directory.CreateDirectory(Path.Combine(_root, "L7"));

        var folder = _library.GetAllFolders().Single(f => f.ModelKey == "L7");

        // Not DateTime.MinValue, which would sort like a real answer.
        Assert.Equal(0, folder.ImageCount);
        Assert.Null(folder.NewestAtUtc);
    }

    [Fact]
    public void The_folder_list_stays_alphabetical()
    {
        Shoot("photo-box", "a.jpg", DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(_root, "L7"));
        Directory.CreateDirectory(Path.Combine(_root, "S19_95TH"));

        var keys = _library.GetAllFolders().Select(f => f.ModelKey).ToList();

        // A list of names should read like one. Only the initial SELECTION follows the
        // photographs — reordering the list under the seller would be a different bug.
        Assert.Equal(keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), keys);
    }

    // ── The rule the screen applies, pinned in the file that implements it ───────────────────

    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void The_screen_no_longer_opens_on_whatever_sorts_first()
    {
        // The exact line that produced the report.
        Assert.DoesNotContain("plSelected = plFolders[0]?.modelKey || '';", Js, StringComparison.Ordinal);
        Assert.Contains("plSelected = plDefaultFolder();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void It_opens_on_the_folder_that_was_filled_most_recently()
    {
        Assert.Contains("function plDefaultFolder()", Js, StringComparison.Ordinal);
        Assert.Contains("const withPhotos = plFolders.filter(f => (f.imageCount || 0) > 0);",
                        Js, StringComparison.Ordinal);
        Assert.Contains("Date.parse(f.newestAtUtc) > Date.parse(best.newestAtUtc)", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_folder_is_opened_only_when_every_folder_is_empty()
    {
        // Then it is not a mistake — it is the truth, and the empty state is the right screen.
        Assert.Contains("if (!withPhotos.length) return plFolders[0]?.modelKey || '';",
                        Js, StringComparison.Ordinal);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
