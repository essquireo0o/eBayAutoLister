using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The same photograph, saved twice, is one photograph.
/// </summary>
/// <remarks>
/// <para>
/// The owner's photo-box folder held 73 files and 97 MB, with the same enhanced shot in it five
/// times — four of those copies written inside a single second, and two others byte-identical
/// (md5 7972E991…) twenty-three seconds apart. Nothing was wrong with the photographs. Every save
/// took a fresh <c>Guid.NewGuid()</c>, so identical bytes could not do anything BUT become another
/// file, and no caller deduplicated: not AI Enhance, not cut-out, not portrait, not the editor,
/// not the phone upload.
/// </para>
/// <para>
/// Naming the file after its own content fixes all of them at once, and cannot be forgotten at a
/// new call site later — which a helper that call sites must remember to use certainly would be.
/// </para>
/// </remarks>
public class PhotoLibraryDedupeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-photo-dedupe", Guid.NewGuid().ToString("N"));

    private PhotoLibrary New()
    {
        Directory.CreateDirectory(_root);
        return new PhotoLibrary(new Env(_root));
    }

    private static byte[] Jpeg(byte seed, int length = 4096)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)((seed + i) % 251);
        return bytes;
    }

    private string[] FilesIn(string folder) =>
        Directory.Exists(Path.Combine(_root, "photos", folder))
            ? Directory.GetFiles(Path.Combine(_root, "photos", folder))
            : [];

    [Fact]
    public async Task The_same_bytes_saved_twice_are_one_file_and_one_url()
    {
        var library = New();
        var photo = Jpeg(7);

        var first  = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, photo, "jpg");
        var second = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, photo, "jpg");

        Assert.Equal(first, second);
        Assert.Single(FilesIn(PhotoLibrary.PhotoBoxFolder));
    }

    [Fact]
    public async Task Five_enhancements_of_one_photo_leave_one_enhanced_file()
    {
        // Exactly what happened: AI Enhance run again and again on the same shot, each run
        // producing a byte-identical result, each result landing under a new random name.
        var library = New();
        var enhanced = Jpeg(19);

        for (var i = 0; i < 5; i++) await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, enhanced, "jpg");

        Assert.Single(FilesIn(PhotoLibrary.PhotoBoxFolder));
    }

    [Fact]
    public async Task Different_photographs_are_still_different_files()
    {
        // The failure that would matter far more than the duplicates: two different shots
        // collapsing onto one name would destroy one of them.
        var library = New();

        var a = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, Jpeg(1), "jpg");
        var b = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, Jpeg(2), "jpg");

        Assert.NotEqual(a, b);
        Assert.Equal(2, FilesIn(PhotoLibrary.PhotoBoxFolder).Length);
    }

    [Fact]
    public async Task A_photo_that_differs_by_one_byte_is_a_different_photo()
    {
        var library = New();
        var original = Jpeg(3);
        var edited = Jpeg(3);
        edited[2048] ^= 0xFF;             // one pixel's worth of difference

        var a = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, original, "jpg");
        var b = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, edited, "jpg");

        Assert.NotEqual(a, b);
        Assert.Equal(2, FilesIn(PhotoLibrary.PhotoBoxFolder).Length);
    }

    [Fact]
    public async Task The_saved_file_still_holds_exactly_what_was_handed_over()
    {
        // Skipping the write when the name already exists must never mean skipping the write when
        // the file is NOT there — a returned url pointing at nothing would be worse than a duplicate.
        var library = New();
        var photo = Jpeg(11);

        var url = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, photo, "jpg");

        var path = Path.Combine(_root, "photos", PhotoLibrary.PhotoBoxFolder, Path.GetFileName(url));
        Assert.True(File.Exists(path), $"nothing was written for {url}");
        Assert.Equal(photo, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task The_same_photograph_in_two_models_is_two_files()
    {
        // Content addressing is per folder, because the folders are what the seller organises by:
        // the same photograph used for two models belongs to both of them.
        var library = New();
        var photo = Jpeg(23);

        var box   = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, photo, "jpg");
        var other = await library.SavePhotoAsync("antminer-s19", photo, "jpg");

        Assert.NotEqual(box, other);
        Assert.Single(FilesIn(PhotoLibrary.PhotoBoxFolder));
        Assert.Single(FilesIn("antminer-s19"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The least that <see cref="PhotoLibrary"/> needs: a content root under the temp dir.</summary>
    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(root);
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
