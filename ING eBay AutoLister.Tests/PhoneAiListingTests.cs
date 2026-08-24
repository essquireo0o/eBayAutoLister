using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Writing the listing on the phone, standing over the item.
/// </summary>
/// <remarks>
/// <para>
/// The seller has the thing in their hand at the moment they photograph it. That is when they can
/// answer what a photograph cannot — whether it powers on, what is missing from the box — and it is
/// the moment they are least able to walk to a computer. So the phone gets the same call the desktop
/// makes: title, category, condition and a starting price, from the shot just sent.
/// </para>
/// <para>
/// Measured end to end on a real photograph before this was written: 64 seconds, "TP-Link Deco BE23
/// BE3600 Wi-Fi 7 Whole Home Mesh Router", used &mdash; excellent, $54.99.
/// </para>
/// </remarks>
public class PhoneAiListingTests : IDisposable
{
    private static readonly string Source = ReadSource(Path.Combine("Services", "PhoneCapture.cs"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-phone-ai", Guid.NewGuid().ToString("N"));

    private PhotoLibrary Library()
    {
        Directory.CreateDirectory(_root);
        return new PhotoLibrary(new Env(_root));
    }

    // ── The photograph is read from the library, not sent up again ───────────────────────────

    [Fact]
    public async Task A_saved_photo_can_be_read_back_by_the_url_the_library_handed_out()
    {
        // The whole reason the phone does not re-upload: two megabytes back over a phone's uplink,
        // to analyse what the computer is already holding, is the slowest way to ask.
        var library = Library();
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);

        var url = await library.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, bytes, "jpg");

        Assert.Equal(bytes, library.ReadPhoto(url));
    }

    [Theory]
    [InlineData("/photos/photo-box/../../credentials.json")]
    [InlineData("/photos/../../../windows/win.ini")]
    [InlineData("/photos/photo-box/notes.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_outside_the_library_is_readable(string? url)
    {
        // A url is a string off a request, and "read me any file you like" is the shape of that
        // mistake. Sanitized to bare names exactly as DeletePhoto is.
        Assert.Null(Library().ReadPhoto(url));
    }

    [Fact]
    public void A_photo_that_is_not_there_reads_as_nothing_rather_than_throwing()
    {
        Assert.Null(Library().ReadPhoto("/photos/photo-box/deadbeefdeadbeefdeadbeefdeadbeef.jpg"));
    }

    // ── The endpoint ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_listing_is_written_behind_the_same_token_as_everything_else_on_the_phone()
    {
        Assert.Contains("web.MapPost(\"/c/{token}/listing\"", Source, StringComparison.Ordinal);
        // The pairing secret gates it, checked in constant time, exactly like the photo handler.
        Assert.Contains("if (!Ok(token)) return Results.NotFound();", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void It_reads_the_photo_off_disk_rather_than_asking_the_phone_for_it_again()
    {
        Assert.Contains("photos.ReadPhoto(url)", Source, StringComparison.Ordinal);
        Assert.Contains("claude.AnalyzeImageAsync(Convert.ToBase64String(bytes)", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_photo_named_it_uses_the_one_just_sent()
    {
        // The phone posts the url it got back; a phone that lost it mid-session should still work.
        Assert.Contains("if (url.Length == 0) url = _shots.Count > 0 ? _shots[^1] : \"\";",
                        Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_listing_never_takes_the_photograph_with_it()
    {
        // The photo is already saved by the time this runs. Losing the picture because the AI had a
        // bad minute would be the one unrecoverable outcome here.
        Assert.Contains("The photo is saved on your computer.", Source, StringComparison.Ordinal);
        Assert.Contains("log.Add(\"Warning\", \"Phone listing failed\"", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_phone_reads_words_rather_than_eBays_condition_codes()
    {
        // "USED_EXCELLENT" is for eBay. Somebody holding the item reads "Used — excellent".
        Assert.Contains("private static string ConditionWords(string? code)", Source, StringComparison.Ordinal);
        Assert.Contains("\"FOR_PARTS_OR_NOT_WORKING\"", Source, StringComparison.Ordinal);
        Assert.Contains("condition  = ConditionWords(listing.Condition),", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_offers_it_only_once_there_is_a_photograph_to_write_from()
    {
        var page = Between(Source, "private string CameraFreePageHtml()", "private string TrustPageHtml");

        Assert.Contains("id=\"write\"", page, StringComparison.Ordinal);
        Assert.Contains("hidden", page, StringComparison.Ordinal);
        // Revealed by a successful send, not on load.
        Assert.Contains("write.hidden = false;", page, StringComparison.Ordinal);
        // And it says the wait is a wait, because a button that looks stuck gets pressed twice.
        Assert.Contains("about a minute", page, StringComparison.Ordinal);
    }

    [Fact]
    public void What_comes_back_is_what_a_person_can_check_standing_up()
    {
        var page = Between(Source, "private string CameraFreePageHtml()", "private string TrustPageHtml");

        foreach (var field in new[] { "Condition", "Price", "Category", "Brand" })
            Assert.Contains($"row('{field}'", page, StringComparison.Ordinal);

        // And where the rest of it is. The phone shows the summary; the draft lives on the computer.
        Assert.Contains("Saved as a draft on your computer", page, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"could not find \"{start}\"");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"could not find \"{end}\" after \"{start}\"");
        return source[from..to];
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", relative));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

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
