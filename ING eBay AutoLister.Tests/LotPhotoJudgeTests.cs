using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The check on the one field every number on the live card is derived from. Most of these are
/// refusals, and they are the point: a photo check that cries wolf costs the seller the lot AND the
/// panel, and one that quietly swaps the name they typed costs them the bid.
/// </summary>
public class LotPhotoJudgeTests
{
    private const string Photo = "https://media.whatnot.com/lot/abc.jpg";

    private static SnapIdentity Seen(
        string title, string certainty = "high", string brand = "", string model = "",
        string condition = "USED_GOOD", string note = "", string check = "") => new()
    {
        Title = title,
        Brand = brand,
        Model = model,
        Certainty = certainty,
        Condition = condition,
        ConditionNote = note,
        CheckThis = check,
    };

    // ── A name, or no name ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_photo_that_named_nothing_is_not_a_failure_and_still_says_what_to_do()
    {
        var look = LotPhotoJudge.Judge("Bitmain Antminer S19j Pro", Seen(""), Photo);

        Assert.Equal(LotPhotoStatuses.Unnamed, look.Status);
        Assert.Equal("", look.SeenTitle);
        Assert.Equal("", look.SuggestedTitle);
        Assert.NotEqual("", look.Hint);
    }

    [Fact]
    public void A_name_too_short_to_search_on_is_not_a_name()
    {
        // The same bar the pasted lot list holds a line to. "TV" answers a sold search at random.
        var look = LotPhotoJudge.Judge("", Seen("TV"), Photo);

        Assert.Equal(LotPhotoStatuses.Unnamed, look.Status);
        Assert.Contains("TV", look.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_at_all_coming_back_is_survivable()
    {
        var look = LotPhotoJudge.Judge("Antminer S19j Pro", null, Photo);

        Assert.Equal(LotPhotoStatuses.Unnamed, look.Status);
        Assert.NotEqual("", look.Headline);
    }

    [Fact]
    public void The_name_off_the_photo_is_cleaned_by_the_same_rule_a_pasted_lot_line_is()
    {
        // A name off a photo and the same name typed by hand have to reach the comp lookup
        // identically, or the app has two ways of asking eBay the same question.
        var look = LotPhotoJudge.Judge("", Seen("3) Bitmain Antminer S19j Pro — $250"), Photo);

        Assert.Equal("Bitmain Antminer S19j Pro", look.SeenTitle);
        Assert.Equal(LiveLotList.Clean("3) Bitmain Antminer S19j Pro — $250").Title, look.SeenTitle);
    }

    [Fact]
    public void What_was_typed_is_carried_back_cleaned_so_the_panel_argues_about_the_priced_name()
    {
        var look = LotPhotoJudge.Judge("  Lot 4: Goldshell Mini Doge II  ", Seen("Goldshell Mini Doge II"), Photo);

        Assert.Equal("Goldshell Mini Doge II", look.TypedTitle);
    }

    // ── What the photo says about the name ───────────────────────────────────────────────────

    [Fact]
    public void The_same_model_in_the_box_and_in_the_picture_agrees_and_offers_nothing()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro 104TH", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Equal(LotPhotoAgreement.Agrees, look.Agreement);
        Assert.Equal("", look.SuggestedTitle);
    }

    [Fact]
    public void A_photo_that_carries_a_spec_the_box_doesnt_sharpens_it()
    {
        var look = LotPhotoJudge.Judge(
            "Antminer S19j Pro", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Equal(LotPhotoAgreement.Sharpens, look.Agreement);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", look.SuggestedTitle);
    }

    [Fact]
    public void Two_different_models_of_the_same_thing_is_the_disagreement_worth_shouting_about()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro", Seen("Bitmain Antminer S9 13.5TH"), Photo);

        Assert.Equal(LotPhotoAgreement.Differs, look.Agreement);
        // And it is offered, because the seller is the one who decides which is right.
        Assert.Equal("Bitmain Antminer S9 13.5TH", look.SuggestedTitle);
    }

    [Fact]
    public void A_mystery_lot_cannot_be_contradicted_only_named()
    {
        // The case this whole feature exists for. "MYSTERY MINER LOT" makes no claim, so a sold
        // search on it answers at random — and the photo is the only thing that can fix that.
        var look = LotPhotoJudge.Judge("MYSTERY MINER LOT", Seen("Bitmain Antminer L7 9050M"), Photo);

        Assert.Equal(LotPhotoAgreement.Sharpens, look.Agreement);
        Assert.Equal("Bitmain Antminer L7 9050M", look.SuggestedTitle);
    }

    [Fact]
    public void One_word_in_the_box_is_a_category_not_a_claim()
    {
        var look = LotPhotoJudge.Judge("Antminer", Seen("Bitmain Antminer S19j Pro"), Photo);

        Assert.Equal(LotPhotoAgreement.Sharpens, look.Agreement);
    }

    [Fact]
    public void A_photo_that_adds_the_model_number_to_a_name_without_one_sharpens_it()
    {
        var look = LotPhotoJudge.Judge(
            "DeWalt cordless drill", Seen("DeWalt DCD771 20V Cordless Drill"), Photo);

        Assert.Equal(LotPhotoAgreement.Sharpens, look.Agreement);
        Assert.Equal("DeWalt DCD771 20V Cordless Drill", look.SuggestedTitle);
    }

    [Fact]
    public void Two_specific_names_with_nothing_in_common_differ()
    {
        var look = LotPhotoJudge.Judge("Nintendo Switch OLED", Seen("Sony PlayStation portal"), Photo);

        Assert.Equal(LotPhotoAgreement.Differs, look.Agreement);
    }

    /// <summary>
    /// The rule that stops the panel crying wolf. A picture with no legible plate cannot tell a 12
    /// from a 13 — and a "that's not what you typed" on every blurry photo is a panel the seller
    /// learns to ignore before the night's first good lot.
    /// </summary>
    [Fact]
    public void A_photo_with_no_model_number_on_it_neither_confirms_nor_contradicts_one()
    {
        var look = LotPhotoJudge.Judge("iPhone 12 Pro 128GB", Seen("Apple iPhone"), Photo);

        Assert.Equal(LotPhotoAgreement.Unsure, look.Agreement);
        Assert.Equal("", look.SuggestedTitle);
        Assert.Contains("iPhone 12 Pro 128GB", look.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_the_box_means_the_photos_name_is_the_only_one_there_is()
    {
        var look = LotPhotoJudge.Judge("", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Equal(LotPhotoAgreement.OnlyName, look.Agreement);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", look.SuggestedTitle);
    }

    [Fact]
    public void Packaging_words_are_not_a_disagreement()
    {
        // "used", "free ship" and a lot number decorate a name; two titles must not be held to
        // disagree over them.
        var look = LotPhotoJudge.Judge(
            "Used Goldshell Mini Doge II free ship", Seen("Goldshell Mini Doge II"), Photo);

        Assert.Equal(LotPhotoAgreement.Agrees, look.Agreement);
    }

    // ── The confidence gate ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_low_confidence_look_neither_agrees_nor_argues_and_offers_nothing()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro", Seen("Bitmain Antminer S9", certainty: "low"), Photo);

        Assert.Equal(LotPhotoAgreement.Unsure, look.Agreement);
        Assert.Equal("", look.SuggestedTitle);
        // It still says what it thinks it saw — that is worth reading, it is just not worth acting on.
        Assert.Contains("Bitmain Antminer S9", look.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confidence_nobody_has_ever_seen_reads_as_the_cautious_one()
    {
        var look = LotPhotoJudge.Judge(
            "Antminer S19j Pro", Seen("Bitmain Antminer S19j Pro 104TH", certainty: "very sure"), Photo);

        Assert.Equal(LotPhotoJudge.Low, look.Certainty);
        Assert.Equal(LotPhotoAgreement.Unsure, look.Agreement);
    }

    [Fact]
    public void Nothing_typed_and_a_low_confidence_photo_still_offers_nothing()
    {
        var look = LotPhotoJudge.Judge("", Seen("Bitmain Antminer S19j Pro", certainty: "low"), Photo);

        Assert.Equal(LotPhotoAgreement.OnlyName, look.Agreement);
        Assert.Equal("", look.SuggestedTitle);
    }

    // ── What is offered, and what never is ───────────────────────────────────────────────────

    [Theory]
    [InlineData(LotPhotoAgreement.Agrees)]
    [InlineData(LotPhotoAgreement.Unsure)]
    public void Carrying_on_is_never_dressed_up_as_a_decision(string agreement)
    {
        Assert.Equal("", LotPhotoJudge.Suggestion(
            agreement, "Antminer S19j Pro", "Bitmain Antminer S19j Pro 104TH", LotPhotoJudge.High));
    }

    [Fact]
    public void A_low_confidence_name_is_never_offered_however_much_better_it_looks()
    {
        Assert.Equal("", LotPhotoJudge.Suggestion(
            LotPhotoAgreement.Sharpens, "miner", "Bitmain Antminer S19j Pro 104TH", LotPhotoJudge.Low));
    }

    [Fact]
    public void A_name_that_would_not_survive_the_search_bar_is_never_offered()
    {
        Assert.Equal("", LotPhotoJudge.Suggestion(
            LotPhotoAgreement.Sharpens, "miner", "??", LotPhotoJudge.High));
    }

    [Fact]
    public void The_name_already_in_the_box_is_not_an_offer()
    {
        Assert.Equal("", LotPhotoJudge.Suggestion(
            LotPhotoAgreement.Sharpens, "bitmain antminer s19j pro",
            "Bitmain Antminer S19j Pro", LotPhotoJudge.High));
    }

    // ── What a picture cannot tell you ───────────────────────────────────────────────────────

    [Fact]
    public void Every_look_that_named_something_says_what_a_photo_cannot_tell_you()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro 104TH", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Contains(look.Warnings, w => w.Contains("powers on", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disagreement_says_out_loud_that_it_has_not_changed_the_card()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro", Seen("Bitmain Antminer S9 13.5TH"), Photo);

        Assert.Contains(look.Warnings, w => w.Contains("Bitmain Antminer S19j Pro", StringComparison.Ordinal));
        Assert.Contains(look.Warnings, w => w.Contains("Nothing has changed it", StringComparison.Ordinal));
    }

    [Fact]
    public void Medium_confidence_asks_for_a_glance_and_high_confidence_does_not()
    {
        var medium = LotPhotoJudge.Judge(
            "Antminer S19j Pro", Seen("Bitmain Antminer S19j Pro 104TH", certainty: "medium"), Photo);
        var high = LotPhotoJudge.Judge(
            "Antminer S19j Pro", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Contains(medium.Warnings, w => w.Contains("medium confidence", StringComparison.Ordinal));
        Assert.DoesNotContain(high.Warnings, w => w.Contains("medium confidence", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rough_looking_lot_is_said_and_never_costed()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro 104TH",
            Seen("Bitmain Antminer S19j Pro 104TH", condition: "FOR_PARTS_OR_NOT_WORKING",
                 note: "burnt PCIe connector"), Photo);

        Assert.Contains(look.Warnings, w => w.Contains("has taken money off the ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_carries_no_warnings_because_nothing_was_looked_at()
    {
        var look = LotPhotoJudge.Judge("Antminer S19j Pro", Seen(""), Photo);

        Assert.Empty(look.Warnings);
    }

    // ── The one thing that is a question ─────────────────────────────────────────────────────

    /// <summary>
    /// At a yard sale the seller picks the thing up. On a live show they can't — but the chat is
    /// open and the host is holding it, so the check becomes something to ask rather than something
    /// to do. It is the most actionable line on the panel.
    /// </summary>
    [Fact]
    public void What_a_photo_cannot_answer_becomes_a_question_for_the_host()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro 104TH",
            Seen("Bitmain Antminer S19j Pro 104TH", check: "Power it on before you pay"), Photo);

        Assert.Contains("Ask the host", look.AskTheHost, StringComparison.Ordinal);
        Assert.Contains("power it on before you pay", look.AskTheHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_ask_means_nothing_is_asked()
    {
        var look = LotPhotoJudge.Judge(
            "Bitmain Antminer S19j Pro 104TH", Seen("Bitmain Antminer S19j Pro 104TH"), Photo);

        Assert.Equal("", look.AskTheHost);
    }

    // ── It is inspectable ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_evidence_says_how_sure_it_was_and_what_it_was_compared_against()
    {
        var look = LotPhotoJudge.Judge(
            "Antminer S19j Pro",
            Seen("Bitmain Antminer S19j Pro 104TH", brand: "Bitmain", model: "S19j Pro"), Photo);

        Assert.Contains(look.Evidence, e => e.Contains("Confidence", StringComparison.Ordinal));
        Assert.Contains(look.Evidence, e => e.Contains("Brand on the photo: Bitmain", StringComparison.Ordinal));
        Assert.Contains(look.Evidence, e => e.Contains("Model on the photo: S19j Pro", StringComparison.Ordinal));
        Assert.Contains(look.Evidence, e => e.Contains("\"Antminer S19j Pro\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_evidence_says_when_there_was_nothing_to_compare_against()
    {
        var look = LotPhotoJudge.Judge("", Seen("Bitmain Antminer S19j Pro"), Photo);

        Assert.Contains(look.Evidence, e => e.Contains("Nothing was in the item box", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_that_was_shortened_is_shown_next_to_its_original()
    {
        var look = LotPhotoJudge.Judge("", Seen("Lot 7: Goldshell Mini Doge II — $95"), Photo);

        Assert.Contains(look.Evidence, e => e.Contains("Lot 7: Goldshell Mini Doge II", StringComparison.Ordinal));
    }

    // ── It prices nothing ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The safety property. A photograph is evidence about identity; a ceiling is made of sales that
    /// happened. Letting the weaker evidence move the stronger one's number would put two opinions
    /// about money on the one screen with no time to notice there are two.
    /// </summary>
    [Fact]
    public void The_look_carries_no_money_at_all()
    {
        var properties = typeof(LotPhotoLook).GetProperties().Select(p => p.Name).ToList();

        foreach (var money in new[]
                 {
                     "MaxBid", "BreakEvenBid", "ResalePrice", "MedianPrice", "Headroom",
                     "ProfitAtMaxBid", "CurrentBid", "LandedCostNow", "Price", "Adjustment",
                 })
        {
            Assert.DoesNotContain(money, properties);
        }

        Assert.DoesNotContain(typeof(LotPhotoLook).GetProperties(),
            p => p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?));
    }

    [Theory]
    [InlineData("Bitmain Antminer S19j Pro", "Bitmain Antminer S9 13.5TH")]
    [InlineData("MYSTERY MINER LOT", "Bitmain Antminer L7 9050M")]
    [InlineData("", "Bitmain Antminer S19j Pro 104TH")]
    public void Nothing_the_panel_says_is_a_number_with_a_currency_sign_on_it(string typed, string seen)
    {
        var look = LotPhotoJudge.Judge(typed, Seen(seen), Photo);

        foreach (var sentence in new[] { look.Headline, look.Detail, look.Hint }.Concat(look.Warnings))
            Assert.DoesNotContain("$", sentence, StringComparison.Ordinal);
    }

    // ── The refusals that arrive without a look ──────────────────────────────────────────────

    [Fact]
    public void No_photo_yet_is_an_empty_state_with_a_next_move_on_it()
    {
        var look = LotPhotoJudge.NoPhoto();

        Assert.Equal(LotPhotoStatuses.NoPhoto, look.Status);
        Assert.Contains("Read the show", look.Detail, StringComparison.Ordinal);
        Assert.NotEqual("", look.Hint);
    }

    [Fact]
    public void A_failed_look_says_the_failures_own_sentences()
    {
        var look = LotPhotoJudge.Failed(new FailureInfo
        {
            Headline = "No AI key",
            WhatHappened = "The app has no Anthropic key saved.",
            WhatToDo = "Add one in Settings.",
        });

        Assert.Equal(LotPhotoStatuses.Failed, look.Status);
        Assert.Equal("No AI key", look.Headline);
        Assert.Equal("Add one in Settings.", look.Hint);
    }

    [Fact]
    public void A_failure_with_nothing_to_do_still_gets_the_next_move_that_always_works()
    {
        var look = LotPhotoJudge.Failed(new FailureInfo { Headline = "Something went wrong" });

        Assert.Contains("Price it", look.Hint, StringComparison.Ordinal);
    }

    // ── Reading a name ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("s19j", true)]
    [InlineData("104th", true)]
    [InlineData("dcd771", true)]
    [InlineData("12", true)]
    [InlineData("antminer", false)]
    [InlineData("pro", false)]
    public void A_model_shaped_token_is_one_with_a_number_in_it(string token, bool expected) =>
        Assert.Equal(expected, LotPhotoJudge.ModelShaped(token));

    [Fact]
    public void Decoration_is_dropped_and_a_word_is_only_counted_once()
    {
        var tokens = LotPhotoJudge.Significant("NEW Antminer antminer, free shipping (used)");

        Assert.Equal(new[] { "antminer" }, tokens);
    }

    [Fact]
    public void A_single_letter_is_not_a_word()
    {
        // "2 x Antminer S9" — the x is a multiplication sign, not part of the name.
        Assert.DoesNotContain("x", LotPhotoJudge.Significant("2 x Antminer S9"));
    }

    [Theory]
    [InlineData("mystery box", true)]
    [InlineData("random bundle", true)]
    [InlineData("antminer", true)]
    [InlineData("nintendo switch oled", false)]
    [InlineData("antminer s19j pro", false)]
    public void A_name_that_makes_no_claim_cannot_be_contradicted(string title, bool expected)
    {
        var tokens = LotPhotoJudge.Significant(title);
        var models = tokens.Where(LotPhotoJudge.ModelShaped).ToList();

        Assert.Equal(expected, LotPhotoJudge.IsVague(tokens, models));
    }

    [Theory]
    [InlineData("Antminer", true)]
    [InlineData("TV", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_searchable_bar_is_the_lot_lists_own(string? title, bool expected) =>
        Assert.Equal(expected, LotPhotoJudge.Searchable(title));
}
