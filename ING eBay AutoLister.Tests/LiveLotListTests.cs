using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A lot list is pasted, not typed, and what gets pasted was written for a human: lot numbers,
// asking prices, dashes, repeats and a couple of lines that name nothing at all. Every one of those
// reaches an eBay sold search if it is not taken off, and a search for "3) Bitmain Antminer S19j Pro
// 104TH — starting at $250" returns nothing.
//
// So the risk this parser carries is not that it fails to clean a line. It is that it cleans the
// WRONG half — drops the year off "1975 Topps", the quantity off "2 x Antminer S9", the face value
// off "$100 gift card" — and the seller bids on a ceiling priced for a different thing. Most of what
// is below is about what it must leave alone.
public class LiveLotListTests
{
    // ── What comes off ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1. Bitmain Antminer S19j Pro", "Bitmain Antminer S19j Pro")]
    [InlineData("12) Goldshell Mini Doge II", "Goldshell Mini Doge II")]
    [InlineData("Lot 3: iPhone 12 Pro 128GB", "iPhone 12 Pro 128GB")]
    [InlineData("lot 3 - iPhone 12 Pro", "iPhone 12 Pro")]
    [InlineData("#4 — Antminer S9", "Antminer S9")]
    [InlineData("Item 7] Whatsminer M30S", "Whatsminer M30S")]
    [InlineData("No. 5: Nintendo Switch OLED", "Nintendo Switch OLED")]
    public void A_lot_number_is_not_part_of_what_the_thing_is(string line, string expected)
    {
        Assert.Equal(expected, LiveLotList.Clean(line).Title);
    }

