namespace ING_eBay_AutoLister.Services;

/// <summary>Copies a photo the app has taken out to a folder the seller actually opens.</summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The Photo Box worked end to end — phone shutter, transfer, full-resolution
/// file on disk, AI enhancement — and the seller still could not get a photograph. Every capture
/// landed at <c>%LOCALAPPDATA%\ING AutoLister\photos\photo-box\1f4c…9be.jpg</c>: a hidden folder,
/// under a name nobody can read, with no button anywhere in the app that produced a file they could
/// see. The owner asked for this repeatedly — <i>"I can't take a photo on my camera and save it to my
/// desktop"</i> — and each time the answer had been to fix the capture, which was not what was broken.
/// </para>
/// <para>
/// <b>The name matters as much as the copy.</b> A GUID is the right name for a file the app owns and
/// the wrong name for one a person is about to look at in a folder full of their own things, so the
/// copy is renamed to the moment it was taken. Collisions are resolved by counting up rather than by
/// overwriting: two photographs a second apart are two photographs, and the second one silently
/// replacing the first is exactly the kind of loss nobody notices until the listing is live.
/// </para>
/// <para>
/// Nothing is moved and nothing is deleted. The library keeps its copy — the listing flow still reads
/// from there — so saving is additive and can be done twice without consequence.
/// </para>
/// </remarks>
public sealed class PhotoExport(IWebHostEnvironment env)
{
    /// <summary>The real Desktop, which is not always under the user profile — OneDrive moves it.</summary>
    /// <remarks>
    /// Asked of Windows rather than built from a home directory, because a redirected Desktop is the
    /// common case on a machine signed into OneDrive and a hand-built path would write to a folder
    /// the seller does not see.
    /// </remarks>
    public static string DesktopFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>One saved copy: where it went and what it is now called.</summary>
    public sealed record Saved(string FullPath, string FileName, string Folder);

    /// <summary>
    /// The last file this app copied out, so "show me it" needs no path from the page.
    /// </summary>
    /// <remarks>
    /// Deliberately not a parameter. Opening a file manager at a path the browser supplied is a
    /// remote-controlled <c>Process.Start</c>, and there is no validation of an arbitrary string
    /// that is as convincing as never accepting one: the only thing this can ever reveal is the file
    /// the seller just asked this app to write.
    /// </remarks>
    public string? LastSavedPath { get; private set; }

    /// <summary>
    /// Copies one already-saved photo out. Returns what it was named, so the app can say it.
    /// </summary>
    /// <param name="url">A URL this app produced — <c>/photos/…</c> or <c>/generated-photos/…</c>.</param>
    /// <param name="destination">Where to put it. Defaults to the Desktop.</param>
    /// <param name="takenAt">The timestamp the copy is named after.</param>
    public Saved Save(string url, string? destination, DateTimeOffset takenAt)
    {
        var source = ResolveLocalPhoto(url);
        var folder = string.IsNullOrWhiteSpace(destination) ? DesktopFolder : destination!;

        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Windows did not report a Desktop folder for this account.");

        Directory.CreateDirectory(folder);

        var ext = Path.GetExtension(source);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        var name = UniqueName(folder, $"ING Photo {takenAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}", ext);

        var full = Path.Combine(folder, name);
        File.Copy(source, full, overwrite: false);
        LastSavedPath = full;
        return new Saved(full, name, folder);
    }

    /// <summary>
    /// The first name in this folder that is not taken — "… (2)", "… (3)" — never an overwrite.
    /// </summary>
    /// <remarks>
    /// Pure apart from the existence check, and separated out because the counting is the part worth
    /// testing: a burst of three lands three files inside the same second, and all three have to
    /// survive.
    /// </remarks>
    public static string UniqueName(string folder, string baseName, string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var candidate = baseName + ext;
        for (var n = 2; File.Exists(Path.Combine(folder, candidate)); n++)
            candidate = $"{baseName} ({n}){ext}";
        return candidate;
    }

    /// <summary>
    /// A URL this app served, turned into the file behind it — or an error, never a guess.
    /// </summary>
    /// <remarks>
    /// The same two roots and the same containment check <see cref="PhotoEnhancer"/> uses, for the
    /// same reason: this takes a path from the page and copies whatever it names to somewhere the
    /// seller will open, so "/photos/../../credentials.json" must resolve to a refusal rather than to
    /// a file. Kept beside that copy rather than shared with it while PhotoEnhancer is held by
    /// another session.
    /// </remarks>
    private string ResolveLocalPhoto(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A photo URL is required.");
        var clean = Uri.UnescapeDataString(url.Split('?', '#')[0])
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        string root, relative;
        var photosPrefix = "photos" + Path.DirectorySeparatorChar;
        var generatedPrefix = "generated-photos" + Path.DirectorySeparatorChar;
        if (clean.StartsWith(photosPrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(env.ContentRootPath, "photos");
            relative = clean[photosPrefix.Length..];
        }
        else if (clean.StartsWith(generatedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(env.ContentRootPath, "generated-photos");
            relative = clean[generatedPrefix.Length..];
        }
        else
        {
            throw new ArgumentException("Only photos this app saved can be copied out.");
        }

        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("That photo is no longer in the library.");
        return full;
    }
}

/// <summary>What the page asks to have copied out. An empty list means "the one url".</summary>
public sealed record SavePhotosRequest(string? Url, List<string>? Urls);
