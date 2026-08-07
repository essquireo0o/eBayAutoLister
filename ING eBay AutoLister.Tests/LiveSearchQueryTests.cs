using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What the sold search actually asks eBay for, when the seller types what a live host just said.
/// </summary>
/// <remarks>
/// <para>
/// The lookup behind every number on the live card is a boolean AND against sold titles, so one word
/// of auction talk in the query is the difference between five statistics and CAN'T PRICE IT. Most
/// of what follows is therefore about what comes OUT of a lot's name — and the rest, which matters
/// more, is about what may never come out of it.
/// </para>
/// <para>
/// The asymmetry these are written around: dropping a word that mattered makes the comps quietly
/// wrong, and keeping one that didn't makes the card visibly say nothing. The visible failure is the
/// recoverable one, so the vocabulary is deliberately short and the keep-list is deliberately long.
/// <see cref="Hot_and_fire_are_product_names_on_a_live_show"/> is the case that decided its shape.
/// </para>
/// </remarks>
public class LiveSearchQueryTests
{
    // ── The lot name a live show actually produces ───────────────────────────────────────────

    [Fact]
    public void The_auction_talk_comes_out_and_the_item_stays_in()
    {
        var terms = LiveSearchQuery.Build("🔥3x Bitmain Antminer S9 13.5TH — NO RESERVE!! ships free 📦");

        Assert.Equal("Bitmain Antminer S9 13.5TH", terms.Query);
        Assert.True(terms.Changed);
        Assert.Equal("🔥3x Bitmain Antminer S9 13.5TH — NO RESERVE!! ships free 📦", terms.Typed);
    }

    [Fact]
    public void Every_word_taken_out_is_named_with_its_reason()
    {
        // The seller is trusting a ceiling to a search they cannot see. A cleaner that edited the
        // question silently would be worse than no cleaner at all.
        var terms = LiveSearchQuery.Build("3x Antminer S9 no reserve free shipping 🔥");
        var dropped = terms.Dropped.Select(d => d.Text).ToList();

        Assert.Contains("3x", dropped);
        Assert.Contains("no reserve", dropped);
        Assert.Contains("free shipping", dropped);
        Assert.All(terms.Dropped, d => Assert.NotEqual("", d.Why));
        Assert.All(terms.Dropped, d => Assert.NotEqual("", d.Kind));
    }

