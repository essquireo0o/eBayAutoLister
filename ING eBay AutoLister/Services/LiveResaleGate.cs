using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Whether eBay will let the seller list the thing on screen at all, and on what terms.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every number on the live card is the price of an eBay listing.
/// The resale figure is what one fetched on eBay, the sell-through is the share of eBay listings
/// that sold, the break-even is after eBay's cut, and the ceiling is the bid that keeps all of it
/// clearing a target. None of that is a price at all if the listing is not allowed to exist — and
/// replica handbags, loose ammunition, swatched makeup, vape kits and sealed bottles of bourbon
/// cross live-selling feeds nightly. Priced against genuine sold comps, every one of them reads as
/// a spectacular flip.
/// </para>
/// <para>
/// <b>Why this is the one read allowed to overrule the call.</b> Everything else on this card that
/// moves a number moves it because the object is worth less than the comps suggest — a slide, a
/// worse condition, a long wait. Those are haircuts, and haircuts are argued about in percent. This
/// is not a haircut. A lot eBay refuses to list has no eBay resale price, so the ceiling above it is
/// not too high, it is a price for a different transaction entirely. Correcting the call is the only
/// honest thing to do with that, and the badge says <c>CAN'T LIST IT</c> rather than a dollar figure.
/// The rest of the card is deliberately left standing: what the genuine article fetches is still
/// true, still shown, and is exactly the number that explains why the lot was tempting.
/// </para>
/// <para>
/// <b>The asymmetry the vocabulary is tuned to</b>, and it is the same one <see cref="LiveCondition"/>
/// and <see cref="LiveTrend"/> are built around. A rule that fires when it should not costs a lot
/// somebody else wins — invisible, and there is another in four minutes, and the strip names the
/// matched words so the seller can retype the name and carry on. A rule that stays quiet when it
/// should not costs the whole purchase, on a live sale with no undo, and the loss only shows up when
/// eBay takes the listing down. So where a word is ambiguous this errs toward saying something, and
/// the two places that would produce constant noise on ordinary lots — bare "ammo" on a collectible
/// ammo can, "scotch" on a roll of tape — are carved out by hand rather than by loosening the rule.
/// </para>
/// <para>
/// <b>What it refuses to claim.</b> It never says a lot is genuine or fake: it reads a NAME, which
/// is whatever the auctioneer typed, and a replica advertised as real matches nothing here. It never
/// says the seller lacks a licence — the restricted rules state eBay's condition and leave the
/// answer to the person who knows it. It never prices anything: no ceiling, resale figure, median or
/// break-even moves for anything found here, including the authentication leg, whose cost is days
/// rather than dollars. And it puts no probability on an authentication failing, because nobody
/// measured one.
/// </para>
/// <para>
/// Pure and deterministic: no clock, no network, no state. A card re-priced from held comps re-runs
/// exactly this reading and gets exactly this answer, in microseconds, which is what lets it sit on
/// the path a bid moving every two seconds runs down.
/// </para>
/// </remarks>
public static class LiveResaleGate
{
    /// <summary>
    /// Roughly how many extra days eBay's authentication leg adds before the money lands: the item
    /// goes to the hub, gets inspected, and goes on to the buyer, and the payout waits for all of it.
    /// </summary>
    /// <remarks>
    /// A <b>stated assumption</b> and not a measurement — the app has never watched one go through.
    /// It is reported on the strip and in the spoken line and is deliberately not added to
    /// <see cref="LiveBidCard.DaysToCash"/>: that figure is estimated from real sell-through data,
    /// and folding a guess into it would make a measured number quietly part guess.
    /// </remarks>
    public const int AuthenticationDays = 4;

    /// <summary>
    /// One line of eBay's rulebook: what it matches, how bad it is, and what eBay actually says.
    /// </summary>
    /// <param name="Threshold">The sale price at or above which an Authenticity Guarantee category
    /// routes through the hub. Zero on every rule that is not one.</param>
    private sealed record Rule(string Name, Regex Words, string Verdict, decimal Threshold, string Policy);

