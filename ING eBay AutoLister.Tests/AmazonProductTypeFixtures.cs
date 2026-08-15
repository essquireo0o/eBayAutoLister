namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Amazon-shaped responses for the Product Type Definitions API.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are reconstructions, not captures.</b> No response here came off Amazon's wire — this
/// deployment cannot obtain an Amazon access token at all (the stored LWA client secret is a
/// placeholder note and no seller has authorised the application), so there is no live response to
/// record. What is faithful is the SHAPE: every construct below is one Amazon's published
/// Product Type Definition Meta-Schema v1 defines — the array-of-object envelope with its
/// <c>selectors</c>, <c>maxUniqueItems</c>, <c>enumNames</c>, <c>maxUtf8ByteLength</c>,
/// <c>editable</c>, <c>hidden</c>, local <c>$ref</c>s into <c>$defs</c>, and requirements expressed
/// as a root-level <c>anyOf</c>.
/// </para>
/// <para>
/// So what these prove is that the parser reads Amazon's grammar correctly. They do NOT prove that
/// <c>BLUETOOTH_SPEAKER</c>'s real required list is the one below; only Amazon can say that, and
/// only in production — the sandbox replays a fixed product type set regardless of the keywords.
/// Anywhere that distinction could be lost, <see cref="ING_eBay_AutoLister.Services.AmazonSandboxNotice"/>
/// states it in the answer itself.
/// </para>
/// </remarks>
public static class AmazonProductTypeFixtures
{
    /// <summary>What <c>searchDefinitionsProductTypes?keywords=bluetooth,speaker</c> answers with.</summary>
    public const string SearchResponse = """
    {
      "productTypes": [
        { "name": "SPEAKERS",            "displayName": "Speakers",           "marketplaceIds": ["ATVPDKIKX0DER"] },
        { "name": "BLUETOOTH_SPEAKER",   "displayName": "Bluetooth Speaker",  "marketplaceIds": ["ATVPDKIKX0DER"] },
        { "name": "HOME_THEATER_SYSTEM", "displayName": "Home Theater System","marketplaceIds": ["ATVPDKIKX0DER"] }
      ],
      "productTypeVersion": "UNVERSIONED"
    }
    """;

    /// <summary>The sandbox's answer to the same call: static, and about luggage.</summary>
    public const string SandboxSearchResponse = """
    {
      "productTypes": [
        { "name": "LUGGAGE", "displayName": "Luggage", "marketplaceIds": ["ATVPDKIKX0DER"] }
      ],
      "productTypeVersion": "UNVERSIONED"
    }
    """;

    /// <summary>
    /// What <c>getDefinitionsProductType</c> answers with. Note that the schema is NOT in it — the
    /// document lives behind a short-lived pre-signed link, which is what makes caching by version
    /// worth doing.
    /// </summary>
    public const string DefinitionResponse = """
    {
      "metaSchema": {
        "link": { "resource": "https://definitions.s3.us-east-1.amazonaws.com/meta/v1.json", "verb": "GET" },
        "checksum": "QFQDmPwMARO7vwMEyLhOtw=="
      },
      "schema": {
        "link": { "resource": "https://definitions.s3.us-east-1.amazonaws.com/BLUETOOTH_SPEAKER.json?X-Amz-Expires=300", "verb": "GET" },
        "checksum": "TBr8ubaxXrUyay9hmxUXUw=="
      },
      "requirements": "LISTING",
      "requirementsEnforced": "ENFORCED",
      "propertyGroups": {
        "product_identity": {
          "title": "Product Identity",
          "description": "Information to uniquely identify your product",
          "propertyNames": ["item_name", "brand", "externally_assigned_product_identifier",
                            "merchant_suggested_asin", "country_of_origin"]
        },
        "offer": {
          "title": "Offer",
          "description": "Product Offer",
          "propertyNames": ["purchasable_offer", "condition_type", "list_price"]
        },
        "product_details": {
          "title": "Product Details",
          "propertyNames": ["bullet_point", "product_description", "item_dimensions", "color",
                            "number_of_items", "batteries_required", "supplier_declared_dg_hz_regulation"]
        }
      },
      "locale": "en_US",
      "marketplaceIds": ["ATVPDKIKX0DER"],
      "productType": "BLUETOOTH_SPEAKER",
      "displayName": "Bluetooth Speaker",
      "productTypeVersion": { "version": "U8L4z4Ud95N16tZlR7rsmbQ==", "latest": true, "releaseCandidate": false }
    }
    """;

