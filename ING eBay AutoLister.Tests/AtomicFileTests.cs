using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The saved-means-saved guarantees, held as tests because every one of them is invisible until
/// the day it matters — and by then the seller's accounts are already gone.
///
/// What these lock down: a save never leaves a partial file, a save always leaves the previous
/// contents recoverable, and a file that cannot be read is never silently replaced with nothing.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ing-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [Fact]
    public void WritesTheContents()
    {
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, """{"key":"one"}""");
        Assert.Equal("""{"key":"one"}""", File.ReadAllText(file));
    }

    [Fact]
    public void LeavesNoTempFileBehind()
    {
        // A stray .tmp is how a half-written save survives to confuse the next one.
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, "first");
        AtomicFile.WriteAllText(file, "second");
        Assert.False(File.Exists(AtomicFile.TempPathFor(file)));
    }

    [Fact]
    public void KeepsThePreviousContentsAsABackup()
    {
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, "original");
        AtomicFile.WriteAllText(file, "replacement");

        Assert.Equal("replacement", File.ReadAllText(file));
        Assert.Equal("original", File.ReadAllText(AtomicFile.BackupPathFor(file)));
    }

    [Fact]
    public void RecoversFromTheBackupWhenTheMainFileIsEmptied()
    {
        // Exactly the old failure: the process died between truncate and write.
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, "good contents");
        AtomicFile.WriteAllText(file, "newer contents");
        File.WriteAllText(file, "");

        Assert.Equal("good contents", AtomicFile.ReadWithRecovery(file));
    }

    [Fact]
    public void RecoversFromTheBackupWhenTheMainFileIsGarbage()
    {
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, """{"ok":true}""");
        AtomicFile.WriteAllText(file, """{"ok":true,"v":2}""");
        File.WriteAllText(file, """{"ok":tr""");   // truncated mid-write

        var recovered = AtomicFile.ReadWithRecovery(file, IsJson);
        Assert.Equal("""{"ok":true}""", recovered);
    }

    [Fact]
    public void ReturnsNullWhenNothingWasEverSaved()
    {
        // A first run must read as "nothing saved yet", not as a recovery failure.
        Assert.Null(AtomicFile.ReadWithRecovery(Path_("never-written.json")));
    }

    [Fact]
    public void PrefersTheMainFileOverTheBackup()
    {
        var file = Path_("creds.json");
        AtomicFile.WriteAllText(file, "older");
        AtomicFile.WriteAllText(file, "newer");
        Assert.Equal("newer", AtomicFile.ReadWithRecovery(file, IsAnything));
    }

    [Fact]
    public void TheNodeSaveWritesToATempAndRenames()
    {
        // The browser sessions are written by Playwright, not by C#, so the same guarantee has to
        // exist in the JavaScript. Asserting the shape is the only check available without Node.
        var js = AtomicFile.NodeWriteJs("C:/tmp/session.json", "JSON.stringify(state)");
        Assert.Contains(".tmp", js);
        Assert.Contains("renameSync", js);
        Assert.Contains(".bak", js);
        // The dangerous form is writing straight at the target.
        Assert.DoesNotContain("writeFileSync(target,", js.Replace(" ", ""));
    }

    private static bool IsJson(string text)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(text); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static bool IsAnything(string text) => true;
}
