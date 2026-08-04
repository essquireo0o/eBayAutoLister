namespace ING_eBay_AutoLister.Models;

// What the seller sends to have their title checked against the search they are competing in.
// Everything here is already on the form; nothing extra is asked of them.
public sealed class SearchTermRequest
{
    public string Title { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Mpn { get; set; } = "";
    public Dictionary<string, string> ItemSpecifics { get; set; } = [];
}

// One word or phrase, and the evidence for it. The counts are the whole point: "used by 31 of the
// 50 listings eBay ranks first" is a reason, and "recommended keyword" is not.
public sealed class SearchTerm
{
    // The term as sellers actually write it — the commonest spelling seen in real titles, so
    // "SHA-256" doesn't come back as "sha 256".
    public string Term { get; set; } = "";

    // How many of eBay's top-ranked results for this search carry it, out of how many were read.
    public int RankedCount { get; set; }
    public int RankedTotal { get; set; }

    // And how many of the listings that actually SOLD carried it. Zero here doesn't mean unsold —
    // it means the sold-comps database had nothing to say, which SoldTotal makes visible.
    public int SoldCount { get; set; }
    public int SoldTotal { get; set; }

    // Share of the ranked results, rounded. What the chip shows.
    public int SharePercent { get; set; }

    // Already in the seller's title. Kept in the answer rather than filtered out: "you have the
    // three words that matter" is worth as much to a seller as a list of what they're missing.
    public bool InYourTitle { get; set; }
}

// An Item Specific eBay's own value list can answer, read off the titles of the listings that
// rank and sell for this search. Offered, never written: the counts say how much agreement there
// was, and the seller decides whether it is true of the item in front of them.
public sealed class MarketSpecificSuggestion
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Required { get; set; }
    public bool Recommended { get; set; }

    // How many listings named this value, out of how many named any value for this specific.
    public int AgreeCount { get; set; }
    public int VoteCount { get; set; }
}

public sealed class SearchTermReport
{
    // ok | thin_market | no_data | bad_input
    public string Status { get; set; } = "ok";
    public string Headline { get; set; } = "";
    public string Message { get; set; } = "";

    // The words actually searched for, which is rarely the whole 80-character title. Shown so a
    // seller looking at a surprising answer can see what was asked.
    public string Query { get; set; } = "";

    // Where the corpus came from, in words rather than a slug.
    public string SourceLabel { get; set; } = "";
    public int RankedTotal { get; set; }
    public int SoldTotal { get; set; }

    public string YourTitle { get; set; } = "";
    public int YourTitleLength { get; set; }
    public int FreeCharacters { get; set; }

    // Ranked strongest first. Missing = worth adding; Shared = already earned.
    public List<SearchTerm> Missing { get; set; } = [];
    public List<SearchTerm> Shared { get; set; } = [];

    // The seller's own title with the top missing terms appended, never over eBay's 80. Empty
    // when there is nothing to add or nothing to build on.
    public string SuggestedTitle { get; set; } = "";
    public List<string> AddedTerms { get; set; } = [];

    // ok | no_category | not_connected | error | none
    public string AspectStatus { get; set; } = "ok";
    public string AspectMessage { get; set; } = "";
    public List<MarketSpecificSuggestion> Specifics { get; set; } = [];
}