    /// <summary>
    /// The schema document itself, in Amazon's grammar.
    /// </summary>
    /// <remarks>
    /// Every construct the parser has to survive is present: the array-of-object envelope, a
    /// repeatable attribute, a closed list with display labels, a boolean, an integer, a local
    /// <c>$ref</c>, two levels of genuine composite (a scheduled price), a UTF-8 byte length that
    /// binds tighter than the character length, a hidden non-editable attribute, and a requirement
    /// expressed as an <c>anyOf</c> rather than a <c>required</c>.
    /// </remarks>
    public const string BluetoothSpeakerSchema = """
    {
      "$schema": "https://json-schema.org/draft/2019-09/schema",
      "$id": "https://definitions.s3.amazonaws.com/BLUETOOTH_SPEAKER.json",
      "type": "object",
      "required": [
        "item_name", "brand", "bullet_point", "product_description", "condition_type",
        "batteries_required", "supplier_declared_dg_hz_regulation", "country_of_origin",
        "list_price", "purchasable_offer"
      ],
      "anyOf": [
        { "required": ["externally_assigned_product_identifier"] },
        { "required": ["merchant_suggested_asin"] }
      ],
      "$defs": {
        "country_code": {
          "type": "string",
          "title": "Country of Origin",
          "enum": ["US", "CN", "VN", "MX"],
          "enumNames": ["United States", "China", "Vietnam", "Mexico"]
        },
        "language_tag": { "type": "string", "default": "en_US" },
        "marketplace_id": { "type": "string" }
      },
      "properties": {
        "item_name": {
          "title": "Title",
          "description": "The title of the product, as it appears on the detail page.",
          "editable": true,
          "hidden": false,
          "examples": ["JBL Flip 6 Portable Bluetooth Speaker, Black"],
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "title": "Title", "type": "string", "maxLength": 200, "maxUtf8ByteLength": 180 },
              "language_tag":   { "$ref": "#/$defs/language_tag" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            },
            "additionalProperties": false
          },
          "minItems": 1,
          "maxItems": 20,
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id", "language_tag"]
        },
        "brand": {
          "title": "Brand Name",
          "description": "The brand name of the product.",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "title": "Brand Name", "type": "string", "maxLength": 50 },
              "language_tag":   { "$ref": "#/$defs/language_tag" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id", "language_tag"]
        },
        "bullet_point": {
          "title": "Key Product Features",
          "description": "A short description of a key feature. Up to five may be supplied.",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "string", "maxLength": 500 },
              "language_tag":   { "$ref": "#/$defs/language_tag" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxItems": 25,
          "maxUniqueItems": 5,
          "selectors": ["marketplace_id", "language_tag"]
        },
        "product_description": {
          "title": "Product Description",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "string", "maxLength": 2000 },
              "language_tag":   { "$ref": "#/$defs/language_tag" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id", "language_tag"]
        },
        "condition_type": {
          "title": "Condition",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value": {
                "type": "string",
                "enum": ["new_new", "used_like_new", "used_very_good", "used_good", "used_acceptable"],
                "enumNames": ["New", "Used - Like New", "Used - Very Good", "Used - Good", "Used - Acceptable"]
              },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "batteries_required": {
          "title": "Batteries Required",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "boolean" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "number_of_items": {
          "title": "Number of Items",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "integer", "minimum": 1 },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "supplier_declared_dg_hz_regulation": {
          "title": "Dangerous Goods Regulations",
          "description": "Select all that apply. Lithium batteries in a speaker are regulated.",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value": {
                "type": "string",
                "enum": ["not_applicable", "ghs", "storage", "transportation", "unknown"],
                "enumNames": ["Not Applicable", "GHS", "Storage", "Transportation", "Unknown"]
              },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 5,
          "selectors": ["marketplace_id"]
        },
        "country_of_origin": {
          "title": "Country of Origin",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "$ref": "#/$defs/country_code" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "list_price": {
          "title": "List Price",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value", "currency"],
            "properties": {
              "value":          { "title": "Amount", "type": "number", "minimum": 0 },
              "currency":       { "title": "Currency", "type": "string", "enum": ["USD", "CAD", "MXN"] },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "purchasable_offer": {
          "title": "Purchasable Offer",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["currency", "our_price"],
            "properties": {
              "currency": { "title": "Currency", "type": "string", "enum": ["USD"] },
              "our_price": {
                "title": "Our Price",
                "type": "array",
                "items": {
                  "type": "object",
                  "required": ["schedule"],
                  "properties": {
                    "schedule": {
                      "title": "Schedule",
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["value_with_tax"],
                        "properties": {
                          "value_with_tax": { "title": "Price", "type": "number", "minimum": 0 },
                          "start_at":       { "title": "Start At", "type": "string" }
                        }
                      }
                    }
                  }
                }
              },
              "audience":       { "title": "Audience", "type": "string", "enum": ["ALL", "B2B"] },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "selectors": ["marketplace_id", "audience"]
        },
        "externally_assigned_product_identifier": {
          "title": "External Product ID",
          "description": "A UPC, EAN or GTIN for the product.",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value", "type"],
            "properties": {
              "value":          { "title": "External Product ID", "type": "string", "maxLength": 40 },
              "type":           { "title": "Type", "type": "string", "enum": ["ean", "gtin", "upc"] },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "merchant_suggested_asin": {
          "title": "Merchant Suggested ASIN",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "string", "maxLength": 10, "minLength": 10 },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "color": {
          "title": "Color",
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "string", "maxLength": 50 },
              "language_tag":   { "$ref": "#/$defs/language_tag" },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id", "language_tag"]
        },
        "item_dimensions": {
          "title": "Item Dimensions",
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "length": {
                "type": "object",
                "required": ["value", "unit"],
                "properties": {
                  "value": { "title": "Length", "type": "number" },
                  "unit":  { "title": "Unit", "type": "string", "enum": ["inches", "centimeters"] }
                }
              },
              "width": {
                "type": "object",
                "required": ["value", "unit"],
                "properties": {
                  "value": { "title": "Width", "type": "number" },
                  "unit":  { "title": "Unit", "type": "string", "enum": ["inches", "centimeters"] }
                }
              },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        },
        "item_type_keyword": {
          "title": "Item Type Keyword",
          "description": "Amazon's own classification keyword. Set by Amazon, not by the seller.",
          "editable": false,
          "hidden": true,
          "type": "array",
          "items": {
            "type": "object",
            "required": ["value"],
            "properties": {
              "value":          { "type": "string", "maxLength": 50 },
              "marketplace_id": { "$ref": "#/$defs/marketplace_id" }
            }
          },
          "maxUniqueItems": 1,
          "selectors": ["marketplace_id"]
        }
      }
    }
    """;
}