    [Theory]
    [InlineData("iPhone 12 Pro 128GB $180", "iPhone 12 Pro 128GB", 180)]
    [InlineData("Antminer S19j Pro — starting at $250", "Antminer S19j Pro", 250)]
    [InlineData("Antminer S19j Pro (opening $250)", "Antminer S19j Pro", 250)]
    [InlineData("Whatsminer M30S · now $1,250.50", "Whatsminer M30S", 1250.50)]
    [InlineData("Goldshell Mini Doge II, $75 USD", "Goldshell Mini Doge II", 75)]
    [InlineData("Nintendo Switch OLED bid $199.99", "Nintendo Switch OLED", 199.99)]
    public void The_price_on_the_line_becomes_the_opening_bid_and_leaves_the_title(
        string line, string expectedTitle, decimal expectedBid)
    {
        var (title, opening) = LiveLotList.Clean(line);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedBid, opening);
    }

    // ── What must NOT come off ────────────────────────────────────────────────

    /// <summary>
    /// The commonest way a parser like this loses money: a leading number that is part of the name.
    /// A lot marker is a number followed by punctuation, or introduced by a word — not any number at
    /// the start of a line.
    /// </summary>
    [Theory]
    [InlineData("1975 Topps complete set")]
    [InlineData("2 x Antminer S9")]
    [InlineData("104TH Antminer S19j Pro")]
    [InlineData("50 lb box of mixed cards")]
    public void A_number_that_names_the_thing_stays_on_it(string line)
    {
        Assert.Equal(line, LiveLotList.Clean(line).Title);
    }

    /// <summary>
    /// A price at the FRONT is part of the name — a "$100 gift card" is named after its face value,
    /// and pricing a gift card without it prices nothing. Only the trailing price comes off.
    /// </summary>
    [Fact]
    public void A_price_at_the_front_is_part_of_the_name()
    {
        var (title, opening) = LiveLotList.Clean("$100 Amazon gift card");
        Assert.Equal("$100 Amazon gift card", title);
        Assert.Null(opening);
    }

    /// <summary>
    /// The lead-in words ("starting at", "now", "bid") are only stripped when a price was actually
    /// found behind them. Otherwise the last word of a title would go for free.
    /// </summary>
    [Theory]
    [InlineData("Xbox Series X Now")]
    [InlineData("Antminer S19 opening")]
    [InlineData("Lot of parts for Bitmain")]
    public void A_lead_in_word_only_goes_when_it_was_holding_a_price(string line)
    {
        Assert.Equal(line, LiveLotList.Clean(line).Title);
    }

    /// <summary>
    /// A price the app cannot read is left where it is. Deciding a run of characters was "the price"
    /// and dropping it without being able to say what it was is how a title loses a model number.
    /// </summary>
    [Fact]
    public void An_unreadable_price_is_left_on_the_line()
    {
        var (title, opening) = LiveLotList.Clean("Antminer S19j Pro $0");
        Assert.Equal("Antminer S19j Pro $0", title);
        Assert.Null(opening);
    }

    /// <summary>Model numbers containing "at" or "for" as a substring are not words.</summary>
    [Fact]
    public void A_word_boundary_is_respected_when_the_lead_in_is_stripped()
    {
        Assert.Equal("AT&T iPhone 12", LiveLotList.Clean("AT&T iPhone 12 $200").Title);
        Assert.Equal("Cat 6 cable reel", LiveLotList.Clean("Cat 6 cable reel $40").Title);
    }

    // ── The list ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_pasted_list_reads_in_the_order_it_was_written()
    {
        var plan = LiveLotList.Parse("""
            1) Bitmain Antminer S19j Pro 104TH — starting at $250
            2) Goldshell Mini Doge II
            Lot 3: iPhone 12 Pro 128GB $180
            """);

        Assert.Equal(3, plan.Lots.Count);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", plan.Lots[0].Title);
        Assert.Equal(250m, plan.Lots[0].OpeningBid);
        Assert.Equal("Goldshell Mini Doge II", plan.Lots[1].Title);
        Assert.Null(plan.Lots[1].OpeningBid);
        Assert.Equal("iPhone 12 Pro 128GB", plan.Lots[2].Title);
        Assert.Equal(180m, plan.Lots[2].OpeningBid);
    }

    /// <summary>The line is kept alongside what was made of it. A cleaned title that dropped the
    /// wrong half is only findable next to the thing it was read from.</summary>
    [Fact]
    public void Each_lot_keeps_the_line_it_was_read_from()
    {
        var plan = LiveLotList.Parse("1) Bitmain Antminer S19j Pro — starting at $250");
        Assert.Equal("1) Bitmain Antminer S19j Pro — starting at $250", plan.Lots[0].Line);
    }

    /// <summary>A show repeats itself, and a list pasted twice is the commonest paste there is.
    /// Pricing the same title twice spends an eBay read to arrive at the answer already on screen.</summary>
    [Fact]
    public void A_repeat_is_skipped_and_counted()
    {
        var plan = LiveLotList.Parse("""
            Antminer S19j Pro
            antminer s19j pro
            3. Antminer S19j Pro $50
            Goldshell Mini Doge II
            """);

        Assert.Equal(2, plan.Lots.Count);
        Assert.Equal(2, plan.DuplicatesDropped);
        Assert.Contains("2 repeats skipped", plan.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$250")]
    [InlineData("---")]
    [InlineData("7.")]
    [InlineData("***")]
    [InlineData("42")]
    public void A_line_naming_nothing_is_dropped_rather_than_searched_for(string line)
    {
        var plan = LiveLotList.Parse($"Antminer S19j Pro\n{line}");
        Assert.Single(plan.Lots);
        Assert.Equal(1, plan.UnusableDropped);
        Assert.Contains("nothing to search on", plan.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_lines_separate_a_list_and_are_not_entries()
    {
        var plan = LiveLotList.Parse("Antminer S19j Pro\n\n\nGoldshell Mini Doge II\n\n");
        Assert.Equal(2, plan.Lots.Count);
        Assert.Equal(0, plan.UnusableDropped);
    }

    [Fact]
    public void Tabs_and_runs_of_spaces_collapse_so_a_pasted_table_still_reads()
    {
        Assert.Equal("iPhone 12 Pro 128GB", LiveLotList.Clean("  iPhone\t12   Pro\t128GB\t$180  ").Title);
    }

    // ── The cap ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The list stops at what the board can hold, because every lot on it holds comps there. A
    /// thirteenth would evict the first one's while the seller was still deciding about it, and the
    /// row would stop opening for a reason nothing on screen could explain.
    /// </summary>
    [Fact]
    public void The_list_stops_at_what_the_board_can_hold()
    {
        Assert.Equal(LiveBidBoard.Capacity, LiveLotList.MaxLots);

        var lines = Enumerable.Range(1, LiveLotList.MaxLots + 4).Select(i => $"{i}. Antminer S{i} miner");
        var plan = LiveLotList.Parse(string.Join("\n", lines));

        Assert.Equal(LiveLotList.MaxLots, plan.Lots.Count);
        Assert.Equal(4, plan.OverflowDropped);
        Assert.Equal(LiveLotList.MaxLots, plan.MaxLots);
        Assert.Contains($"4 more than the {LiveLotList.MaxLots}", plan.Note, StringComparison.Ordinal);
    }

    /// <summary>The lots kept are the FIRST ones, not the last. A show sells in the order it
    /// published, so the ones about to happen are the ones worth pricing.</summary>
    [Fact]
    public void The_lots_kept_are_the_ones_that_happen_first()
    {
        var lines = Enumerable.Range(1, LiveLotList.MaxLots + 3).Select(i => $"{i}. Antminer S{i} miner");
        var plan = LiveLotList.Parse(string.Join("\n", lines));

        Assert.Equal("Antminer S1 miner", plan.Lots[0].Title);
        Assert.Equal($"Antminer S{LiveLotList.MaxLots} miner", plan.Lots[^1].Title);
    }

    // ── Nothing, and far too much ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  \t ")]
    public void Nothing_pasted_is_an_empty_list_and_says_so(string? text)
    {
        var plan = LiveLotList.Parse(text);
        Assert.Empty(plan.Lots);
        Assert.Equal("Nothing on that list to price.", plan.Note);
    }

    /// <summary>A paste that turns out to be a document is bounded rather than read: finding out it
    /// was a document by reading all of it spends the seconds this screen exists to save.</summary>
    [Fact]
    public void A_paste_that_is_a_document_is_bounded_rather_than_read()
    {
        var huge = string.Join("\n", Enumerable.Range(1, 5_000).Select(i => $"Antminer model S{i}x"));
        var plan = LiveLotList.Parse(huge);

        Assert.Equal(LiveLotList.MaxLots, plan.Lots.Count);
        // Only MaxLines were examined, so the overflow count reflects the bound and not the paste.
        Assert.True(plan.OverflowDropped <= LiveLotList.MaxLines);
    }

    /// <summary>Windows clipboards carry \r\n. A carriage return left on a title is a character in
    /// the sold search that matches nothing.</summary>
    [Fact]
    public void A_windows_clipboard_does_not_leave_carriage_returns_in_the_search()
    {
        var plan = LiveLotList.Parse("1) Antminer S19j Pro\r\n2) Goldshell Mini Doge II\r\n");
        Assert.Equal(2, plan.Lots.Count);
        Assert.DoesNotContain(plan.Lots, l => l.Title.Contains('\r'));
        Assert.Equal("Goldshell Mini Doge II", plan.Lots[1].Title);
    }

    /// <summary>Nothing on a clean list is reported as dropped. A note that always warns about
    /// something is a note nobody reads on the day it matters.</summary>
    [Fact]
    public void A_clean_list_is_reported_as_clean()
    {
        var plan = LiveLotList.Parse("Antminer S19j Pro\nGoldshell Mini Doge II");
        Assert.Equal("2 lots to price, in the order they were listed.", plan.Note);
    }
}
