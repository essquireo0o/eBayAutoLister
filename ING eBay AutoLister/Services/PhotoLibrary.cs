using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

// Representative-photo library for USED items. The seller shoots their real stock once per model;
// those photos live in a per-model folder under photos/ and are reused (with disclosure) across
// every identical used unit of that model — so used listings show a REAL photo of the seller's
// actual inventory without a per-unit shoot. New items don't use this (they pull stock images).
public sealed class PhotoLibrary(IWebHostEnvironment env)
{
    /// <summary>
    /// Four model folders this app used to invent on first run, and no longer does.
    /// </summary>
    /// <remarks>
    /// They were pre-created "so the folders show up empty in the UI, inviting the seller to
    /// populate them". In practice the invitation reads as clutter: the owner opens the Photo
    /// Library to look at the photographs they just took and finds their one real folder filed
    /// among four empty ones named after somebody else's mining hardware. The library now shows
    /// only folders that exist because a photograph or a person put them there.
    /// </remarks>
    private static readonly string[] RetiredSeedFolders = ["S19_95TH", "S19_110TH", "S19j_Pro", "L7"];

    /// <summary>Written once the retired seeds have been swept, so the sweep can never run twice.</summary>
    private const string SeedRetirementMarker = ".seeds-retired";

    /// <summary>
    /// Folders under <c>photos/</c> that are this app's plumbing rather than a model's photographs.
    /// </summary>
    /// <remarks>
    /// <c>photo-box-video</c> holds phone video clips and is served on its own <c>/photo-box-video</c>
    /// route. It is not a model, it never contains an image, and listing it puts a permanently empty
    /// tile in the library.
    /// </remarks>
    private static readonly HashSet<string> NotModelFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "photo-box-video",
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    // Appended to a used-item description when the listing uses library photos rather than a
    // per-unit shoot. eBay expects the actual item represented; a DISCLOSED representative photo of
    // identical, individually-tested stock is the accepted practice for high-volume used inventory.
    /// <summary>
    /// Where Photo Box snaps land. It outlived the ESP32 board that named it (removed 2026-08-21)
    /// because the seller's existing photos are in this folder — renaming it would orphan them.
    /// </summary>
    public const string PhotoBoxFolder = "photo-box";

    public const string RepresentativeDisclosure =
        "Photos are representative of the unit you will receive. Every unit is individually tested; " +
        "see the condition notes and description for the exact grade and specifications of your item.";

    private string RootPath => Path.Combine(env.ContentRootPath, "photos");
    private void EnsureRoot() => Directory.CreateDirectory(RootPath);

    // Legacy entry point (kept for the existing /api/photos/default-folders caller, which prints a
    // one-line folder summary on the Settings screen). It used to answer with four folders that
    // existed only because it had just created them; it now answers with what is actually there.
    public IReadOnlyList<PhotoFolderSummary> GetDefaultFolders() => GetAllFolders();

