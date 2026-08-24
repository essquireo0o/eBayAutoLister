using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Getting a photograph out of the app, which is the step that was missing.
/// </summary>
/// <remarks>
/// The Photo Box shot, transferred, saved and enhanced for weeks, and the owner still could not get
/// a photo: every capture was a GUID under <c>%LOCALAPPDATA%</c> and no button anywhere produced a
/// file they could see. These cover the two things that make the copy trustworthy — that a burst
/// never overwrites itself, and that a path from the page cannot reach outside the photo folders.
/// </remarks>
public class PhotoExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-export-" + Guid.NewGuid().ToString("N"));
    private readonly string _desktop;
    private readonly PhotoExport _export;

    public PhotoExportTests()
    {
        _desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(Path.Combine(_root, "photos", PhotoLibrary.PhotoBoxFolder));
        Directory.CreateDirectory(Path.Combine(_root, "generated-photos"));
        Directory.CreateDirectory(_desktop);
        _export = new PhotoExport(new StubEnv(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class StubEnv(string root) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private string GivenPhoto(string name = "abc.jpg", string folder = PhotoLibrary.PhotoBoxFolder)
    {
        var path = Path.Combine(_root, "photos", folder, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        return $"/photos/{folder}/{name}";
    }

    /// <summary>
    /// Local, deliberately. The copy is named after the seller's own clock — a photograph taken at
    /// noon should be called noon — so a UTC instant here would name the file differently in every
    /// timezone the suite runs in, and only agree with itself in London.
    /// </summary>
    private static readonly DateTimeOffset Noon = new(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Local));

    // ── The name, which is half the fix ──────────────────────────────────────────────────────

    [Fact]
    public void A_saved_photo_is_named_after_when_it_was_taken_not_by_its_guid()
    {
        var saved = _export.Save(GivenPhoto("9f3c1e7b2a4d.jpg"), _desktop, Noon);

        Assert.StartsWith("ING Photo 2026-08-24", saved.FileName);
        Assert.EndsWith(".jpg", saved.FileName);
        Assert.DoesNotContain("9f3c1e7b2a4d", saved.FileName);
        Assert.True(File.Exists(saved.FullPath));
    }

    [Fact]
    public void The_library_keeps_its_copy_because_saving_is_a_copy_and_not_a_move()
    {
        var url = GivenPhoto();
        var source = Path.Combine(_root, "photos", PhotoLibrary.PhotoBoxFolder, "abc.jpg");

        _export.Save(url, _desktop, Noon);

        Assert.True(File.Exists(source), "the listing flow still reads the photo from the library");
    }

    [Fact]
    public void A_burst_taken_inside_one_second_lands_as_three_files_and_never_overwrites()
    {
        _export.Save(GivenPhoto("one.jpg"), _desktop, Noon);
        _export.Save(GivenPhoto("two.jpg"), _desktop, Noon);
        _export.Save(GivenPhoto("three.jpg"), _desktop, Noon);

        var files = Directory.GetFiles(_desktop);
        Assert.Equal(3, files.Length);
        Assert.Contains(files, f => Path.GetFileName(f) == "ING Photo 2026-08-24 12-00-00.jpg");
        Assert.Contains(files, f => Path.GetFileName(f) == "ING Photo 2026-08-24 12-00-00 (2).jpg");
        Assert.Contains(files, f => Path.GetFileName(f) == "ING Photo 2026-08-24 12-00-00 (3).jpg");
    }

    [Fact]
    public void Saving_the_same_photo_twice_is_harmless()
    {
        var url = GivenPhoto();
        var first = _export.Save(url, _desktop, Noon);
        var second = _export.Save(url, _desktop, Noon);

        Assert.NotEqual(first.FileName, second.FileName);
        Assert.True(File.Exists(first.FullPath));
        Assert.True(File.Exists(second.FullPath));
    }

    // ── What it must refuse ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/photos/photo-box/../../credentials.json")]
    [InlineData("/photos/../credentials.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/config/SAM")]
    [InlineData("https://example.com/photo.jpg")]
    [InlineData("")]
    public void A_path_from_the_page_cannot_reach_outside_the_photo_folders(string url)
    {
        // This copies whatever it is handed to a folder the seller opens, so a refusal is the only
        // acceptable answer for anything that is not one of this app's own photographs.
        File.WriteAllText(Path.Combine(_root, "credentials.json"), "{\"secret\":1}");

        Assert.ThrowsAny<Exception>(() => _export.Save(url, _desktop, Noon));
        Assert.Empty(Directory.GetFiles(_desktop));
    }

    [Fact]
    public void A_photo_that_has_been_deleted_since_the_page_loaded_says_so()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => _export.Save("/photos/photo-box/gone.jpg", _desktop, Noon));

        Assert.Contains("no longer in the library", ex.Message);
    }

    // ── The enhanced copy is saveable too, which is the one people actually want ─────────────

    [Fact]
    public void An_enhanced_photo_saves_from_its_own_folder()
    {
        File.WriteAllBytes(Path.Combine(_root, "generated-photos", "studio.jpg"), [0xFF, 0xD8, 1, 2, 3]);

        var saved = _export.Save("/generated-photos/studio.jpg", _desktop, Noon);

        Assert.True(File.Exists(saved.FullPath));
        Assert.StartsWith("ING Photo", saved.FileName);
    }

    // ── The counting itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public void UniqueName_counts_up_from_two_and_leaves_a_free_name_alone()
    {
        Assert.Equal("Shot.jpg", PhotoExport.UniqueName(_desktop, "Shot", ".jpg"));

        File.WriteAllText(Path.Combine(_desktop, "Shot.jpg"), "x");
        Assert.Equal("Shot (2).jpg", PhotoExport.UniqueName(_desktop, "Shot", "jpg"));
    }
}