    [Theory]
    [InlineData("Antminer S19j Pro NO RESERVE", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro going once", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro starting at $1", "Antminer S19j Pro")]
    [InlineData("WOW!! Antminer S19j Pro", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro @minerdealsdaily", "Antminer S19j Pro")]
    [InlineData("Lot 12: Antminer S19j Pro", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro — free ship", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro • must see", "Antminer S19j Pro")]
    [InlineData("Antminer S19j Pro (giveaway)", "Antminer S19j Pro")]
    public void The_sale_is_described_by_words_the_search_never_asks_for(string typed, string expected)
    {
        Assert.Equal(expected, LiveSearchQuery.Build(typed).Query);
    }

    [Fact]
    public void The_count_the_ceiling_already_multiplied_by_is_not_asked_for_twice()
    {
        // The documented gap this closes: the money was priced for three of them off comps that had
        // to contain the words "3x" to be found at all.
        var terms = LiveSearchQuery.Build("3x Bitmain Antminer S9");

        Assert.Equal("Bitmain Antminer S9", terms.Query);
        Assert.Equal(3, LiveLotSize.Read(terms.Typed, null).Count);
        Assert.Contains(terms.Dropped, d => d.Kind == LiveSearchDropKinds.Count && d.Text == "3x");
        Assert.Contains("all 3", terms.Dropped.First(d => d.Kind == LiveSearchDropKinds.Count).Why,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(4) Goldshell Mini Doge II", "Goldshell Mini Doge II")]
    [InlineData("LOT OF 5 GPU risers", "GPU risers")]
    [InlineData("Antminer control boards 8 pcs", "Antminer control boards")]
    [InlineData("MYSTERY MINER LOT", "MYSTERY MINER")]
    public void Bulk_wording_goes_with_it(string typed, string expected)
    {
        Assert.Equal(expected, LiveSearchQuery.Build(typed).Query);
    }

    [Fact]
    public void A_count_the_reader_refused_to_believe_is_left_in_the_search()
    {
        // "16x PCIe riser" is one riser, and 16x is HOW you tell it from an 8x. The reader refuses
        // to price it as sixteen; the search would be searching for a different product without it.
        var terms = LiveSearchQuery.Build("16x PCIe riser card");

        Assert.Contains("16x", terms.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, LiveLotSize.Read(terms.Typed, null).Count);
    }

    // ── What may never come out ──────────────────────────────────────────────────────────────

    [Fact]
    public void Hot_and_fire_are_product_names_on_a_live_show()
    {
        // The two words that were in the hype vocabulary until it was asked what a live feed
        // actually sells. Hot Wheels and Amazon Fire are categories; 🔥 is decoration.
        Assert.Equal("Hot Wheels Redline Camaro", LiveSearchQuery.Build("Hot Wheels Redline Camaro 🔥").Query);
        Assert.Equal("Amazon Fire HD 10 tablet", LiveSearchQuery.Build("Amazon Fire HD 10 tablet").Query);
        Assert.Equal("Fire Emblem Three Houses", LiveSearchQuery.Build("Fire Emblem Three Houses").Query);
    }

    [Theory]
    [InlineData("Pokemon 151 booster box SEALED")]
    [InlineData("Charizard PSA 10 graded")]
    [InlineData("Antminer S19 for parts not working")]
    [InlineData("Nintendo Switch OLED new in box")]
    [InlineData("Vintage Coach purse used")]
    [InlineData("Rolex Datejust authentic")]
    [InlineData("Xbox Series X untested as-is")]
    [InlineData("Air Jordan 1 Chicago NWT")]
    public void Condition_completeness_and_authenticity_are_never_touched(string typed)
    {
        // These are the words that decide which end of the price spread a thing lands on. A search
        // that dropped them would compare a sealed box to an opened one and call the difference
        // profit — the silent failure, which is the one this refuses to risk.
        var terms = LiveSearchQuery.Build(typed);

        Assert.Equal(typed, terms.Query);
        Assert.False(terms.Changed);
        Assert.Empty(terms.Dropped);
    }

    [Theory]
    [InlineData("chess set")]
    [InlineData("LEGO Star Wars set 75192")]
    [InlineData("Pearl Export drum set")]
    public void A_set_is_a_product_and_a_set_of_is_a_quantity(string typed)
    {
        Assert.Equal(typed, LiveSearchQuery.Build(typed).Query);
    }

    [Fact]
    public void The_marks_inside_an_identifier_survive()
    {
        // "13.5TH", "S19-Pro" and "1/2 inch" are one token each. Punctuation is stripped everywhere
        // it decorates and nowhere it identifies.
        Assert.Equal("Antminer S19-Pro 13.5TH 1/2 inch", LiveSearchQuery.Build("Antminer S19-Pro 13.5TH 1/2 inch!!").Query);
    }

    [Fact]
    public void A_name_that_is_all_auction_talk_is_searched_exactly_as_typed()
    {
        // Cleaning this down to one word would search a whole category and price the lot off it.
        // Refused whole, and said out loud.
        var terms = LiveSearchQuery.Build("MYSTERY BUNDLE no reserve 🔥");

        Assert.Equal("MYSTERY BUNDLE no reserve 🔥", terms.Query);
        Assert.False(terms.Changed);
        Assert.NotEqual("", terms.Refused);
        Assert.Contains("exactly as typed", terms.Refused, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ordinary_product_name_is_left_completely_alone()
    {
        // The compatibility property. A seller who types a clean title gets the search they typed,
        // no chips, and a card that says so.
        var terms = LiveSearchQuery.Build("Bitmain Antminer S19j Pro 104TH");

        Assert.Equal("Bitmain Antminer S19j Pro 104TH", terms.Query);
        Assert.False(terms.Changed);
        Assert.Empty(terms.Dropped);
        Assert.Equal("", terms.Refused);
        Assert.False(terms.Widened);
    }

    [Fact]
    public void Nothing_typed_is_nothing_searched()
    {
        foreach (var empty in new[] { null, "", "   " })
        {
            var terms = LiveSearchQuery.Build(empty);
            Assert.Equal("", terms.Query);
            Assert.Empty(terms.Dropped);
        }
    }

    [Fact]
    public void No_word_appears_in_the_query_that_the_seller_did_not_type()
    {
        // The one property that makes a cleaned search checkable at a glance: this only ever
        // removes. A cleaner that could add or reorder words would be one the seller has to read
        // the whole of rather than diff against what they typed.
        var typed = "🔥 LOT OF 3 Bitmain Antminer S9 13.5TH PSU included — NO RESERVE, ships free!!";
        var terms = LiveSearchQuery.Build(typed);

        var typedWords = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('!', ',', '—', '.', '(', ')').ToLowerInvariant()).ToHashSet();

        foreach (var word in terms.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word.ToLowerInvariant(), typedWords);
    }

    // ── Widening: evidence traded for precision, once, and said out loud ─────────────────────

    [Fact]
    public void A_long_name_can_be_cut_back_to_the_words_that_identify_it()
    {
        var terms = LiveSearchQuery.Build("Pokemon 151 Ultra Premium Collection sealed English 2024");
        var wider = LiveSearchQuery.Widen(terms);

        Assert.NotNull(wider);
        Assert.Equal("Pokemon 151 Ultra", wider!.Query);
        Assert.True(wider.Widened);
        Assert.Contains("Pokemon 151 Ultra", wider.WidenedNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_words_the_widening_gave_up_are_shown_as_given_up()
    {
        var wider = LiveSearchQuery.Widen(LiveSearchQuery.Build("Pokemon 151 Ultra Premium Collection sealed English"));

        Assert.NotNull(wider);
        var lost = Assert.Single(wider!.Dropped, d => d.Kind == LiveSearchDropKinds.Widened);
        Assert.Equal("Premium Collection sealed English", lost.Text);
    }

    [Fact]
    public void There_is_no_shorter_search_that_is_still_a_search()
    {
        // Three identifying words IS the widened search, so a name that is already that short has
        // nowhere to go. Returning a two-word query here would price an S19j Pro off "Antminer".
        Assert.Null(LiveSearchQuery.Widen(LiveSearchQuery.Build("Bitmain Antminer S19")));
        Assert.Null(LiveSearchQuery.Widen(LiveSearchQuery.Build("Antminer S9")));
        Assert.Null(LiveSearchQuery.Widen(LiveSearchQuery.Build("")));
    }

    [Fact]
    public void A_widened_search_is_never_widened_again()
    {
        // One step, so the ceiling cannot walk away from the lot a word at a time.
        var once = LiveSearchQuery.Widen(LiveSearchQuery.Build("Sony PlayStation 5 Digital Edition console bundle white"));

        Assert.NotNull(once);
        Assert.Null(LiveSearchQuery.Widen(once!));
    }

    [Fact]
    public void The_widening_keeps_the_leading_words_because_that_is_where_the_model_is()
    {
        var wider = LiveSearchQuery.Widen(LiveSearchQuery.Build("Bitmain Antminer S19j Pro 104TH with PSU and cord"));

        Assert.NotNull(wider);
        Assert.Equal("Bitmain Antminer S19j", wider!.Query);
        Assert.StartsWith(wider.Query, "Bitmain Antminer S19j Pro 104TH with PSU and cord", StringComparison.Ordinal);
    }

    [Fact]
    public void A_widened_card_has_to_say_so_where_the_money_is()
    {
        var wider = LiveSearchQuery.Widen(LiveSearchQuery.Build("Pokemon 151 Ultra Premium Collection sealed English"));
        var warning = LiveSearchQuery.WidenedWarning(wider!);

        Assert.Contains("Pokemon 151 Ultra", warning, StringComparison.Ordinal);
        Assert.Contains("not for the whole name", warning, StringComparison.OrdinalIgnoreCase);
        // And a search that was not widened says nothing at all, rather than a reassurance nobody
        // asked for on every card.
        Assert.Equal("", LiveSearchQuery.WidenedWarning(LiveSearchQuery.Build("Antminer S9")));
    }

    // ── The undo ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_seller_can_ask_for_their_exact_words_back()
    {
        var typed = "🔥3x Antminer S9 NO RESERVE";
        var exact = LiveSearchQuery.Exact(typed);

        Assert.Equal(typed, exact.Query);
        Assert.False(exact.Changed);
        Assert.True(exact.AskedForExactly);
        Assert.Empty(exact.Dropped);
        Assert.Contains("exactly what you typed", exact.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_reason_the_search_looks_like_that_is_on_the_card()
    {
        Assert.Contains("boolean AND", LiveSearchQuery.Build("Antminer S9 no reserve").Note, StringComparison.Ordinal);
        Assert.Contains("exactly what you typed", LiveSearchQuery.Build("Antminer S9 104TH").Note,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── It prices nothing ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_builder_is_pure_and_carries_no_money()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "LiveSearchQuery.cs"));

        foreach (var money in new[] { "ProfitCalculator", "FeeProfile", "MaxBid", "BreakEven", "decimal " })
            Assert.DoesNotContain(money, source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
