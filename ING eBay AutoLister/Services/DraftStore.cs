using ING_eBay_AutoLister.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Saved drafts, one JSON file each: on the seller's Desktop in the desktop build, in a folder of
/// their own on the server in the hosted one.
/// </summary>
/// <remarks>
/// <para>
/// This was written for one seller on their own machine and kept everything in
/// <c>Desktop\eBayListing</c>. The hosted container has no Desktop — .NET answers an empty string
/// for a special folder that does not exist — so the folder became the relative path
/// <c>eBayListing</c>, which is <c>/app/eBayListing</c>, which the unprivileged account the image
/// runs as cannot create. Every one of these endpoints answered 500 on app.inglisting.com, and had
/// the folder been writable it would have been ONE folder with every seller's drafts in it.
/// </para>
/// <para>
/// So under a per-user <see cref="UserScope"/> each seller gets
/// <c>&lt;data home&gt;/App_Data/drafts/&lt;user id&gt;</c>, on the same volume as the database. The
/// folder is resolved on every call and never cached, for the reason <see cref="UserScope.OwnerId"/>
/// gives: this is a singleton, and holding on to the first answer is how one seller's drafts end up
/// in front of another. With nobody signed in it reads nothing and refuses to save.
/// </para>
/// </remarks>
public class DraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly UserScope _scope;
    private readonly string _perUserRoot;

    public DraftStore(UserScope? scope = null)
        : this(scope, Path.Combine(AppPaths.DataHome, "App_Data", "drafts")) { }

    /// <param name="perUserRoot">The folder the per-seller folders go in. Unused by the desktop build.</param>
    public DraftStore(UserScope? scope, string perUserRoot)
    {
        _scope       = scope ?? UserScope.Desktop;
        _perUserRoot = perUserRoot;
    }

    /// <summary>True when each signed-in seller has their own drafts — the hosted build.</summary>
    public bool IsPerUser => _scope.IsPerUser;

    /// <summary>
    /// The folder the caller's drafts live in, created if it is not there yet. Null when a hosted
    /// deployment has nobody signed in on this call.
    /// </summary>
    private string? DraftsDir
    {
        get
        {
            string dir;
            if (_scope.IsPerUser)
            {
                if (_scope.OwnerId is not { } owner) return null;
                dir = Path.Combine(_perUserRoot, owner.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                dir = Path.Combine(desktop, "eBayListing");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Makes sure the caller's drafts folder exists. Empty when there is no caller.</summary>
    public string EnsureFolder() => DraftsDir ?? "";

    public List<DraftSummary> ListDrafts()
    {
        if (DraftsDir is not { } dir) return [];

        return Directory.GetFiles(dir, "*.json")
            .Select(f =>
            {
                try
                {
                    using var stream = File.OpenRead(f);
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;
                    // Either casing. SaveDraft writes "Title" and "SavedAt"; only a file put in the
                    // folder by hand says "title". Reading just the lower-case names made every
                    // draft this store saved "Untitled" with no date, so the newest-first sort
                    // below sorted nothing.
                    var title   = Text(root, "title", "Title") ?? "Untitled";
                    var savedAt = Text(root, "savedAt", "SavedAt") ?? "";
                    return new DraftSummary(Path.GetFileName(f), title, savedAt);
                }
                catch { return null; }
            })
            .Where(d => d != null)
            .Cast<DraftSummary>()
            .OrderByDescending(d => d.SavedAt)
            .ToList();
    }

    public DraftFile? LoadDraft(string filename)
    {
        var path = SafePath(filename);
        if (path is null || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<DraftFile>(File.ReadAllText(path), JsonOptions);
    }

    public string SaveDraft(DraftFile draft)
    {
        // Not a quiet no-op like the reads: the filename handed back is the seller's only way to
        // this draft again, and a name for a file that was never written is a receipt for nothing.
        var dir = DraftsDir
            ?? throw new InvalidOperationException("Nobody is signed in, so there is no one to save this draft for.");

        draft.SavedAt = DateTimeOffset.UtcNow.ToString("O");

        var filename = !string.IsNullOrWhiteSpace(draft.Filename)
            ? Sanitize(draft.Filename)
            : $"{Slugify(draft.Title)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json";

        draft.Filename = filename;
        File.WriteAllText(Path.Combine(dir, filename), JsonSerializer.Serialize(draft, JsonOptions));
        return filename;
    }

    public void DeleteDraft(string filename)
    {
        var path = SafePath(filename);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// The one file a name can mean: inside the caller's own folder, whatever the name says.
    /// <see cref="Sanitize"/> drops every directory part, so <c>../7/draft.json</c> is this
    /// seller's <c>draft.json</c> and never seller 7's.
    /// </summary>
    private string? SafePath(string filename) =>
        DraftsDir is { } dir ? Path.Combine(dir, Sanitize(filename)) : null;

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string Sanitize(string name) =>
        Regex.Replace(Path.GetFileName(name), @"[^a-zA-Z0-9_\-\.]", "_");

    private static string Slugify(string? input)
    {
        var s = Regex.Replace((input ?? "draft").ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        return s.Length > 40 ? s[..40] : (s.Length == 0 ? "draft" : s);
    }
}

public record DraftSummary(string Filename, string Title, string SavedAt);

public class DraftFile
{
    public string? Filename         { get; set; }
    public string  Title            { get; set; } = "";
    public string  SavedAt          { get; set; } = "";
    public PostListingRequest Data  { get; set; } = new();
    public string? ImageBase64      { get; set; }
    public string? MimeType         { get; set; }
    public string? VisualDescription { get; set; }
}

/// <summary>
/// The saved-drafts endpoints. Kept out of Program.cs as one small unit so the same handlers the app
/// maps are the ones the tests exercise.
/// </summary>
public static class DraftEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/local-drafts/ensure-folder", (DraftStore drafts) =>
        {
            var path = drafts.EnsureFolder();
            // The desktop seller is told where on their own machine the drafts are. A hosted one
            // has no use for a path on the server's disk, and no business reading it.
            return Results.Ok(new { path = drafts.IsPerUser ? "" : path });
        });

        app.MapGet("/api/local-drafts/list", (DraftStore drafts) => Results.Ok(drafts.ListDrafts()));

        app.MapPost("/api/local-drafts/save", (DraftFile draft, DraftStore drafts, ActionLog log) =>
        {
            var filename = drafts.SaveDraft(draft);
            log.Add("Info", "Draft saved locally", $"{filename}");
            return Results.Ok(new { filename });
        });

        app.MapGet("/api/local-drafts/load/{filename}", (string filename, DraftStore drafts) =>
        {
            var draft = drafts.LoadDraft(filename);
            return draft != null ? Results.Ok(draft) : Results.NotFound();
        });

        app.MapDelete("/api/local-drafts/delete/{filename}", (string filename, DraftStore drafts, ActionLog log) =>
        {
            drafts.DeleteDraft(filename);
            log.Add("Info", "Draft deleted", filename);
            return Results.Ok();
        });
    }
}