    /// <summary>Every model folder the seller actually has, alphabetically.</summary>
    public IReadOnlyList<PhotoFolderSummary> GetAllFolders()
    {
        EnsureRoot();
        RetireSeedFoldersOnce();
        return Directory.EnumerateDirectories(RootPath)
            .Select(d => Path.GetFileName(d)!)
            .Where(name => !NotModelFolders.Contains(name))
            .Select(Summarize)
            .OrderBy(s => s.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sweeps away the four invented seed folders, once, and only while they are still empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not creating them any more does nothing for the machines that already ran the old build —
    /// the owner's library had four empty tiles sitting on disk next to the one real folder. So the
    /// retirement has to remove them, not merely stop making them.
    /// </para>
    /// <para>
    /// Two guards, because this deletes directories in the seller's data folder. A folder is only
    /// touched when it is <b>completely empty</b> — not "holds no images", empty, so a stray file of
    /// any kind keeps it — and the whole sweep runs <b>once</b>, marked by a dotfile in the photos
    /// root. Without the marker a seller who later creates an "L7" folder and has not put a
    /// photograph in it yet would find the app had quietly deleted it.
    /// </para>
    /// </remarks>
    private void RetireSeedFoldersOnce()
    {
        var marker = Path.Combine(RootPath, SeedRetirementMarker);
        try
        {
            if (File.Exists(marker)) return;

            foreach (var name in RetiredSeedFolders)
            {
                var dir = Path.Combine(RootPath, name);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }

            File.WriteAllText(marker, "The four pre-made model folders were removed on "
                + $"{DateTime.UtcNow:yyyy-MM-dd}. This file stops that from ever running again, so "
                + "folders you make yourself are left alone even while they are empty.");
        }
        catch (IOException) { /* a folder in use is not worth failing a page load over */ }
        catch (UnauthorizedAccessException) { /* nor one we may not touch */ }
    }

    public IReadOnlyList<string> ListPhotoUrls(string modelKey)
    {
        var key = Sanitize(modelKey);
        var dir = Path.Combine(RootPath, key);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .Select(f => $"/photos/{key}/{Path.GetFileName(f)}")
            .ToList();
    }

    /// <summary>
    /// The Photo Library screen is a capture inbox, so the photograph just accepted from the phone
    /// belongs first. This is deliberately separate from <see cref="ListPhotoUrls"/>: that method's
    /// filename order is the stable eBay gallery order and must not move every time a file is edited.
    /// </summary>
    public IReadOnlyList<string> ListPhotoUrlsNewestFirst(string modelKey)
    {
        var key = Sanitize(modelKey);
        var dir = Path.Combine(RootPath, key);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"/photos/{key}/{Path.GetFileName(f)}")
            .ToList();
    }

    public string CreateFolder(string modelKey)
    {
        var key = Sanitize(modelKey);
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Model key is empty.");
        Directory.CreateDirectory(Path.Combine(RootPath, key));
        return key;
    }

    public async Task<string> SavePhotoAsync(string modelKey, byte[] bytes, string ext)
    {
        var key = CreateFolder(modelKey);
        ext = ext.TrimStart('.').ToLowerInvariant();
        if (!ImageExtensions.Contains("." + ext)) ext = "png";
        // Named by WHAT IT IS, not by when it arrived. A random name meant identical bytes always
        // became another file, and nothing in the app deduplicates at the call site: the owner's
        // photo-box ended up holding the same enhanced shot five times — four of them written
        // inside one second — because every AI Enhance of the same photo produced a byte-identical
        // result and a brand new Guid to put it under. Content addressing settles it once for every
        // path that saves: enhance, cut-out, portrait, the editor, and the phone upload.
        //
        // The consequence worth stating: saving the same photograph twice returns the SAME url
        // rather than a second one. That is the point — two identical files are not two photographs
        // — and it is why the write is skipped when the name is already there. Different pixels
        // still mean a different name, so nothing is ever silently replaced by something else.
        var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                          .ToLowerInvariant()[..32] + "." + ext;
        var path = Path.Combine(RootPath, key, name);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        return $"/photos/{key}/{name}";
    }

    // Delete one photo from a model's folder (a bad shot the seller wants out of the rotation).
    // Both parts are sanitized to bare names, so nothing outside the model's own folder is reachable.
    // Returns false when the file isn't a library image that exists.
    public bool DeletePhoto(string modelKey, string fileName)
    {
        var key  = Sanitize(modelKey);
        var name = Sanitize(fileName);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) return false;
        if (!ImageExtensions.Contains(Path.GetExtension(name))) return false;

        var path = Path.Combine(RootPath, key, name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // Given a listing's model and/or title, return the representative photos from the best-matching
    // model folder that actually HAS photos. Null when nothing matches, or when two models fit
    // equally well — the UI then prompts the seller to add photos for this model once, after which
    // every future unit reuses them. A wrong match is worse than no match here: these photos go out
    // under a line telling the buyer they represent the unit being shipped.
    public RepresentativeMatch? ResolveForListing(string? model, string? title)
    {
        var folders = GetAllFolders().Where(f => f.ImageCount > 0).ToList();
        if (folders.Count == 0) return null;

        // Whole words, not substrings. A folder keyed "S19_95TH" holds photos of a 95TH machine; a
        // substring test would let its "s19" claim an "S19j Pro" listing and put the wrong unit's
        // photos under a line that promises they represent what ships.
        var hayTokens = Norm($"{model} {title}")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (hayTokens.Count == 0) return null;

        PhotoFolderSummary? best = null;
        var bestScore = 0;
        var ambiguous = false;
        foreach (var f in folders)
        {
            var keyTokens = Norm(f.ModelKey).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keyTokens.Length == 0) continue;
            // Token overlap between the folder key and the listing's model/title.
            var score = keyTokens.Count(hayTokens.Contains);
            if (score > bestScore) { bestScore = score; best = f; ambiguous = false; }
            else if (score == bestScore && score > 0) ambiguous = true;
        }
        // Two models fitting equally well means the title has not said which one this is — "Antminer
        // S19" fits the 95TH and the 110TH folder alike. Guessing ships one hashrate's photos on the
        // other's listing; no match asks the seller instead, which is what the UI is already for.
        if (best is null || bestScore == 0 || ambiguous) return null;
        return new RepresentativeMatch(best.ModelKey, ListPhotoUrls(best.ModelKey), RepresentativeDisclosure);
    }

    // Canonical per-model bucket key for ANY product (not just miners): prefer Brand+Model, else
    // the first few distinctive tokens of the title. This is what makes the library work for every
    // category — the second time the seller lists the same used model, its photos are reused.
    public string DeriveModelKey(string? brand, string? model, string? title)
    {
        var basis = !string.IsNullOrWhiteSpace(model)
            ? $"{brand} {model}"
            : !string.IsNullOrWhiteSpace(brand)
                ? $"{brand} {title}"
                : title ?? "";
        var tokens = Norm(basis).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Drop generic condition/marketing words so "Used Antminer S19j Pro" -> "antminer_s19j_pro".
        string[] stop = ["used", "new", "the", "for", "with", "and", "lot", "of", "bitcoin", "asic", "miner"];
        var kept = tokens.Where(t => !stop.Contains(t)).Take(4).ToArray();
        if (kept.Length == 0) kept = tokens.Take(3).ToArray();
        var key = Sanitize(string.Join("_", kept));
        return string.IsNullOrWhiteSpace(key) ? "misc" : key;
    }

    private PhotoFolderSummary Summarize(string folder)
    {
        var path = Path.Combine(RootPath, folder);
        var images = Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList()
            : [];

        // When this folder last received a photograph. It is what lets the screen open on the
        // folder the seller has just been filling instead of on whichever one sorts first —
        // see the picker in loadPhotoLibrary. Null for a folder that has never held one.
        DateTime? newest = null;
        foreach (var file in images)
        {
            try
            {
                var at = File.GetLastWriteTimeUtc(file);
                if (newest is null || at > newest) newest = at;
            }
            catch (IOException) { /* a file being written this instant is not worth failing over */ }
            catch (UnauthorizedAccessException) { /* nor one we may not stat */ }
        }

        return new PhotoFolderSummary(folder, path, images.Count, newest);
    }

    private static string Sanitize(string? name) =>
        Regex.Replace(Path.GetFileName(name ?? ""), @"[^A-Za-z0-9_\-\.]", "_");

    private static string Norm(string? s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}

public sealed record PhotoFolderSummary(string ModelKey, string Path, int ImageCount, DateTime? NewestAtUtc = null);

public sealed record RepresentativeMatch(string ModelKey, IReadOnlyList<string> PhotoUrls, string Disclosure);

public sealed record LibraryUploadRequest(string ModelKey, string ImageBase64, string? MimeType);
public sealed record LibraryCreateRequest(string ModelKey);
public sealed record LibraryDeleteRequest(string ModelKey, string FileName);
