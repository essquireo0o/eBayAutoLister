using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The representative-photo library is what turns "shoot every unit" into "shoot every model once".
// The seller photographs their real stock for a model, and every identical used unit after that
// reuses those photos with a disclosure line. Three things have to hold for that to be worth having:
//
//   1. The same model has to land in the same bucket every time it is listed, however the seller
//      typed it — otherwise the photos are never found again and the shortcut does nothing.
//   2. A listing must never be handed another model's photos. They go out under a line promising
//      the buyer they represent the unit being shipped; the wrong hashrate there is a
//      not-as-described case, which costs the sale, the fees and the defect.
//   3. Nothing a model key or file name says can reach outside the photos folder.
//
// Every path here is under a temp content root, so no test touches the seller's real photos/.
public class PhotoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"photo_library_{Guid.NewGuid():N}");

    private PhotoLibrary NewLibrary() => new(new StubWebHostEnvironment { ContentRootPath = _root });

    private string PhotosDir(string modelKey) => Path.Combine(_root, "photos", modelKey);

    // Nothing in the library decodes an image, so any bytes stand in for a photo.
    private static byte[] Bytes(string marker) => System.Text.Encoding.UTF8.GetBytes(marker);

    private void AddFile(string modelKey, string fileName, string marker = "photo")
    {
        var dir = PhotosDir(modelKey);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), Bytes(marker));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── Bucketing: the same model has to come back to the same folder ────────

    // The seller fills in Model on one listing and leaves it blank on the next. If those bucket
    // apart, the second unit finds no photos and the library has saved nobody anything.
    [Fact]
    public void The_same_model_buckets_together_whether_or_not_the_model_field_was_filled_in()
    {
        var photos = NewLibrary();

        var withModel = photos.DeriveModelKey("Bitmain", "Antminer S19j Pro", null);
        var withoutModel = photos.DeriveModelKey("Bitmain", null, "Used Antminer S19j Pro ASIC Miner");

        Assert.Equal("bitmain_antminer_s19j_pro", withModel);
        Assert.Equal(withModel, withoutModel);
    }

    // "Used", "ASIC" and "Miner" are words about the listing, not about which machine it is. Letting
    // them into the key would split one model across a bucket per phrasing.
    [Fact]
    public void Condition_and_marketing_words_do_not_split_one_model_into_two_buckets()
    {
        var photos = NewLibrary();

        Assert.Equal(
            photos.DeriveModelKey(null, null, "Antminer S19j Pro"),
            photos.DeriveModelKey(null, null, "Used Antminer S19j Pro ASIC Bitcoin Miner"));
    }

    [Fact]
    public void Capitalisation_and_punctuation_do_not_split_a_bucket()
    {
        var photos = NewLibrary();

        Assert.Equal(
            photos.DeriveModelKey("bitmain", "antminer s19j pro", null),
            photos.DeriveModelKey("BITMAIN", "Antminer S19j-Pro!!", null));
    }

    // The key stops at four tokens so the specifics a seller tacks onto a title — hashrate, PSU,
    // shipping — do not push the same machine into a bucket of its own.
    [Fact]
    public void A_long_title_buckets_with_the_short_one_for_the_same_machine()
    {
        var photos = NewLibrary();

        var shortForm = photos.DeriveModelKey("Bitmain", "Antminer S19j Pro", null);
        var longForm = photos.DeriveModelKey(
            null, null, "Bitmain Antminer S19j Pro 104TH Bitcoin Miner PSU Included Fast Shipping");

        Assert.Equal("bitmain_antminer_s19j_pro", shortForm);
        Assert.Equal(shortForm, longForm);
    }

    // A listing with nothing usable still needs somewhere to put photos; an empty key would be a
    // write straight into photos/ itself.
    [Fact]
    public void A_listing_with_nothing_to_go_on_buckets_as_misc()
    {
        var photos = NewLibrary();

        Assert.Equal("misc", photos.DeriveModelKey(null, null, null));
        Assert.Equal("misc", photos.DeriveModelKey(null, null, "!!! ??? ***"));
    }

    // Every word being generic is not the same as having no words: the seller still gets a bucket
    // they can rename later, rather than everything generic piling into one.
    [Fact]
    public void A_title_of_only_generic_words_still_gets_a_bucket_of_its_own()
    {
        var photos = NewLibrary();

        Assert.Equal("used_asic_miner", photos.DeriveModelKey(null, null, "Used ASIC Miner"));
    }

    // The key is used as a folder name straight after it is derived, so a title can never be the
    // thing that decides where on disk the write lands.
    [Fact]
    public void A_derived_key_is_always_a_bare_folder_name()
    {
        var photos = NewLibrary();

        var key = photos.DeriveModelKey(null, "../../Windows/System32", "C:\\secrets\\keys");

        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('\\', key);
        Assert.DoesNotContain("..", key);
    }

    // ── Folders ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_library_offers_the_seed_model_folders_empty()
    {
        var photos = NewLibrary();

        var folders = photos.GetDefaultFolders();

        Assert.Equal(4, folders.Count);
        Assert.All(folders, f => Assert.Equal(0, f.ImageCount));
        Assert.Contains(folders, f => f.ModelKey == "S19j_Pro");
    }

    [Fact]
    public void Folders_the_seller_made_are_listed_with_the_seeds_in_name_order()
    {
        var photos = NewLibrary();
        photos.CreateFolder("Whatsminer_M30S");
        photos.CreateFolder("Avalon_1246");

        var keys = photos.GetAllFolders().Select(f => f.ModelKey).ToList();

        Assert.Contains("Whatsminer_M30S", keys);
        Assert.Contains("Avalon_1246", keys);
        Assert.Contains("L7", keys);
        Assert.Equal(keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), keys);
    }

    // The count on the folder is what tells the seller whether this model is ready to reuse. A
    // stray notes file inflating it reads as "photos done" on a model with no photos.
    [Fact]
    public void Only_images_count_towards_a_folder_being_ready()
    {
        AddFile("L7", "front.jpg");
        AddFile("L7", "notes.txt");
        AddFile("L7", "thumbs.db");
        var photos = NewLibrary();

        var l7 = photos.GetAllFolders().Single(f => f.ModelKey == "L7");

        Assert.Equal(1, l7.ImageCount);
    }

    // ── Listing a model's photos ────────────────────────────────────────────

    [Fact]
    public void Photo_urls_are_web_paths_under_the_model_folder()
    {
        AddFile("S19j_Pro", "front.jpg");
        var photos = NewLibrary();

        Assert.Equal(["/photos/S19j_Pro/front.jpg"], photos.ListPhotoUrls("S19j_Pro"));
    }

    [Fact]
    public void A_model_with_no_folder_yet_lists_no_photos()
    {
        var photos = NewLibrary();

        Assert.Empty(photos.ListPhotoUrls("Whatsminer_M30S"));
    }

    // A .txt or a .db in the folder would become a broken image on a live listing.
    [Fact]
    public void Files_that_are_not_images_never_reach_a_listing()
    {
        AddFile("L7", "back.png");
        AddFile("L7", "receipt.pdf");
        AddFile("L7", "notes.txt");
        var photos = NewLibrary();

        Assert.Equal(["/photos/L7/back.png"], photos.ListPhotoUrls("L7"));
    }

    // Photo order is the listing's gallery order, and the first one is the one buyers see in search
    // results. It has to be the same on every unit of the model, not whatever the disk returns.
    [Fact]
    public void Photos_come_back_in_a_fixed_order()
    {
        AddFile("L7", "3_back.jpg");
        AddFile("L7", "1_front.jpg");
        AddFile("L7", "2_side.jpg");
        var photos = NewLibrary();

        Assert.Equal(
            ["/photos/L7/1_front.jpg", "/photos/L7/2_side.jpg", "/photos/L7/3_back.jpg"],
            photos.ListPhotoUrls("L7"));
    }

    // ── Saving ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_saved_photo_joins_the_model_set_with_its_bytes_intact()
    {
        var photos = NewLibrary();

        var url = await photos.SavePhotoAsync("S19j_Pro", Bytes("real-photo"), "jpg");

        Assert.Contains(url, photos.ListPhotoUrls("S19j_Pro"));
        Assert.Equal("real-photo", File.ReadAllText(Path.Combine(PhotosDir("S19j_Pro"), Path.GetFileName(url))));
    }

    [Fact]
    public async Task Saving_into_a_model_that_has_no_folder_yet_creates_it()
    {
        var photos = NewLibrary();

        await photos.SavePhotoAsync("Whatsminer_M30S", Bytes("photo"), "png");

        Assert.True(Directory.Exists(PhotosDir("Whatsminer_M30S")));
        Assert.Single(photos.ListPhotoUrls("Whatsminer_M30S"));
    }

    // An extension the library will not serve back is an upload the seller can see saved and then
    // never see again. Storing it as png keeps it in the set it was added to.
    [Fact]
    public async Task An_extension_the_library_does_not_serve_is_stored_as_png()
    {
        var photos = NewLibrary();

        var url = await photos.SavePhotoAsync("L7", Bytes("photo"), "exe");

        Assert.EndsWith(".png", url);
        Assert.Contains(url, photos.ListPhotoUrls("L7"));
    }

    [Fact]
    public async Task A_leading_dot_or_upper_case_extension_is_still_the_extension_it_names()
    {
        var photos = NewLibrary();

        var url = await photos.SavePhotoAsync("L7", Bytes("photo"), ".JPG");

        Assert.EndsWith(".jpg", url);
        Assert.Contains(url, photos.ListPhotoUrls("L7"));
    }

    // ── Nothing escapes the photos folder ───────────────────────────────────

    [Fact]
    public void A_model_key_that_climbs_out_of_the_photos_folder_is_flattened()
    {
        var photos = NewLibrary();

        var key = photos.CreateFolder("../../escaped");

        Assert.Equal("escaped", key);
        Assert.True(Directory.Exists(PhotosDir("escaped")));
        Assert.False(Directory.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public async Task A_photo_saved_under_a_climbing_key_still_lands_inside_the_photos_folder()
    {
        var photos = NewLibrary();

        var url = await photos.SavePhotoAsync("..\\..\\escaped", Bytes("photo"), "png");

        Assert.StartsWith("/photos/escaped/", url);
        Assert.False(Directory.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public void An_empty_model_key_is_refused_rather_than_writing_into_the_photos_root()
    {
        var photos = NewLibrary();

        Assert.Throws<ArgumentException>(() => photos.CreateFolder(""));
    }

    // ── Deleting ────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_a_photo_takes_it_out_of_the_model_set()
    {
        AddFile("L7", "bad-shot.jpg");
        AddFile("L7", "good-shot.jpg");
        var photos = NewLibrary();

        Assert.True(photos.DeletePhoto("L7", "bad-shot.jpg"));
        Assert.Equal(["/photos/L7/good-shot.jpg"], photos.ListPhotoUrls("L7"));
    }

    [Fact]
    public void Deleting_a_photo_that_is_not_there_is_refused()
    {
        var photos = NewLibrary();

        Assert.False(photos.DeletePhoto("L7", "never-existed.jpg"));
    }

    // Delete is reachable from an endpoint, so the file name is seller input. Only library images
    // may go, and only the ones inside the model's own folder.
    [Fact]
    public void Delete_will_not_remove_a_file_that_is_not_a_library_image()
    {
        AddFile("L7", "receipts.txt", "keep me");
        var photos = NewLibrary();

        Assert.False(photos.DeletePhoto("L7", "receipts.txt"));
        Assert.True(File.Exists(Path.Combine(PhotosDir("L7"), "receipts.txt")));
    }

    [Fact]
    public void Delete_cannot_reach_out_of_the_model_folder_it_was_given()
    {
        AddFile("L7", "keeper.jpg", "another model's photo");
        AddFile("S19j_Pro", "decoy.jpg");
        var photos = NewLibrary();

        Assert.False(photos.DeletePhoto("S19j_Pro", "../L7/keeper.jpg"));
        Assert.True(File.Exists(Path.Combine(PhotosDir("L7"), "keeper.jpg")));
    }

    // ── Matching a listing to a model's photos ──────────────────────────────

    // The whole loop, the way the seller runs it: bucket a model, photograph it once, then list the
    // next identical unit and have its photos already there.
    [Fact]
    public async Task A_model_photographed_once_supplies_the_next_unit_listed()
    {
        var photos = NewLibrary();
        var key = photos.DeriveModelKey("Bitmain", "Antminer S19j Pro", null);
        var url = await photos.SavePhotoAsync(key, Bytes("photo"), "jpg");

        var match = photos.ResolveForListing("Antminer S19j Pro", "Bitmain Antminer S19j Pro 104TH Bitcoin Miner");

        Assert.NotNull(match);
        Assert.Equal(key, match!.ModelKey);
        Assert.Equal([url], match.PhotoUrls);
        Assert.Equal(PhotoLibrary.RepresentativeDisclosure, match.Disclosure);
    }

    // Empty seed folders exist from the first run. Offering a match backed by no photos would put a
    // disclosure line on a listing with nothing to disclose.
    [Fact]
    public void A_model_with_a_folder_but_no_photos_is_not_a_match()
    {
        var photos = NewLibrary();
        photos.GetDefaultFolders();

        Assert.Null(photos.ResolveForListing("Antminer S19j Pro", "Antminer S19j Pro"));
    }

    [Fact]
    public void A_listing_with_no_model_and_no_title_gets_no_photos()
    {
        AddFile("L7", "front.jpg");
        var photos = NewLibrary();

        Assert.Null(photos.ResolveForListing(null, null));
        Assert.Null(photos.ResolveForListing("   ", "  "));
    }

    [Fact]
    public void A_listing_from_a_category_the_library_has_never_seen_gets_no_photos()
    {
        AddFile("L7", "front.jpg");
        var photos = NewLibrary();

        Assert.Null(photos.ResolveForListing("PlayStation 5", "Sony PlayStation 5 Disc Edition Console"));
    }

    [Fact]
    public void The_folder_that_names_the_machine_most_exactly_wins()
    {
        AddFile("S19_95TH", "front.jpg");
        AddFile("antminer_s19j_pro", "front.jpg");
        var photos = NewLibrary();

        var match = photos.ResolveForListing("Antminer S19j Pro", "Antminer S19j Pro 104TH");

        Assert.Equal("antminer_s19j_pro", match?.ModelKey);
    }

    // "s19" is not "s19j". A substring test made the 95TH folder claim an S19j Pro listing, which
    // ships one machine's photos on another machine's listing under a line saying they represent
    // the unit being sent.
    [Fact]
    public void A_folder_key_is_not_matched_by_a_word_it_is_only_part_of()
    {
        AddFile("S19_95TH", "front.jpg");
        var photos = NewLibrary();

        Assert.Null(photos.ResolveForListing("Antminer S19j Pro", "Antminer S19j Pro 104TH Miner"));
    }

    // "Antminer S19" fits the 95TH folder and the 110TH folder exactly as well. Picking either one
    // is a coin flip over which hashrate the buyer is shown; asking the seller is not.
    [Fact]
    public void When_two_models_fit_equally_well_no_photos_are_offered()
    {
        AddFile("S19_95TH", "front.jpg");
        AddFile("S19_110TH", "front.jpg");
        var photos = NewLibrary();

        Assert.Null(photos.ResolveForListing("Antminer S19", null));
    }

    // The disclosure is the reason reusing a photo is allowed at all. It has to keep saying both
    // halves: the photo stands in for the unit, and the unit itself was tested.
    [Fact]
    public void The_disclosure_says_the_photos_stand_in_and_the_unit_was_tested()
    {
        Assert.Contains("representative of the unit you will receive", PhotoLibrary.RepresentativeDisclosure);
        Assert.Contains("individually tested", PhotoLibrary.RepresentativeDisclosure);
    }
}
