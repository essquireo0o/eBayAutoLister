namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A photograph is one file per version, not one file per attempt.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24: "AI enhance is fucked up and its saving tons of pictures." Measured on the owner's
/// library — 73 files in <c>photo-box</c>, five of them the same 227,936 bytes, and rows of
/// near-identical thumbnails they were deleting by hand one at a time.
/// </para>
/// <para>
/// Every treatment (AI Enhance, Cut out, Portrait) POSTs the picture and gets a NEW library file
/// back, then swaps it into the filmstrip. The file it swapped OUT was never removed, so each
/// retouch left its predecessor behind: enhance one shot five times and five files land, identical
/// to the byte whenever the treatment is deterministic. Nothing was wrong with the saving — every
/// call did exactly what it was told; nothing was ever told to clean up.
/// </para>
/// <para>
/// The one thing these tests exist to protect is the exception: an ORIGINAL capture is never
/// superseded. It is the photograph the seller actually took, it is what "Save to my computer"
/// exports, and the first enhance of an untouched photo has to keep both. A tidy-up that eats the
/// original would be a far worse bug than the mess it cleans.
/// </para>
/// </remarks>
public class PhotoTreatmentsDoNotPileUpTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void Superseding_is_defined_and_deletes_only_a_derived_file()
    {
        Assert.Contains("function pbSupersede(", Js, StringComparison.Ordinal);

        // The guard IS the safety property. Without it the call deletes whatever it was handed,
        // which on a first enhance is the seller's untouched photograph. Asserted as the early
        // return rather than as a particular boolean, so the rule is pinned and the spelling is not.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        Assert.Contains("if (oldUrl === original) return;", body, StringComparison.Ordinal);
        Assert.Contains("pbForgetLibraryFile(oldUrl)", body, StringComparison.Ordinal);

        // And the return has to come BEFORE the delete, or the guard guards nothing.
        Assert.True(body.IndexOf("if (oldUrl === original) return;", StringComparison.Ordinal)
                  < body.IndexOf("pbForgetLibraryFile(oldUrl)", StringComparison.Ordinal),
            "the original-capture guard must sit above the delete.");
    }

    [Fact]
    public void Every_treatment_that_replaces_a_picture_supersedes_the_one_it_replaced()
    {
        // Four sites produce a replacement picture: the desktop shutter's auto-enhance, the phone
        // shutter's auto-enhance, the per-photo Enhance button, and pbRework (Cut out / Portrait).
        // A fifth treatment added later without this call is a fifth way to fill the library up.
        var calls = Occurrences(Js, "pbSupersede(");
        Assert.True(calls >= 5, // one definition + four call sites
            $"expected pbSupersede to be called at all four treatment sites; found {calls - 1}.");
    }

    [Fact]
    public void The_original_capture_stays_reachable_behind_whatever_replaced_it()
    {
        // Save to my computer exports the untouched frame first, and it finds it through this map.
        // If superseding dropped the chain, Save would start exporting only the studio crop --
        // which is the failure that would have shipped with the Save button in the first place.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        Assert.Contains("pbOriginalOf.get(oldUrl) || oldUrl", body, StringComparison.Ordinal);
        Assert.Contains("pbOriginalOf.set(newUrl, original)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tidy_up_can_never_fail_a_treatment_the_seller_can_already_see()
    {
        // Fire-and-forget. The picture is on screen and in the library before this runs; a 404 on
        // the cleanup must not surface as "your enhancement failed".
        var body = Slice(Js, "async function pbForgetLibraryFile", "// ── Getting the photograph out");
        Assert.Contains("try {", body, StringComparison.Ordinal);
        Assert.Contains("catch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 162);
    }

    [Fact]
    public void A_file_two_pictures_share_is_never_deleted_out_from_under_one_of_them()
    {
        // 50c8e90 made library filenames the SHA-256 of the bytes, so two identical pictures are
        // now ONE file and one url. Before that, a superseded file could not possibly be anyone
        // else's; now deleting it can blank a second filmstrip entry, or destroy the original that
        // "Save to my computer" was going to export for a different shot.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        Assert.Contains("pbUrlStillInUse(oldUrl)", body, StringComparison.Ordinal);

        var guard = Slice(Js, "function pbUrlStillInUse(", "async function pbForgetLibraryFile");
        Assert.Contains("pbSessionSnaps.includes(url)", guard, StringComparison.Ordinal);
        Assert.Contains("pbOriginalOf.values()", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void Lineage_survives_a_file_that_is_still_in_use()
    {
        // The subtle half. If the map entry is dropped while another picture still holds the file,
        // the next entry to move off it reads it as an ORIGINAL — that is what "absent from the
        // map" means — and then refuses to collect it for the rest of the session. So the delete
        // of the mapping has to sit after the in-use check, not before it.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        var inUse = body.IndexOf("if (pbUrlStillInUse(oldUrl)) return;", StringComparison.Ordinal);
        var forget = body.IndexOf("pbOriginalOf.delete(oldUrl)", StringComparison.Ordinal);
        Assert.True(inUse >= 0, "the in-use guard is gone from pbSupersede.");
        Assert.True(forget > inUse,
            "pbOriginalOf.delete must come AFTER the in-use check, or a shared file becomes uncollectable.");
    }

    [Fact]
    public void A_refused_cut_out_does_not_claim_a_studio_background()
    {
        // 4a10372: when the cut-out is refused the photo is still improved, at full resolution,
        // with the background exactly as photographed. Labelling that "✨ Enhanced" promises a
        // backdrop that is not there, and the seller finds out on the live listing instead.
        Assert.Contains("pbKeptBackground", Js, StringComparison.Ordinal);
        Assert.Contains("backgroundReplaced === false", Js, StringComparison.Ordinal);
        Assert.Contains("Background left as photographed", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_collector_never_reaches_the_store_that_holds_live_listing_pictures()
    {
        // Cut out and Portrait answer with /generated-photos/<file>, not a library url. Widening
        // the pattern to collect those would look like finishing the job and would eventually blank
        // the pictures on a listing the seller has already published: those exact urls are pushed
        // into listing ImageUrls (Program.cs 1231, 1294, 1323), and nothing on disk tells a
        // superseded cut-out apart from a photograph currently illustrating a live listing.
        // From the comment, not the signature: the reasoning this pins sits above the function.
        var body = Slice(Js, "// Drops a superseded file from the library",
                             "// ── Getting the photograph out");

        // Verbatim, because the thing being asserted is a JS regex full of backslashes.
        Assert.Contains(@"/^\/photos\/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("generated-photos/(", body, StringComparison.Ordinal);

        // The reason has to stay next to the rule. A scope limit with no stated why is the kind of
        // thing the next session deletes on sight.
        Assert.Contains("ImageUrls", body, StringComparison.Ordinal);
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in app.js.");
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\" in app.js.");
        return text[a..b];
    }

    private static int Occurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository root above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
