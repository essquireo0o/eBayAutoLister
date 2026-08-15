using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A listing this app actually produced, kept exactly as it was saved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlike <see cref="AmazonProductTypeFixtures"/>, this one is a capture, not a reconstruction.</b>
/// It is a verbatim copy of a draft file out of the seller's own eBayListing folder — the AI read the
/// photos and the source page, filled in the title, the brand, the part number, the price, the box,
/// the HTML description and thirteen Item Specifics, and the seller saved it. Nothing in it was
/// written for this test.
/// </para>
/// <para>
/// That is the point. The acceptance question for this phase is whether the pipeline that already
/// fills an eBay listing can fill an Amazon one, and a draft composed to make the mapper look good
/// would not answer it. This one was not: it has no UPC, which is exactly the case where Amazon
/// cannot be satisfied and the honest output is a blocked listing rather than a plausible barcode.
/// </para>
/// <para>
/// Three identifiers were replaced with zeroes — the eBay business-policy ids, which are account
/// credentials and have nothing to do with Amazon attributes. Every field the mapper reads is
/// untouched.
/// </para>
/// </remarks>
public static class AmazonListingFillFixtures
{
    /// <summary>The draft file as it sits on disk, in DraftStore's own format.</summary>
    public const string RealDraftFile = """
    {
      "filename": "Bitaxe_NerdQaxe_48THs_BM1370_Bitcoin_Solo_Miner_SHA-256_ASIC.json",
      "title": "Bitaxe NerdQaxe++ 4.8TH/s BM1370 Bitcoin Solo Miner SHA-256 ASIC w/ Fan",
      "savedAt": "2026-07-15T20:11:29.372Z",
      "data": {
        "title": "Bitaxe NerdQaxe++ 4.8TH/s BM1370 Bitcoin Solo Miner SHA-256 ASIC w/ Fan",
        "subtitle": "",
        "category": "",
        "categoryId": "179171",
        "secondaryCategoryId": "",
        "condition": "NEW",
        "conditionDescription": "",
        "brand": "NerdQaxe",
        "mpn": "NerdQaxe++",
        "upc": "",
        "ean": "",
        "isbn": "",
        "description": "<div style=\"font-family:Arial,sans-serif;max-width:760px;margin:0 auto;color:#222;font-size:15px;line-height:1.75\">\n\n  <h2 style=\"margin:0 0 10px;font-size:20px;font-weight:700;color:#0d5c63\">NerdQaxe++ 4.8TH/s Bitcoin Solo Miner — Quad BM1370 ASIC | SHA-256</h2>\n\n  <p style=\"margin:0 0 16px;font-size:15px\">The NerdQaxe++ is a high-efficiency open-source Bitcoin solo miner powered by four Bitmain BM1370 ASIC chips delivering up to 4.8 TH/s on the SHA-256 algorithm. Built on the popular Bitaxe design, this desktop miner is ideal for solo lottery mining, home node hobbyists, and crypto enthusiasts who want a quiet, low-power alternative to industrial rigs. It features a color IPS status display, WiFi monitoring, and a large 92mm cooling fan with heatsink.</p>\n\n  <h2 style=\"margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63\">Key Specifications</h2>\n  <ul style=\"margin:0 0 18px 18px;padding:0;font-size:14px\">\n    <li style=\"margin-bottom:5px\">ASIC Chips: 4x Bitmain BM1370</li>\n    <li style=\"margin-bottom:5px\">Hashrate: Up to 4.8 TH/s</li>\n    <li style=\"margin-bottom:5px\">Algorithm: SHA-256 (Bitcoin / BTC)</li>\n    <li style=\"margin-bottom:5px\">Power Draw: Approx 75-82W typical</li>\n    <li style=\"margin-bottom:5px\">Input Voltage: 12V DC barrel connector</li>\n    <li style=\"margin-bottom:5px\">Display: Color IPS TFT status screen (LILYGO)</li>\n    <li style=\"margin-bottom:5px\">Connectivity: WiFi 2.4GHz, USB-C config</li>\n    <li style=\"margin-bottom:5px\">Cooling: 92mm fan with dual-tower heatsink</li>\n  </ul>\n\n  <h2 style=\"margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63\"></h2>\n  <ul style=\"margin:0 0 18px 18px;padding:0;font-size:14px\">\n    <li style=\"margin-bottom:5px\"></li>\n    <li style=\"margin-bottom:5px\">Features &amp; Benefits</li>\n    <li style=\"margin-bottom:5px\">Quad BM1370 layout on the NerdQaxe++ boosts hashrate to 4.8 TH/s while staying under 85W</li>\n    <li style=\"margin-bottom:5px\">Built-in color display shows live hashrate, temperature, voltage, RPM and pool data</li>\n  </ul>\n\n  <h2 style=\"margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63\">Open-source AxeOS firmware with browser dashboard for pool setup and monitoring</h2>\n  <p style=\"margin:0 0 8px;font-size:14px\"></p>\n  <ul style=\"margin:0 0 18px 18px;padding:0;font-size:14px\">\n    <li style=\"margin-bottom:5px\">Quiet desktop form factor perfect for solo lottery mining and learning ASIC hardware</li>\n    <li style=\"margin-bottom:5px\"></li>\n    <li style=\"margin-bottom:5px\">Condition &amp; What's Included</li>\n  </ul>\n\n  <h2 style=\"margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63\">Brand new, fully assembled and tested NerdQaxe++ miner as shown in photos.</h2>\n  <p style=\"margin:0 0 18px;font-size:14px\">1x NerdQaxe++ 4.8T miner with fan and heatsink</p>\n\n  <p style=\"margin:0;font-size:12px;color:#888;border-top:1px solid #eee;padding-top:10px\"></p>\n</div><p>Note: 12V DC power supply not included unless stated</p><p>Ships in protective packaging</p><p></p><p>Compatibility</p><p>Mines Bitcoin (BTC) and any SHA-256 coin. Compatible with public pools and solo mining setups including CKPool solo, public-pool.io, and Braiins. Configured via any modern web browser over WiFi. Requires a 12V DC power supply rated 8A or higher.</p><p>Ships securely packaged with tracking. See all photos for exact item condition.</p>",
        "price": 549.99,
        "quantity": 20,
        "quantityLimitPerBuyer": null,
        "bestOfferEnabled": true,
        "autoAcceptPrice": null,
        "autoDeclinePrice": null,
        "packageType": "MAILING_BOX",
        "weightLbs": 3,
        "weightOz": 8,
        "packageLengthIn": 8,
        "packageWidthIn": 6,
        "packageHeightIn": 6,
        "handlingTimeBusinessDays": 3,
        "itemLocationPostalCode": "",
        "itemLocationCountry": "US",
        "privateListing": false,
        "charityDonationPercentage": 0,
        "charityId": "",
        "listingFormat": "FIXED_PRICE",
        "durationDays": 7,
        "itemSpecifics": {
          "Brand": "NerdQaxe",
          "Model": "NerdQaxe++",
          "Type": "ASIC Bitcoin Miner",
          "Algorithm": "SHA-256",
          "Hashrate": "4.8 TH/s",
          "ASIC Chip": "4x BM1370",
          "Power Consumption": "75-82W",
          "Input Voltage": "12V DC",
          "Connectivity": "WiFi USB-C",
          "Compatible Currency": "Bitcoin BTC",
          "Cooling": "92mm Fan",
          "Color": "Black",
          "Country of Manufacture": "China"
        },
        "imageUrls": [
          "https://i.ebayimg.com/00/s/MTAwMFgxMDAw/z/PbAAAeSwPAZqV90q/$_12.PNG?set_id=880000500F",
          "https://i.ebayimg.com/00/s/ODAwWDgwMA==/z/13oAAeSwN09qV90r/$_12.JPG?set_id=880000500F",
          "https://i.ebayimg.com/00/s/MTAyNFgxMDI0/z/IJsAAeSwws5qV90r/$_12.JPG?set_id=880000500F"
        ],
        "fulfillmentPolicyId": "000000000000",
        "paymentPolicyId": "000000000000",
        "returnPolicyId": "000000000000"
      },
      "imageBase64": null,
      "mimeType": "image/jpeg",
      "visualDescription": "Compact desktop Bitcoin miner with a large square 92mm cooling fan mounted on an aluminum dual-tower heatsink, standing on two black rubber feet. A dark PCB circuit board sits on top holding capacitors and a small color IPS display showing NerdQaxe hashrate stats. A right-angle 12V DC barrel power cable and braided black cables connect at the top."
    }
    """;

    /// <summary>The draft, read the way the app reads it back off disk.</summary>
    public static PostListingRequest RealDraft()
    {
        var file = JsonSerializer.Deserialize<DraftFile>(
            RealDraftFile, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return file?.Data ?? throw new InvalidOperationException("The captured draft did not deserialize.");
    }

    /// <summary>The product type the schema fixture describes, parsed and ready to fill.</summary>
    public static AmazonProductTypeDefinition SpeakerDefinition()
    {
        var definition = AmazonDefinitionResponse.Parse(AmazonProductTypeFixtures.DefinitionResponse);
        definition.Attributes = AmazonSchemaParser.Parse(
            AmazonProductTypeFixtures.BluetoothSpeakerSchema,
            AmazonDefinitionResponse.ParseGroups(AmazonProductTypeFixtures.DefinitionResponse));
        return definition;
    }
}
