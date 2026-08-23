using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Keeping the prices that arrived when the model ran out of room.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to stop was measured on 2026-08-23: the Opportunity Finder asked one
/// model call to price sixty items against a 4096-token ceiling that adaptive thinking and web
/// search also spend from. The reply ended mid-object, the strict reader found no closing bracket,
/// and the log said <c>AiUnreadableReply after 3 attempt(s): AI response did not contain a JSON
/// array</c> — three identical retries of a deterministic truncation, and sixty prices thrown away.
/// </para>
/// <para>
/// The AI estimate is the only thing that can value a row while eBay's sold data is behind a login
/// wall, so a lost batch is a board that says "no sold data" on rows it could have priced. Fifty
/// seven prices beat none.
/// </para>
/// </remarks>
public class JsonSalvageTests
{
    private record Row(string ItemId, decimal Low, decimal High, string? Basis);

    private static List<Row> Parse(string json) =>
        JsonSerializer.Deserialize<List<Row>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    [Fact]
    public void A_reply_cut_off_mid_object_keeps_every_row_that_closed()
    {
        // Exactly the shape the model produces when it hits the ceiling: the last object simply
        // stops, with no closing brace and no closing bracket.
        const string truncated = """
            [{"itemId":"a","low":10,"high":25,"basis":"used drill"},
             {"itemId":"b","low":80,"high":150,"basis":"S19 miner"},
             {"itemId":"c","low":5,"high":1
            """;

        var rows = Parse(JsonSalvage.CompleteObjects(truncated));

        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0].ItemId);
        Assert.Equal(150m, rows[1].High);
    }

    [Fact]
    public void A_complete_reply_is_returned_untouched()
    {
        const string whole = """[{"itemId":"a","low":1,"high":2,"basis":"x"}]""";
        Assert.Equal(2, Parse(JsonSalvage.CompleteObjects(whole))[0].High);
        Assert.Single(Parse(JsonSalvage.CompleteObjects(whole)));
    }

    [Fact]
    public void Braces_and_quotes_inside_a_title_are_not_mistaken_for_structure()
    {
        // A real product name carries the characters this scanner is looking for. Getting this
        // wrong would truncate a GOOD reply, which is worse than the bug being fixed.
        const string tricky = """
            [{"itemId":"a","low":1,"high":2,"basis":"6\" pipe {new} [lot]"},
             {"itemId":"b","low":3,"high":4,"basis":"say \"hi\""},
             {"itemId":"c","low":5
            """;

        var rows = Parse(JsonSalvage.CompleteObjects(tricky));

        Assert.Equal(2, rows.Count);
        Assert.Equal("6\" pipe {new} [lot]", rows[0].Basis);
        Assert.Equal("say \"hi\"", rows[1].Basis);
    }

    [Fact]
    public void An_escaped_backslash_at_the_end_of_a_string_does_not_swallow_the_closing_quote()
    {
        const string s = """[{"itemId":"a","low":1,"high":2,"basis":"back\\"},{"itemId":"b","low":3,"high":4,"basis":"ok"}]""";
        var rows = Parse(JsonSalvage.CompleteObjects(s));
        Assert.Equal(2, rows.Count);
        Assert.Equal("back\\", rows[0].Basis);
    }

    [Fact]
    public void Prose_before_the_array_is_skipped()
    {
        const string chatty = """
            Here are the estimates you asked for:
            [{"itemId":"a","low":10,"high":20,"basis":"x"},{"itemId":"b","low":1
            """;
        Assert.Single(Parse(JsonSalvage.CompleteObjects(chatty)));
    }

    [Fact]
    public void Nothing_complete_yields_nothing_rather_than_broken_json()
    {
        Assert.Equal("", JsonSalvage.CompleteObjects("""[{"itemId":"a","low":1"""));
        Assert.Equal("", JsonSalvage.CompleteObjects("no array here at all"));
        Assert.Equal("", JsonSalvage.CompleteObjects(""));
        Assert.Equal("", JsonSalvage.CompleteObjects(null));
    }

    [Fact]
    public void A_nested_object_inside_a_row_does_not_end_the_row_early()
    {
        const string nested = """
            [{"itemId":"a","low":1,"high":2,"meta":{"src":"kbb"},"basis":"car"},
             {"itemId":"b","low":3
            """;
        var rows = Parse(JsonSalvage.CompleteObjects(nested));
        Assert.Single(rows);
        Assert.Equal("car", rows[0].Basis);
    }
}
