namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot screen stopped offering the one thing it could never do.
/// </summary>
/// <remarks>
/// <para>
/// 📡 Read the show fetched the show's public page through the app and read the lot off it.
/// Whatnot answers that with <b>HTTP 403</b> — it does it to anything that is not a signed-in
/// browser — so the biggest, gold, primary button on the screen apologised every time it was
/// pressed. The show-address box, "From the panel", "Keep reading" and "Check the photo" existed
/// only to feed that read or to use what it brought back, so all four were dead with it.
/// </para>
/// <para>
/// The hosted build had already hidden the lot of them for exactly this reason — style.css still
/// says "four buttons that could only apologise" — but the reason was never hosted-specific, and
/// the desktop build kept them. The owner put it plainly: "Read the show does not work".
/// </para>
/// <para>
/// What replaces it was already on the screen: share the tab you are already watching the show on,
/// and the app reads the picture and prices it. That is the only path that does not have to ask
/// Whatnot for anything.
/// </para>
/// <para>
/// The server endpoints (<c>/api/whatsnot/read</c>, <c>/api/whatsnot/photo</c>) are deliberately
/// still there. Nothing is wrong with that code, and Whatnot may open the door again; what was
/// removed is offering it as the way in.
/// </para>
/// </remarks>
public class WhatsNotWatchNotReadTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    [Theory]
    [InlineData("wn-read-btn")]      // 📡 Read the show — 403, every time
    [InlineData("wn-read-url")]      // the address box that fed it
    [InlineData("wn-read-here")]     // ↧ From the panel — filled that box
    [InlineData("wn-photo-btn")]     // 🔍 Check the photo — needed a photo only the read returned
    [InlineData("wn-blocked-read")]  // 📡 Read this show instead — the same 403, offered again
    public void The_controls_that_could_only_apologise_are_gone(string id)
    {
        Assert.DoesNotContain($"id=\"{id}\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void And_nothing_is_still_wired_to_them()
    {
        // A handler on an element that does not exist is not harmless: it is the next reader's
        // evidence that the feature is still there.
        Assert.DoesNotContain("on('wn-read-btn'", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("on('wn-read-here'", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("on('wn-photo-btn'", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("on('wn-blocked-read'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_that_works_is_now_the_one_that_leads()
    {
        // It was a ghost button beside a primary that could not work. Now it is the primary.
        Assert.Contains("id=\"wn-video-btn\" class=\"btn btn-primary wn-video-btn\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_label_says_all_three_things_it_does()
    {
        // "Watch & listen" was true and incomplete — pricing is the only reason anyone presses it.
        Assert.Contains("Watch, listen &amp; price", Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Watch &amp; listen (AI)", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_promise_an_ear_this_install_has_not_got()
    {
        // Claude takes no audio, so hearing the host needs an OpenAI key for transcription. Without
        // one the button offers the two things it can do rather than three things it cannot.
        Assert.Contains("wnCanListen ? '🎥 Watch, listen & price' : '🎥 Watch & price'", Js, StringComparison.Ordinal);
        Assert.Contains("wnCanListen = !!f.hasOpenAiKey;", Js, StringComparison.Ordinal);
        Assert.Contains("Add an OpenAI key in Settings → AI Provider and it hears the host as well.",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prose_underneath_names_whichever_button_is_actually_there()
    {
        // The paragraph tells the seller which button to press. It used to carry its own copy of
        // the label, so on a machine with no speech-to-text key it named a button that was not on
        // the screen. It is filled from the same variable the button is.
        Assert.Contains("id=\"wn-video-name\"", Html, StringComparison.Ordinal);
        Assert.Contains("if (named) named.textContent = label;", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resting_label_is_written_in_exactly_one_place()
    {
        // The old label was a literal inside wnStopVideoWatch, which is how a button can end a
        // session wearing a different name from the one it started with.
        Assert.Contains("function wnPaintVideoButton()", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("btn.textContent = '🎥 Watch & listen (AI)'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_says_why_it_watches_instead_of_reading()
    {
        // Removing a button without saying why leaves the seller assuming the feature broke. The
        // reason is specific and checkable, and it names the status code.
        Assert.Contains("Whatnot", Html, StringComparison.Ordinal);
        Assert.Contains("HTTP 403", Html, StringComparison.Ordinal);
        Assert.Contains("How watching the show works", Html, StringComparison.Ordinal);
        Assert.DoesNotContain("How the page read works", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_the_lot_in_by_hand_is_still_offered_and_still_free()
    {
        // The path that never needed Whatnot's permission, or an AI read, or a key.
        Assert.Contains("Price it", Html, StringComparison.Ordinal);
        Assert.Contains("costs nothing", Html, StringComparison.Ordinal);
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
}