    private static Regex Of(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The catalogue, checked in this order: what cannot be listed, then what can be listed under
    /// conditions, then what has to be authenticated first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a snapshot, and it says so on the strip.</b> It is the app's own reading of eBay's
    /// published selling policies as of August 2026, written down in one table so a seller who
    /// disagrees with a call has one place to look and the app has one place to be corrected. eBay
    /// moves these — the Authenticity Guarantee thresholds especially — and a rule that has drifted
    /// produces a sentence naming the rule and the words that fired it, which is a thing a seller can
    /// see is stale. A silent hard-coded threshold would not be.
    /// </para>
    /// <para>
    /// The authenticated rules are ordered <b>cheapest threshold first</b>, on the same asymmetry as
    /// everything else here: a Cartier bracelet read against jewellery's $500 bar and a Cartier watch
    /// read against the same $500 bar both end up saying "this goes through the authenticator",
    /// which is true of both; the other order would silently clear the bracelet at the watch bar.
    /// </para>
    /// </remarks>
    private static readonly Rule[] Rules =
    [
        // ── Cannot be listed ─────────────────────────────────────────────────────────────────
        new("Replicas and counterfeits",
            // Only words that mean the thing itself. "Fake" and "dupe" are deliberately absent:
            // fake fur, fake plants and fake eyelashes are ordinary eBay stock, and a rule that
            // stopped a lot of artificial flowers would be a rule the seller learns to ignore —
            // which is the one failure a stop-the-call read cannot survive.
            Of(@"\b(?:replicas?|knock[\s-]?offs?|counterfeits?|bootlegs?|unauthorized\s+authentic|rep\s+batch|1:1\s+rep)\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay bans replicas and counterfeits outright — the listing comes down and the account " +
            "takes the strike. There is no eBay resale price for one at any bid."),

        new("Ammunition and gunpowder",
            // The empty container is a legitimate collectible and a common live-show lot, so it is
            // carved out by hand rather than by dropping the word.
            Of(@"\b(?:ammunition|ammo\b(?!\s*(?:cans?|boxe?s?|crates?|pouch|belt|tins?))|gunpowder|black\s+powder|live\s+rounds?)\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay does not allow ammunition, gunpowder or loaded rounds in any category. Empty cases, " +
            "cans and boxes are fine — the contents are not."),

        new("Firearms and automatic knives",
            // Deliberately narrow. "Rifle" is missing because a rifle scope is ordinary eBay stock,
            // "handgun" because holsters, cases and grips are, and "receiver" because most of them
            // are stereo equipment.
            Of(@"\b(?:firearms?|switchblades?|balisongs?|butterfly\s+knife|automatic\s+knife)\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay does not allow firearms or automatic and switchblade knives. Most parts and " +
            "accessories are allowed — if that is what this is, take the word out of the name and price it again."),

        new("Ivory and protected species",
            // Qualified every time, because "ivory" on a live feed is nearly always a colour.
            Of(@"\b(?:elephant\s+ivory|real\s+ivory|genuine\s+ivory|carved\s+ivory|ivory\s+tusk|rhino(?:ceros)?\s+horn|sea\s+turtle\s+shell)\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay bans ivory and protected-species material regardless of age or paperwork."),

        new("Used cosmetics",
            Of(@"\b(?:used|swatched|opened|tested)\s+(?:makeup|cosmetics?|lipsticks?|lip\s+gloss|foundation|mascara|eyeshadows?|perfumes?|cologne)\b|\bswatched\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay does not allow used cosmetics — swatched, tested or opened. Sealed and unused is " +
            "a different lot and prices differently."),

        new("Tobacco, vapes and nicotine",
            // Bare "cigar" is missing on purpose: the box is the collectible, and it is legal.
            Of(@"\b(?:vapes?|e-?cigs?|e-?cigarettes?|e-?liquids?|e-?juice|nicotine|juul|cigarettes?|chewing\s+tobacco|snus)\b"),
            LiveGateVerdicts.Blocked, 0m,
            "eBay does not allow tobacco, nicotine or vaping products. Empty tins, boxes and " +
            "advertising are collectibles and are fine."),

        // ── Can be listed, on conditions only the seller knows ───────────────────────────────
        new("Alcohol",
            Of(@"\b(?:whisk(?:e)?y|bourbon|scotch\b(?!\s+tape)|vodka|tequila|mezcal|\brum\b|wine|champagne|\bbeer\b|liquor|spirits)\b"),
            LiveGateVerdicts.Restricted, 0m,
            "eBay only allows pre-approved sellers to list wine, and otherwise only EMPTY collectible " +
            "containers. If there is liquid in it you cannot list it, whatever it is worth."),

        new("Hemp, CBD and delta products",
            Of(@"\b(?:cbd|hemp|kratom|delta[\s-]?[89]|thc)\b"),
            LiveGateVerdicts.Restricted, 0m,
            "eBay restricts hemp and CBD to a narrow list of topical products from approved sellers. " +
            "Anything ingestible is refused."),

        new("Event tickets",
            Of(@"\b(?:concert|event|game|festival|sports)\s+tickets?\b|\btickets?\s+to\b"),
            LiveGateVerdicts.Restricted, 0m,
            "eBay restricts event tickets — the listing has to state the event, the seat and the " +
            "date, and resale is capped by law in several states."),

        new("Recalled goods",
            Of(@"\brecalled\b"),
            LiveGateVerdicts.Restricted, 0m,
            "eBay does not allow recalled goods to be relisted. Check the recall is not the reason " +
            "this is cheap."),

        // ── Can be listed, through eBay's authenticator ──────────────────────────────────────
        new("Streetwear",
            Of(@"\b(?:supreme|off[\s-]?white|bape|a\s+bathing\s+ape|stone\s+island|palace\s+skateboards)\b"),
            LiveGateVerdicts.Authenticated, 100m,
            "eBay routes streetwear over $100 through its authenticator before the buyer sees it."),

        new("Sneakers",
            Of(@"\b(?:sneakers?|air\s+jordans?|jordan\s+\d+|yeezys?|air\s+max|air\s+force\s+1|dunk\s+(?:low|high)|travis\s+scott)\b"),
            LiveGateVerdicts.Authenticated, 150m,
            "eBay routes sneakers over $150 through its authenticator before the buyer sees it."),

        new("Graded and trading cards",
            // No bare "RC" for rookie card: remote-control everything is a live-show staple.
            Of(@"\b(?:psa\s*\d+|bgs\s*\d+|sgc\s*\d+|cgc\s*\d+|graded\s+cards?|rookie\s+card|pok[eé]mon|topps|panini|upper\s+deck)\b"),
            LiveGateVerdicts.Authenticated, 250m,
            "eBay routes trading cards over $250 through its authenticator before the buyer sees it."),

        new("Handbags",
            Of(@"\b(?:handbags?|birkin|kelly\s+bag|louis\s+vuitton|gucci|chanel|prada|herm[eè]s|balenciaga|dior|fendi|bottega|goyard)\b"),
            LiveGateVerdicts.Authenticated, 500m,
            "eBay routes handbags over $500 through its authenticator before the buyer sees it."),

        new("Jewellery",
            Of(@"\b(?:diamonds?|(?:14|18|22)\s*k\s*gold|solid\s+gold|tiffany\s*&?\s*co|van\s+cleef|cartier)\b"),
            LiveGateVerdicts.Authenticated, 500m,
            "eBay routes jewellery over $500 through its authenticator before the buyer sees it."),

        new("Watches",
            Of(@"\b(?:rolex|omega\s+(?:seamaster|speedmaster|constellation)|patek|audemars|tag\s*heuer|breitling|panerai|vacheron|jaeger|grand\s+seiko)\b"),
            LiveGateVerdicts.Authenticated, 2_000m,
            "eBay routes watches over $2,000 through its authenticator before the buyer sees it."),
    ];

    /// <summary>
    /// Reads the lot's name against eBay's selling policies.
    /// </summary>
    /// <param name="item">
    /// The lot's name as typed or read off the show. The NAME, not the category — an auctioneer
    /// types what the thing is, and a category picked from a dropdown says nothing about whether
    /// the object in the box is a replica of it.
    /// </param>
    /// <param name="perUnitResale">
    /// What ONE of these resells for on eBay, when the comps priced it. The Authenticity Guarantee
    /// thresholds are on the sale price, so this is the figure they are checked against — per unit,
    /// because a lot of four $200 sneakers is four listings and each of them is over the bar. Null
    /// leaves an authenticated category reported with the threshold unchecked, which is the honest
    /// state of a card nothing priced.
    /// </param>
    public static LiveGateRead Read(string? item, decimal? perUnitResale)
    {
        var read = new LiveGateRead
        {
            PricedAt = perUnitResale is > 0m ? Math.Round(perUnitResale.Value, 2) : 0m,
        };

        var name = (item ?? "").Trim();
        if (name.Length == 0) return read;

        read.Readable = true;
        read.Verdict = LiveGateVerdicts.Clear;

        foreach (var rule in Rules)
        {
            var hit = rule.Words.Match(name);
            if (!hit.Success) continue;

            read.RuleName = rule.Name;
            read.Matched = hit.Value.Trim();
            read.Policy = rule.Policy;
            read.ThresholdPrice = rule.Threshold;

            // An authenticated category under its own threshold is not a finding. It stays CLEAR and
            // keeps the rule's sentence, because "sneakers go through the hub over $150 and these
            // price at $60" is a useful thing to have read and a useless thing to be warned about.
            if (rule.Verdict == LiveGateVerdicts.Authenticated
                && read.PricedAt > 0m && read.PricedAt < rule.Threshold)
            {
                Say(read);
                return read;
            }

            read.Verdict = rule.Verdict;
            read.OverThreshold = rule.Verdict == LiveGateVerdicts.Authenticated && read.PricedAt >= rule.Threshold;
            read.ExtraDaysToCash = rule.Verdict == LiveGateVerdicts.Authenticated ? AuthenticationDays : 0;
            Say(read);
            return read;
        }

        Say(read);
        return read;
    }

    /// <summary>
    /// Every sentence this block puts on a screen. Written here, next to the rules, for the reason
    /// every other block on this card writes its own: a sentence assembled in the browser out of a
    /// verdict string is a second opinion about a policy, and it is the one on screen.
    /// </summary>
    private static void Say(LiveGateRead read)
    {
        if (!read.Readable) return;

        // The clear lot, and there is one of these on nearly every card. It says so out loud rather
        // than showing nothing, because a block that only appears once something is wrong is a block
        // whose silence means both "eBay is fine with this" and "nothing ever looked".
        if (read.Verdict == LiveGateVerdicts.Clear)
        {
            read.Headline = read.RuleName.Length > 0
                ? $"{read.RuleName.ToLowerInvariant()} — under eBay's {read.ThresholdPrice:C0} authentication bar"
                : "nothing in this name is restricted on eBay";

            read.Note = read.RuleName.Length > 0
                ? $"{read.Policy} This one prices at {read.PricedAt:C} a unit, so it ships straight to the buyer."
                : "Checked against eBay's banned, restricted and authenticated categories as the app " +
                  "has them written down. It reads the name, not the object.";
            return;
        }

        var matched = $"“{read.Matched}” is in this lot's name";

        switch (read.Verdict)
        {
            case LiveGateVerdicts.Blocked:
                read.Tag = "CAN'T LIST";
                read.Headline = $"eBay won't let you list this — {read.RuleName.ToLowerInvariant()}";
                read.Note = $"{read.Policy} Matched because {matched}.";
                // The badge's own line. It leads with the consequence rather than the rule, because
                // it is read in the two seconds before a hand goes up.
                read.Reason =
                    $"eBay won't list this: {read.RuleName.ToLowerInvariant()} ({matched}). Nothing below is a " +
                    "price you can realise — a resale figure on this card is what the ALLOWED version fetches.";
                read.Warning =
                    $"{read.RuleName}: {read.Policy} If the name is wrong — and it is a name typed by an " +
                    "auctioneer — fix it and price it again.";
                return;

            case LiveGateVerdicts.Restricted:
                read.Tag = "CHECK FIRST";
                read.Headline = $"eBay restricts this — {read.RuleName.ToLowerInvariant()}";
                read.Note = $"{read.Policy} Matched because {matched}.";
                read.Warning =
                    $"{read.RuleName}: {read.Policy} The app cannot tell whether you can meet that — " +
                    "you can, in about ten seconds, and nobody else on this screen can.";
                return;

            case LiveGateVerdicts.Authenticated when read.OverThreshold:
                read.Tag = $"+{AuthenticationDays} DAYS TO CASH";
                read.Headline =
                    $"goes through eBay's authenticator — {read.RuleName.ToLowerInvariant()} over " +
                    $"{read.ThresholdPrice:C0}";
                read.Note =
                    $"{read.Policy} At {read.PricedAt:C} a unit this one does. It ships to eBay first, so " +
                    $"budget about {AuthenticationDays} more days before the money lands — and if it fails " +
                    "inspection the sale is refunded and you are holding it.";
                read.Warning =
                    $"This resells at {read.PricedAt:C}, over the {read.ThresholdPrice:C0} bar, so it goes to " +
                    $"eBay's authenticator before the buyer sees it: about {AuthenticationDays} extra days to " +
                    "cash, and a fake is refunded in full. You are judging it through a camera.";
                return;

            // The category matched and nothing priced it. Reported rather than resolved: the
            // threshold is on a sale price the card does not have.
            case LiveGateVerdicts.Authenticated:
                read.Tag = "AUTHENTICATED OVER THE BAR";
                read.Headline =
                    $"{read.RuleName.ToLowerInvariant()} — authenticated by eBay over {read.ThresholdPrice:C0}";
                read.Note =
                    $"{read.Policy} Nothing priced this one, so whether it clears that bar is unknown.";
                read.Warning =
                    $"{read.RuleName} over {read.ThresholdPrice:C0} ship to eBay's authenticator first — about " +
                    $"{AuthenticationDays} extra days to cash, and a fake is refunded in full. Nothing priced " +
                    "this lot, so there is no way to tell from here which side of that bar it is on.";
                return;
        }
    }
}
