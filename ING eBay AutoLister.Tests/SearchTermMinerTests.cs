using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The keyword miner reads other people's listings and offers their words to the seller. Two
/// things can go wrong with that, and only one of them is visible on screen.
///
/// The visible one is a bad recommendation. The invisible one is a recommendation that reads like
/// evidence and isn't: "60% of results" computed over five listings, or over one seller's ten
/// relists of the same item, or a word that is in every eBay title ever written because it is a
/// promise about postage rather than a fact about the thing. A seller acts on those numbers — they
/// retype an 80-character title because of them — so the thresholds under them are the feature.
///
/// Every test here is on pure functions. No eBay call, no clock, no database; the titles are the
/// input.
/// </summary>
public class SearchTermMinerTests
{
    // A corpus big enough to clear MinCorpusTitles, all agreeing on words the seller's title lacks.
    private static List<string> AntminerMarket() =>
    [
        "Bitmain Antminer S19 Pro 110TH/s Bitcoin Miner SHA-256 ASIC",
        "Bitmain Antminer S19 Pro 110TH Bitcoin Miner SHA-256 with PSU",
        "Antminer S19 Pro 110TH/s Bitcoin Miner ASIC SHA-256 Tested",
        "Bitmain Antminer S19 Pro Bitcoin Miner 110TH/s SHA-256 Working",
        "Antminer S19 Pro 110TH Bitcoin ASIC Miner SHA-256 Hashboard",
        "Bitmain Antminer S19 Pro Bitcoin Miner SHA-256 110TH/s Unit",
        "Antminer S19 Pro Bitcoin Miner 110TH/s SHA-256 ASIC Complete",
    ];

    // ── The bars that stop a coincidence being rendered as a market ────────────────────────────

    [Fact]
    public void Nothing_to_read_says_nothing_rather_than_guessing()
    {
        var r = SearchTermMiner.Mine("Bitmain Antminer S19", [], []);

        Assert.Equal("no_data", r.Status);
        Assert.Empty(r.Missing);
        Assert.Equal("", r.SuggestedTitle);
    }

