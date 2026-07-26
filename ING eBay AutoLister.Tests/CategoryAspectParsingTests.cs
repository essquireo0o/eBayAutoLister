using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Reading eBay's get_item_aspects_for_category response. Getting a flag wrong here is not a
// cosmetic bug: `aspectRequired` decides what blocks a publish, and `aspectMode` decides whether
// the app is allowed to call a seller's typed value invalid. Both fixtures below are the shapes
// eBay actually returned during this session, from a live category.
public class CategoryAspectParsingTests
{
    private const string LiveSample = """
    {
      "aspects": [
        {
          "localizedAspectName": "Brand",
          "aspectConstraint": {
            "aspectDataType": "STRING",
            "itemToAspectCardinality": "SINGLE",
            "aspectMode": "FREE_TEXT",
            "aspectRequired": true,
            "aspectUsage": "RECOMMENDED",
            "aspectEnabledForVariations": false,
            "aspectMaxLength": 65
          },
          "aspectValues": [
            { "localizedValue": "Bitmain" },
            { "localizedValue": "MicroBT" }
          ]
        },
        {
          "localizedAspectName": "Country of Origin",
          "aspectConstraint": {
            "itemToAspectCardinality": "SINGLE",
            "aspectMode": "SELECTION_ONLY",
            "aspectRequired": false,
            "aspectUsage": "OPTIONAL"
          },
          "aspectValues": [
            { "localizedValue": "China" },
            { "localizedValue": "United States" }
          ]
        },
        {
          "localizedAspectName": "Hash Algorithm",
          "aspectConstraint": {
            "itemToAspectCardinality": "MULTI",
            "aspectMode": "FREE_TEXT",
            "aspectRequired": false,
            "aspectUsage": "RECOMMENDED"
          },
          "aspectValues": [ { "localizedValue": "SHA-256" } ]
        },
        {
          "localizedAspectName": "Model",
          "aspectConstraint": {
            "itemToAspectCardinality": "SINGLE",
            "aspectMode": "FREE_TEXT",
            "aspectRequired": false,
            "aspectUsage": "OPTIONAL"
          }
        }
      ]
    }
    """;

    [Fact]
    public void Required_recommended_and_optional_are_read_off_the_constraint()
    {
        var aspects = EbayService.ParseAspects(LiveSample);

        var brand = aspects.Single(a => a.Name == "Brand");
        Assert.True(brand.Required);
        Assert.Equal(65, brand.MaxLength);

        Assert.True(aspects.Single(a => a.Name == "Hash Algorithm").Recommended);
        Assert.False(aspects.Single(a => a.Name == "Model").Required);
        Assert.False(aspects.Single(a => a.Name == "Model").Recommended);
    }

    [Fact]
    public void Cardinality_and_mode_are_read_off_the_constraint()
    {
        var aspects = EbayService.ParseAspects(LiveSample);

        Assert.True(aspects.Single(a => a.Name == "Hash Algorithm").MultiSelect);
        Assert.False(aspects.Single(a => a.Name == "Brand").MultiSelect);
        Assert.True(aspects.Single(a => a.Name == "Country of Origin").SelectionOnly);
        Assert.False(aspects.Single(a => a.Name == "Brand").SelectionOnly);
    }

    [Fact]
    public void Only_a_selection_only_value_list_is_treated_as_the_whole_set()
    {
        // A FREE_TEXT aspect's values are eBay's popular suggestions. Treating that sample as
        // exhaustive would have the app rejecting a seller's real value that eBay accepts.
        var aspects = EbayService.ParseAspects(LiveSample);
        Assert.False(aspects.Single(a => a.Name == "Brand").ValuesAreComplete);
        Assert.True(aspects.Single(a => a.Name == "Country of Origin").ValuesAreComplete);
    }

    [Fact]
    public void An_aspect_with_no_values_cannot_be_selection_only()
    {
        // Nothing to validate against, so calling it fixed-list would reject every value.
        var json = """
        {"aspects":[{"localizedAspectName":"Model",
          "aspectConstraint":{"aspectMode":"SELECTION_ONLY","aspectRequired":true}}]}
        """;
        var aspect = Assert.Single(EbayService.ParseAspects(json));
        Assert.False(aspect.SelectionOnly);
        Assert.True(aspect.Required);
    }

    [Fact]
    public void Aspects_come_back_in_the_order_the_seller_should_work_through_them()
    {
        var names = EbayService.ParseAspects(LiveSample).Select(a => a.Name).ToList();
        Assert.Equal("Brand", names[0]);                 // required
        Assert.Equal("Hash Algorithm", names[1]);        // recommended
        Assert.Equal(["Country of Origin", "Model"], names.Skip(2)); // the rest, alphabetical
    }

    [Fact]
    public void An_unexpected_body_yields_no_aspects_rather_than_an_exception()
    {
        Assert.Empty(EbayService.ParseAspects("{}"));
        Assert.Empty(EbayService.ParseAspects("""{"aspects":null}"""));
        Assert.Empty(EbayService.ParseAspects("""{"aspects":[{"aspectConstraint":{}}]}"""));
    }

    // ── Error reporting ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_parent_category_is_named_as_the_problem_not_reported_as_HTTP_400()
    {
        // Observed live: browsing the category tree and stopping at a parent gets errorId 62009.
        // eBay refuses the publish for the same reason, so this is a real blocker caught early.
        var body = """
        {"errors":[{"errorId":62009,"domain":"API_TAXONOMY","category":"REQUEST",
          "message":"The specified category ID must be a leaf category."}]}
        """;
        var msg = EbayService.DescribeAspectFailure(body, 400, "175673");
        Assert.Contains("parent category", msg);
        Assert.Contains("175673", msg);
        Assert.DoesNotContain("400", msg);
    }

    [Fact]
    public void Any_other_eBay_error_is_passed_through_in_eBays_own_words()
    {
        var body = """{"errors":[{"errorId":1,"message":"Something specific went wrong."}]}""";
        Assert.Contains("Something specific went wrong.", EbayService.DescribeAspectFailure(body, 400, "1"));
    }

    [Fact]
    public void An_auth_failure_points_at_the_connection()
    {
        Assert.Contains("connection", EbayService.DescribeAspectFailure("not json", 401, "1"));
    }

    [Fact]
    public void An_unparseable_body_still_produces_a_message()
    {
        var msg = EbayService.DescribeAspectFailure("<html>gateway error</html>", 502, "179171");
        Assert.Contains("502", msg);
        Assert.Contains("179171", msg);
    }
}
