using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The revise payload sent for a listing imported from eBay (an ItemID, no Inventory API offerId).
///
/// These exist because of a bug that was invisible from every direction a test normally looks:
/// the editor built a full request, the code dropped everything except price and quantity on the
/// way out, eBay answered Ack=Success because the smaller call was itself valid, and the UI
/// reported "Published to eBay live". Nothing threw, nothing logged an error, and the listing on
/// eBay never changed. The only place the failure was ever visible was in the bytes that went out,
/// so that is what these assert.
/// </summary>
public class EbayReviseListingTests
{
    private static UpdateListingRequest FullEdit() => new()
    {
        ListingId            = "286476289055",
        Title                = "Antminer S19 95TH/s Bitcoin Miner | Low Power 2800W Tune",
        Description          = "Fully tested, hashing at rated speed.",
        Condition            = "USED_EXCELLENT",
        ConditionDescription = "Tested, cleaned, light cosmetic wear.",
        CategoryId           = "175673",
        Price                = 499.99m,
        Quantity             = 3,
        ItemSpecifics        = new Dictionary<string, string>
        {
            ["Brand"] = "Bitmain",
            ["Model"] = "Antminer S19",
        },
    };

    [Fact]
    public void Carries_every_edited_field_not_just_price_and_quantity()
    {
        var xml = EbayService.BuildReviseFixedPriceItemXml(FullEdit(), out var changed);

        // The regression itself: these five all used to be dropped.
        Assert.Contains("<Title>Antminer S19 95TH/s Bitcoin Miner | Low Power 2800W Tune</Title>", xml);
        Assert.Contains("Fully tested, hashing at rated speed.", xml);
        Assert.Contains("<ConditionID>3000</ConditionID>", xml);
        Assert.Contains("<ConditionDescription>Tested, cleaned, light cosmetic wear.</ConditionDescription>", xml);
        Assert.Contains("<CategoryID>175673</CategoryID>", xml);
        Assert.Contains("<Name>Brand</Name><Value>Bitmain</Value>", xml);

        // ...alongside the two that always worked.
        Assert.Contains("<StartPrice>499.99</StartPrice>", xml);
        Assert.Contains("<Quantity>3</Quantity>", xml);

        Assert.Contains("<ItemID>286476289055</ItemID>", xml);
        Assert.Contains("ReviseFixedPriceItemRequest", xml);

        // And the seller is told each of them by name, so a shrunken payload can never again be
        // reported as a full edit.
        Assert.Contains("title", changed);
        Assert.Contains("condition", changed);
        Assert.Contains("description", changed);
        Assert.Contains("price", changed);
        Assert.Contains("quantity", changed);
    }

    [Fact]
    public void Never_emits_an_empty_PictureDetails_which_would_strip_the_listings_photos()
    {
        var req = FullEdit();
        req.ImageUrls = [];

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out var changed);

        Assert.DoesNotContain("<PictureDetails>", xml);
        Assert.DoesNotContain(changed, c => c.Contains("photo"));
    }

    [Fact]
    public void Sends_photos_when_there_are_real_public_urls()
    {
        var req = FullEdit();
        req.ImageUrls = ["https://example.com/a.jpg", "", "not-a-url", "https://example.com/b.jpg"];

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out var changed);

        Assert.Contains("<PictureURL>https://example.com/a.jpg</PictureURL>", xml);
        Assert.Contains("<PictureURL>https://example.com/b.jpg</PictureURL>", xml);
        Assert.DoesNotContain("not-a-url", xml);
        Assert.Contains("2 photos", changed);
    }

    [Fact]
    public void Never_sends_ebays_own_thumbnail_back_which_would_wipe_the_real_photo_set()
    {
        // Exactly what importing a live listing returns: one URL, and it is the 140px gallery
        // thumbnail. Sending it back replaces every real photograph with a postage stamp.
        var req = FullEdit();
        req.ImageUrls = ["https://i.ebayimg.com/images/g/pMYAAeSwIOtpVBOS/s-l140.png"];

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out var changed);

        Assert.DoesNotContain("<PictureDetails>", xml);
        Assert.DoesNotContain("ebayimg.com", xml);
        Assert.DoesNotContain(changed, c => c.Contains("photo"));
    }

    [Fact]
    public void Keeps_seller_supplied_photos_while_dropping_the_ebay_hosted_ones()
    {
        var req = FullEdit();
        req.ImageUrls =
        [
            "https://i.ebayimg.com/images/g/pMYAAeSwIOtpVBOS/s-l140.png",
            "https://example.com/seller-photo.jpg",
        ];

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out var changed);

        Assert.Contains("<PictureURL>https://example.com/seller-photo.jpg</PictureURL>", xml);
        Assert.DoesNotContain("ebayimg.com", xml);
        Assert.Contains("1 photo", changed);
    }

    [Fact]
    public void Omits_blank_fields_so_a_partial_edit_leaves_the_rest_of_the_listing_alone()
    {
        var req = new UpdateListingRequest
        {
            ListingId = "286476289055",
            Price     = 449.00m,
            Quantity  = 1,
            Condition = "",
        };

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out var changed);

        // Omitted, not blank: in a Revise call an absent field means "leave it as it is", while a
        // present-but-empty one overwrites what the listing already has.
        Assert.DoesNotContain("<Title>", xml);
        Assert.DoesNotContain("<Description>", xml);
        Assert.DoesNotContain("<ConditionID>", xml);
        Assert.DoesNotContain("<ItemSpecifics>", xml);
        Assert.DoesNotContain("<PrimaryCategory>", xml);

        Assert.Contains("<StartPrice>449.00</StartPrice>", xml);
        Assert.Equal(["price", "quantity"], changed);
    }

    [Fact]
    public void Escapes_xml_so_a_title_with_an_ampersand_cannot_break_the_request()
    {
        var req = FullEdit();
        req.Title = "Miner & PSU <combo>";

        var xml = EbayService.BuildReviseFixedPriceItemXml(req, out _);

        Assert.DoesNotContain("<combo>", xml);
        Assert.Contains("&amp;", xml);
        // Still a parseable document, which is the point of escaping it.
        System.Xml.Linq.XDocument.Parse(xml);
    }

    [Fact]
    public void Builds_a_well_formed_document_for_a_full_edit()
    {
        var xml = EbayService.BuildReviseFixedPriceItemXml(FullEdit(), out _);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("ReviseFixedPriceItemRequest", doc.Root!.Name.LocalName);
    }
}