    [Fact]
    public void A_handful_of_listings_is_reported_as_too_few_to_call_a_pattern()
    {
        // Five titles all saying "Bitcoin" is not a market, and 100% of five is the most
        // persuasive-looking number this could possibly print.
        var r = SearchTermMiner.Mine("Antminer S19", AntminerMarket().Take(5).ToList(), []);

        Assert.Equal("thin_market", r.Status);
        Assert.Empty(r.Missing);
        Assert.Contains("too few", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_seller_relisting_the_same_item_is_one_opinion_not_ten()
    {
        // The same title ten times over clears every count threshold there is on raw volume alone.
        // Deduplicating first is what stops a single relisted listing being reported as a market.
        var oneListing = Enumerable.Repeat("Bitmain Antminer S19 Pro Bitcoin Miner SHA-256", 10).ToList();

        var r = SearchTermMiner.Mine("Antminer S19", oneListing, []);

        Assert.Equal("thin_market", r.Status);
        Assert.Equal(1, r.RankedTotal);
    }

    [Fact]
    public void A_word_in_one_listing_of_a_big_corpus_is_not_offered()
    {
        var market = AntminerMarket();
        market[0] += " Hydro Immersion";

        var r = SearchTermMiner.Mine("Antminer S19", market, []);

        Assert.DoesNotContain(r.Missing, t => t.Term.Contains("Hydro", StringComparison.OrdinalIgnoreCase));
    }

    // ── What the seller is actually told ───────────────────────────────────────────────────────

    [Fact]
    public void The_words_the_market_uses_and_the_title_lacks_come_back_with_their_counts()
    {
        var r = SearchTermMiner.Mine("Antminer S19 Pro", AntminerMarket(), []);

        Assert.Equal("ok", r.Status);
        var bitcoin = r.Missing.FirstOrDefault(t => t.Term.Equals("Bitcoin", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bitcoin);

        // The count is the argument. A term with no evidence attached is just advice.
        Assert.Equal(7, bitcoin!.RankedCount);
        Assert.Equal(7, bitcoin.RankedTotal);
        Assert.Equal(100, bitcoin.SharePercent);
        Assert.False(bitcoin.InYourTitle);
    }

    [Fact]
    public void A_word_the_seller_already_has_is_credited_not_offered_again()
    {
        var r = SearchTermMiner.Mine("Bitmain Antminer S19 Pro Bitcoin Miner", AntminerMarket(), []);

        Assert.DoesNotContain(r.Missing, t => t.Term.Equals("Bitcoin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Shared, t => t.Term.Equals("Bitcoin", StringComparison.OrdinalIgnoreCase));
        Assert.All(r.Shared, t => Assert.True(t.InYourTitle));
    }

    [Fact]
    public void Sold_titles_are_counted_separately_from_ranked_ones()
    {
        // Ranking is attention; selling is money. A seller deciding whether to retype their title
        // is owed both numbers, not a blended one.
        var sold = new List<string>
        {
            "Bitmain Antminer S19 Pro 110TH Bitcoin Miner Hosted",
            "Antminer S19 Pro Bitcoin Miner 110TH/s Ready to Mine",
            "Bitmain Antminer S19 Pro Bitcoin Miner SHA-256 Shipped",
        };

        var r = SearchTermMiner.Mine("Antminer S19 Pro", AntminerMarket(), sold);

        Assert.Equal(7, r.RankedTotal);
        Assert.Equal(3, r.SoldTotal);
        var bitcoin = r.Missing.First(t => t.Term.Equals("Bitcoin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7, bitcoin.RankedCount);
        Assert.Equal(3, bitcoin.SoldCount);
    }

    [Fact]
    public void With_no_ranked_results_the_sold_titles_become_the_market()
    {
        var r = SearchTermMiner.Mine("Antminer S19 Pro", [], AntminerMarket());

        Assert.Equal("ok", r.Status);
        Assert.Equal(0, r.RankedTotal);
        var bitcoin = r.Missing.First(t => t.Term.Equals("Bitcoin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7, bitcoin.SoldCount);
        Assert.Equal(100, bitcoin.SharePercent);
    }

    // ── The words this will not put in a seller's mouth ────────────────────────────────────────

    [Fact]
    public void Shipping_and_hype_promises_are_never_offered_however_common_they_are()
    {
        // These are the most common words in any corpus of eBay titles, so without the refusal
        // list they would top every answer this ever gives — and a title promising free postage on
        // a listing that charges for it is a buyer complaint, not a keyword.
        List<string> hype =
        [
            "Antminer S19 Pro Bitcoin Miner FREE SHIPPING Fast Ship RARE LQQK 1",
            "Antminer S19 Pro Bitcoin Miner Free Shipping Fast Ship Rare Look 2",
            "Antminer S19 Pro Bitcoin Miner FREE SHIPPING Fast Ship Rare LQQK 3",
            "Antminer S19 Pro Bitcoin Miner free shipping fast ship rare look 4",
            "Antminer S19 Pro Bitcoin Miner Free Shipping Fast Ship RARE Look 5",
            "Antminer S19 Pro Bitcoin Miner FREE Shipping Fast Ship rare LQQK 6",
        ];

        var r = SearchTermMiner.Mine("Antminer S19 Pro", hype, []);

        var offered = r.Missing.Select(t => t.Term.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(offered, t => t.Contains("free"));
        Assert.DoesNotContain(offered, t => t.Contains("shipping"));
        Assert.DoesNotContain(offered, t => t.Contains("fast"));
        Assert.DoesNotContain(offered, t => t.Contains("rare"));
        Assert.DoesNotContain(offered, t => t.Contains("lqqk"));
        Assert.DoesNotContain(offered, t => t.Contains("look"));

        // …and the real words in the same titles still come through.
        Assert.Contains(r.Missing, t => t.Term.Contains("Bitcoin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grammar_fragments_are_not_search_terms()
    {
        List<string> withFor =
        [
            "Antminer S19 Pro Power Supply for Bitcoin Miner",
            "Antminer S19 Pro Power Supply for Bitcoin Mining Rig",
            "Antminer S19 Pro PSU Power Supply for Bitcoin Miner",
            "Antminer S19 Pro Power Supply for Bitcoin ASIC",
            "Antminer S19 Pro Power Supply for Bitcoin Miner Unit",
            "Antminer S19 Pro Power Supply for Bitcoin Rig",
        ];

        var r = SearchTermMiner.Mine("Antminer S19 Pro", withFor, []);

        foreach (var term in r.Missing.Select(t => t.Term.ToLowerInvariant()))
        {
            Assert.False(term.StartsWith("for ", StringComparison.Ordinal), $"offered a fragment: {term}");
            Assert.False(term.EndsWith(" for", StringComparison.Ordinal), $"offered a fragment: {term}");
        }
        Assert.Contains(r.Missing, t => t.Term.Contains("Power Supply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_phrase_wins_over_the_word_inside_it()
    {
        // "Power Supply" is in all six; "Supply PSU" is in two of them. The bound phrase is the
        // term, and once it is offered the words inside it are not offered again — a title that
        // says "power supply" does not need "supply" appending to it.
        List<string> market =
        [
            "Antminer S19 Pro Power Supply PSU Bitcoin Miner",
            "Antminer S19 Pro Power Supply APW12 Bitcoin ASIC",
            "Antminer S19 Pro Power Supply Unit Bitcoin Miner",
            "Antminer S19 Pro Power Supply PSU Bitcoin ASIC",
            "Antminer S19 Pro Power Supply APW12 Bitcoin Miner",
            "Antminer S19 Pro Power Supply Unit Bitcoin ASIC",
        ];

        var r = SearchTermMiner.Mine("Antminer S19 Pro", market, []);

        var terms = r.Missing.Select(t => t.Term.ToLowerInvariant()).ToList();
        Assert.Contains("power supply", terms);
        Assert.DoesNotContain("supply", terms);
        Assert.DoesNotContain("power", terms);
    }

    [Fact]
    public void No_two_offered_terms_repeat_a_word()
    {
        // eBay's results for one item are near-copies of each other, so every window of a common
        // title scores the same 100%: "power supply", "supply bitcoin" and "bitcoin miner" all
        // pass, and offered together they read as three findings when they are one sentence sliced
        // three ways. Worse, applying all three writes "supply" and "bitcoin" into the title twice
        // — in eighty characters, paid for out of words the seller needed.
        List<string> homogeneous =
        [
            "Antminer S19 Pro Power Supply Bitcoin Miner APW12",
            "Antminer S19 Pro Power Supply Bitcoin ASIC APW12",
            "Antminer S19 Pro Power Supply Bitcoin Miner APW12 Unit",
            "Antminer S19 Pro Power Supply Bitcoin Miner APW12 Tested",
            "Antminer S19 Pro Power Supply Bitcoin Miner APW12 Working",
            "Antminer S19 Pro Power Supply Bitcoin Miner APW12 Complete",
        ];

        var r = SearchTermMiner.Mine("Antminer S19 Pro", homogeneous, []);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in r.Missing.Concat(r.Shared).SelectMany(t => SearchTermMiner.Tokens(t.Term)))
            Assert.True(used.Add(word), $"“{word}” was offered in two different terms");

        // And a phrase offered as an addition is entirely new — half a phrase the seller already
        // wrote spends their characters repeating themselves.
        var mine = SearchTermMiner.Tokens("Antminer S19 Pro");
        foreach (var t in r.Missing.Where(t => SearchTermMiner.Tokens(t.Term).Count > 1))
            Assert.All(SearchTermMiner.Tokens(t.Term), w => Assert.DoesNotContain(w, mine));
    }

    [Fact]
    public void The_spelling_sellers_actually_use_is_the_one_offered()
    {
        // Rebuilt from tokens this would come back as "sha 256", which is not what anyone types
        // and not what the market wrote.
        var r = SearchTermMiner.Mine("Antminer S19 Pro Bitcoin Miner", AntminerMarket(), []);

        Assert.Contains(r.Missing, t => t.Term.Equals("SHA-256", StringComparison.Ordinal));
    }

    // ── Building the title ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_built_title_never_passes_eBays_eighty_characters()
    {
        var r = SearchTermMiner.Mine("Antminer S19", AntminerMarket(), []);

        Assert.NotEqual("", r.SuggestedTitle);
        Assert.True(r.SuggestedTitle.Length <= SearchTermMiner.MaxTitleLength,
            $"built a {r.SuggestedTitle.Length}-character title: {r.SuggestedTitle}");
    }

    [Fact]
    public void Nothing_the_seller_wrote_is_removed_reordered_or_reworded()
    {
        const string mine = "Antminer S19 Pro (my own note)";
        var (built, added) = SearchTermMiner.BuildTitle(mine, ["Bitcoin", "SHA-256"]);

        Assert.StartsWith(mine, built, StringComparison.Ordinal);
        Assert.Equal(["Bitcoin", "SHA-256"], added);
    }

    [Fact]
    public void A_term_that_does_not_fit_is_dropped_not_squeezed_in()
    {
        var full = new string('x', 70);   // 70 + 1 space + 8 = 79 fits; anything longer does not
        var (built, added) = SearchTermMiner.BuildTitle(full, ["12345678", "123456789"]);

        Assert.Equal(full + " 12345678", built);
        Assert.Equal(["12345678"], added);
    }

    [Fact]
    public void A_term_already_in_the_title_is_not_appended_a_second_time()
    {
        var (built, added) = SearchTermMiner.BuildTitle("Bitmain Antminer S19 Pro", ["Antminer", "Bitcoin"]);

        Assert.Equal("Bitmain Antminer S19 Pro Bitcoin", built);
        Assert.Equal(["Bitcoin"], added);
    }

    [Fact]
    public void With_no_title_to_build_on_nothing_is_built()
    {
        // A title assembled purely out of other people's keywords describes their items.
        var (built, added) = SearchTermMiner.BuildTitle("   ", ["Bitcoin", "Antminer", "SHA-256"]);

        Assert.Equal("", built);
        Assert.Empty(added);
    }

    // ── The query the market is read with ──────────────────────────────────────────────────────

    [Fact]
    public void A_part_number_is_the_tightest_handle_and_is_used_when_there_is_one()
    {
        Assert.Equal("Bitmain S19PRO-110", SearchTermMiner.BuildQuery(
            "Bitmain Antminer S19 Pro 110TH/s Bitcoin Miner", brand: "Bitmain", mpn: "S19PRO-110"));

        // Already carries the maker's name — no point saying it twice.
        Assert.Equal("Bitmain S19PRO", SearchTermMiner.BuildQuery(
            "Antminer S19 Pro", brand: "Bitmain", mpn: "Bitmain S19PRO"));
    }

    [Fact]
    public void eBays_own_word_for_there_isnt_one_is_not_searched_for()
    {
        // "Does Not Apply" is on thousands of listings; searching it returns thousands of
        // unrelated ones, and every count computed off that corpus would be noise.
        var q = SearchTermMiner.BuildQuery("Bitmain Antminer S19 Pro Bitcoin Miner",
                                           brand: "Bitmain", mpn: "Does Not Apply");

        Assert.DoesNotContain("Does Not Apply", q, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Bitmain Antminer", q, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_title_is_not_a_query()
    {
        // The Browse API ANDs the words together, so an 80-character title matches itself and
        // nothing else — which returns an empty market and a report claiming the title is fine.
        var q = SearchTermMiner.BuildQuery(
            "Bitmain Antminer S19 Pro 110TH/s Bitcoin Miner ASIC SHA-256 With PSU Tested Working");

        Assert.True(q.Split(' ').Length <= 6, "the query is still title-length: " + q);
    }

    [Fact]
    public void The_query_skips_words_that_are_only_ever_noise()
    {
        var q = SearchTermMiner.BuildQuery("RARE! Bitmain Antminer S19 Pro FREE SHIPPING");

        Assert.DoesNotContain("RARE", q, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FREE", q, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Antminer", q, StringComparison.Ordinal);
    }

    // ── Item Specifics read off the market ─────────────────────────────────────────────────────

    private static List<CategoryAspect> MiningAspects() =>
    [
        new() { Name = "Brand",     Required = true,  Values = ["Bitmain", "MicroBT", "Canaan"] },
        new() { Name = "Hash Algorithm", Recommended = true, Values = ["SHA-256", "Scrypt", "Ethash"] },
    ];

    [Fact]
    public void A_specific_the_market_agrees_on_is_offered_with_its_count()
    {
        var suggestions = SearchTermMiner.SuggestSpecifics(MiningAspects(), new Dictionary<string, string>(), AntminerMarket());

        var brand = suggestions.FirstOrDefault(s => s.Name == "Brand");
        Assert.NotNull(brand);
        Assert.Equal("Bitmain", brand!.Value);
        Assert.True(brand.Required);
        Assert.True(brand.AgreeCount >= 2);
        Assert.True(brand.AgreeCount <= brand.VoteCount);
    }

    [Fact]
    public void Only_values_from_eBays_own_list_are_ever_offered()
    {
        // The worst case has to be a legal value that doesn't fit the item, never a value that
        // fails the publish. A free-text aspect has no list to check against, so it gets nothing.
        List<CategoryAspect> freeText = [new() { Name = "Model", Required = true, Values = [] }];

        Assert.Empty(SearchTermMiner.SuggestSpecifics(freeText, new Dictionary<string, string>(), AntminerMarket()));
    }

    [Fact]
    public void A_specific_the_seller_already_answered_is_left_alone()
    {
        var answered = new Dictionary<string, string> { ["Brand"] = "MicroBT" };

        var suggestions = SearchTermMiner.SuggestSpecifics(MiningAspects(), answered, AntminerMarket());

        Assert.DoesNotContain(suggestions, s => s.Name == "Brand");
    }

    [Fact]
    public void An_answer_given_under_the_sellers_own_name_for_the_field_still_counts_as_answered()
    {
        // The seller typed it as a custom row called "Manufacturer". Offering "Brand" on top of
        // that sends the same fact twice under two names.
        var answered = new Dictionary<string, string> { ["Manufacturer"] = "Bitmain" };

        var suggestions = SearchTermMiner.SuggestSpecifics(MiningAspects(), answered, AntminerMarket());

        Assert.DoesNotContain(suggestions, s => s.Name == "Brand");
    }

    [Fact]
    public void A_market_split_down_the_middle_is_the_app_not_knowing_and_it_says_nothing()
    {
        List<string> split =
        [
            "Bitmain Antminer S19 Pro Bitcoin Miner",
            "Bitmain Antminer S19 Pro Bitcoin Miner Tested",
            "Bitmain Antminer S19 Pro Bitcoin Miner Working",
            "MicroBT Whatsminer M30S Bitcoin Miner",
            "MicroBT Whatsminer M30S Bitcoin Miner Tested",
            "MicroBT Whatsminer M30S Bitcoin Miner Working",
        ];

        var suggestions = SearchTermMiner.SuggestSpecifics(MiningAspects(), new Dictionary<string, string>(), split);

        Assert.DoesNotContain(suggestions, s => s.Name == "Brand");
    }

    [Fact]
    public void A_legal_claim_is_never_read_off_a_strangers_listing()
    {
        // Country of origin, warranty and the rest are on AspectMatcher's refusal list because
        // they are claims with consequences. Lifting one off a competitor's title is worse than
        // guessing it from the seller's own words, not better.
        List<CategoryAspect> legal =
        [
            new() { Name = "Country/Region of Manufacture", Required = true, Values = ["China", "United States"] },
        ];
        List<string> allChina =
        [
            "Bitmain Antminer S19 Pro Bitcoin Miner Made in China",
            "Bitmain Antminer S19 Pro China Bitcoin Miner",
            "Antminer S19 Pro Bitcoin Miner China Import",
            "Bitmain Antminer S19 Pro Bitcoin Miner from China",
            "Antminer S19 Pro China Bitcoin ASIC Miner",
            "Bitmain Antminer S19 Pro Bitcoin Miner China Stock",
        ];

        Assert.Empty(SearchTermMiner.SuggestSpecifics(legal, new Dictionary<string, string>(), allChina));
    }

    [Fact]
    public void A_thin_corpus_answers_no_specifics_either()
    {
        var suggestions = SearchTermMiner.SuggestSpecifics(
            MiningAspects(), new Dictionary<string, string>(), AntminerMarket().Take(3).ToList());

        Assert.Empty(suggestions);
    }

    [Fact]
    public void The_specifics_that_stop_a_publish_are_listed_first()
    {
        var suggestions = SearchTermMiner.SuggestSpecifics(MiningAspects(), new Dictionary<string, string>(), AntminerMarket());

        Assert.True(suggestions.Count >= 2, "expected both a required and a recommended suggestion");
        Assert.True(suggestions[0].Required, "a required specific was not offered first");
    }
}
