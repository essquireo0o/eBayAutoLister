# ING Listing Engine — Marketing Posts

Ready-to-post content for each platform. Copy, paste, post.

**Last verified against the shipping product: 2026-08-26.** Every number below was measured, not
estimated — see *Facts* at the bottom for where each one comes from. Screenshots for any post are in
`Desktop\ING Listing Engine screenshots 2026-08-26\` (22 screens, full-page, 2x).

---

## Reddit — r/flipping

**Title:** I built a Windows app that writes your eBay listing from a photo and prices it against 927,000 real sold records — free

**Post:**
I've sold on eBay since 2009 and got tired of two things: writing listings, and guessing at prices.
So I built the tool I wanted.

**ING Listing Engine.** Point your phone at the item, or paste a product link. About ten seconds
later you have a complete listing — title under 80 characters, full HTML description, category, 20+
item specifics, condition, shipping dimensions, photos.

The part I actually care about: **it prices against real sold listings, not guesses.** There are
927,218 sold records in the local database that ships with it, and it will pull live sold comps for
the exact thing you're listing. When there's nothing comparable it says so instead of inventing a
number — and gives you an AI estimate clearly labelled as an estimate, never dressed up as sold data.

Other things it does, because the listing was only ever half the job:

- **Opportunity Finder** — scans for underpriced items worth flipping, with the profit maths after
  fees already done
- **Photo Box** — your phone is the camera. Scan a code, shoot, the photos land on your PC. No app
  to install, and it works on iPhone with no certificate setup
- **Money Made / Tax Pack** — what you actually cleared after fees, and the numbers for your return
- Offers to watchers, aging stock rescue, promoted-rate advice, shipping cost checks

Publishes through eBay's official API. Windows 10 & 11. **No subscription, no per-listing fee, no
card.** You bring your own Anthropic API key, so the AI costs go to Anthropic at cost — I don't mark
them up and I never see your key.

Download: https://inglisting.com

Happy to answer anything. What's the part of listing you'd most want automated?

---

## Reddit — r/Entrepreneur / r/SideProject

**Title:** I shipped an eBay seller tool that prices listings off 927k real sold records. Here's what building it actually taught me.

**Post:**
I was spending 20–30 minutes per eBay listing. Title research, description, category, 20+ item
specifics, then guessing the price. So I built **ING Listing Engine**.

The core loop is: photo or product URL → AI reads it → complete listing in about ten seconds.

**The hard part wasn't the AI. It was being honest about prices.**

Anyone can call a model and get a number. The problem is when the comps are wrong and the number is
confident. Real examples that shaped the product:

- A $1,900 Saab priced at $150, because the only "Saab" records in the database were ECU modules.
  The arithmetic was perfect and the product was wrong.
- A one-ounce gold bar priced at $6.99, because the comps that matched were novelty replicas.

Both are the same bug: the model answered a question it should have refused. So the app now grades
its own evidence — how many comps actually priced this, whether they carry the item's own model
number, and whether they describe the same *kind* of thing. A price backed by twenty matching sales
and a price backed by one loose match do not get displayed the same way. Commodities get priced off
the spot price instead of comps, because weight × purity × spot is arithmetic and a comp is an
opinion.

**What worked:** shipping a real installer instead of a zip. Making it work without a subscription.
Letting people bring their own AI key so the running cost is transparent and I never touch it.

**What's hard:** eBay's Sell API has a lot of edge cases. Item specifics vary wildly by category.
And every "clever" pricing shortcut eventually prices something absurdly and costs a user real money.

**Stack:** ASP.NET Core 10, vanilla JS, Claude API, eBay Sell API, SQLite + MariaDB for the comps.
Ships as a 52 MB Windows installer.

https://inglisting.com

---

## Reddit — r/ecommerce

**Title:** Free Windows tool: AI listing writer + sold-comp pricing + arbitrage finder for eBay sellers

**Post:**
Built this for my own eBay business and it's free to use.

**Listing:** photo or URL in, full listing out — title, description, category, item specifics,
condition, shipping. Publishes through eBay's official API.

**Pricing:** 927,218 sold records locally, plus live sold-comp lookups. It tells you what evidence
is behind every number, and refuses to price something it has no business pricing.

**Sourcing:** the Opportunity Finder scans marketplaces for items worth flipping and shows profit
after eBay fees, shipping and tax — not gross margin.

**After the sale:** Money Made tracks real profit; Tax Pack produces the numbers for your return;
Offers to Watchers, Rescue Aging Stock and Ad Rate Advisor handle the listings that are sitting.

Also does Amazon listings, and a live-auction bidding advisor for Whatnot that will tell you to stop
bidding when the metal in a lot is worth less than the bid.

Windows 10 & 11 · no subscription · bring your own AI key
https://inglisting.com

---

## Hacker News — Show HN

**Title:** Show HN: eBay listing tool that grades its own pricing confidence

**Post:**
I sell on eBay and built a Windows app that writes listings from a photo and prices them against
real sold records — 927,218 of them locally, plus live lookups.

The interesting engineering problem wasn't generating listings. It was **refusing to**.

An early version priced a $1,900 Saab at $150. The comps database contained Saab ECU modules, not
Saabs; the median was computed correctly over the wrong population. Another priced a one-ounce gold
bar at $6.99 off two sold "gold bars" that were novelty replicas.

Both failures look identical from inside the code — a confident median over a healthy sample. So the
app now carries an evidence grade alongside every price: how many comps actually produced the figure
after outlier removal and identity checks, whether any of them carry the item's model or part number,
and whether they describe the same category of object. Only a price that passes all three is allowed
to present itself as a rate rather than a guess. For commodities it stops using comps entirely and
prices off the spot metal price, because "1 oz of gold" has a published answer and a comp does not.

The lesson I keep relearning: for a tool people spend money on the output of, "I don't know" has to
be a first-class answer, and it has to be cheaper to produce than a wrong number.

Stack: ASP.NET Core 10, vanilla JS, Claude API for vision and copy, eBay Sell API, SQLite + MariaDB.
52 MB Windows installer, no subscription, bring your own API key.

https://inglisting.com

---

## eBay Community Forum — Seller Tools Board

**Subject:** Free tool I built — AI listing drafts + sold-comp pricing

Hello all — I've been selling since 2009 and built a tool for my own store that I've made available
free.

What it does:

- Drafts the listing from a photo or a product link — title, description, category, item specifics,
  condition, shipping dimensions
- Prices it against real sold listings, and tells you how much evidence is behind the number
- Finds underpriced items worth buying, with profit after fees
- Tracks what you actually made, and produces tax-time figures
- Sends offers to watchers, rescues aging stock, advises on promoted rates

It publishes through eBay's official API — nothing is scraped from your account and no password is
ever entered anywhere but eBay's own sign-in page.

Runs on your own Windows PC. Your listings, photos and cost basis stay on your machine. No
subscription and no per-listing fee.

https://inglisting.com

Glad to answer questions or take feature requests.

---

## Facebook Group Post (eBay Sellers)

🚀 **Free eBay tool — listing + pricing + sourcing**

📸 Photo or link in → complete listing out, about 10 seconds
📊 Priced against 927,000+ real sold records, with the evidence shown
🔍 Opportunity Finder — what's worth buying, profit after fees
📱 Your phone is the camera — scan, shoot, photos land on your PC
💰 Money Made + Tax Pack — real profit, and your tax numbers
✅ No subscription · no per-listing fee · no card
🖥️ Windows 10 & 11 · publishes through eBay's official API

Bring your own AI key — the running cost goes to Anthropic at cost, never marked up.

👉 https://inglisting.com

---

## Twitter / X Thread

**1/**
I've sold on eBay since 2009. Listing an item took me 20–30 minutes.

So I built a Windows app that does it in about ten seconds — from a photo.

It's free. 🧵

**2/**
Photo or product link in. Out comes the whole listing: title under 80 chars, HTML description,
category, 20+ item specifics, condition, shipping dimensions, photos.

Publishes through eBay's official API.

**3/**
The part that took longest wasn't the AI. It was making it refuse to answer.

An early build priced a $1,900 Saab at $150 — because the only "Saab" records it had were ECU
modules. Perfect arithmetic, wrong product.

**4/**
Another priced a 1 oz gold bar at $6.99, off two sold "gold bars" that were novelty replicas.

Same bug wearing a different hat: a confident median over the wrong population.

**5/**
So every price now carries its evidence: how many comps actually produced it, whether they carry the
item's own model number, whether they're even the same kind of object.

Fails any of those → it says so instead of pretending.

**6/**
For commodities it stops using comps entirely. 1 oz of gold is weight × purity × spot price — a
published fact. A comp is somebody else's Tuesday.

**7/**
It also finds things worth buying, tracks what you actually made after fees, produces your tax
numbers, and turns your phone into the studio camera.

**8/**
Windows 10 & 11. No subscription, no per-listing fee. Bring your own AI key so the cost is yours and
transparent.

https://inglisting.com

---

## LinkedIn Post

After 17 years selling on eBay, the two things that never got faster were writing listings and
pricing them. So I built **ING Listing Engine**.

Photo or product link in — complete listing out in about ten seconds. Title, description, category,
20+ item specifics, condition, shipping. Published through eBay's official API.

The engineering problem that actually mattered was pricing honesty.

An early version priced a $1,900 vehicle at $150, because the comparable-sales database held parts
for that vehicle rather than the vehicle. The median was computed correctly over the wrong
population — which is the most dangerous kind of wrong, because it looks exactly like being right.

The product now grades its own evidence before it will state a price as a rate: how many comparable
sales survived outlier removal and identity matching, whether they carry the item's model number,
and whether they describe the same category of object. Commodities bypass comparables entirely and
price off the published spot rate.

For a tool people commit money against, "I don't have enough evidence" has to be a first-class
answer.

It also handles sourcing, profit tracking and tax figures — the listing was always only half the job.

Free, Windows, no subscription: https://inglisting.com

---

## Indie Hackers Post

**Title:** Shipped an eBay seller tool — the hard part was teaching it to say "I don't know"

Built **ING Listing Engine** for my own eBay business, now free for anyone.

**Numbers:**
- 52 MB Windows installer, v2.6.1
- 927,218 sold records in the local pricing database (2.3 GB)
- ~10 seconds from photo to filled listing
- No subscription; users bring their own Anthropic key so AI cost is theirs, at cost

**What I got wrong first:** I optimised for always producing an answer. That is exactly the wrong
target for a pricing tool. A confident wrong price costs a seller real money, and it is
indistinguishable from a confident right one unless you show the evidence.

**What fixed it:** every price now carries a grade — comps that actually priced it, whether they
match the item's identity, whether they're the same kind of object. Anything short of all three is
labelled an estimate and cannot wear a confident badge. Commodities get priced off spot instead.

**What I'd do differently:** ship the installer signed from day one. An unsigned installer gets a
SmartScreen warning that reads like a virus alert, and no amount of good software argues with that
dialog.

https://inglisting.com

---

## Product Hunt Launch Description

**Tagline:** AI writes your eBay listing from a photo — and prices it against 927,000 real sold records

**Description:**
ING Listing Engine turns a photo or a product link into a complete eBay listing in about ten seconds
— title, description, category, 20+ item specifics, condition, shipping — and prices it against real
sold listings rather than guesses.

What makes it different is what it refuses to do. Every price carries its evidence: how many
comparable sales actually produced it, whether they carry the item's own model number, whether
they're even the same kind of object. When the evidence is thin it says so. Commodities are priced
off the published spot rate instead of comparables, because an ounce of gold has a real answer.

Beyond listing: an Opportunity Finder that shows what's worth buying with profit after fees, a phone
camera that needs no app installed, real profit tracking, tax-time figures, and tools for the
listings that are just sitting there.

Runs on your own Windows PC. Your data stays local. No subscription, no per-listing fee — bring your
own AI key.

---

## YouTube Video Description (for demo video)

ING Listing Engine — write and price an eBay listing in about ten seconds.

In this video: photograph an item with your phone, watch the listing fill itself in, see it priced
against real sold records, and publish it to eBay.

⏱️ Chapters
0:00 The problem — 20 minutes per listing
0:35 Photo to filled listing
2:10 Pricing against 927,000 sold records
3:40 What it does when the evidence is thin
5:00 Opportunity Finder — what's worth buying
6:30 Money Made and Tax Pack
7:45 Installing it

🔗 Download (free, Windows 10 & 11): https://inglisting.com

No subscription. No per-listing fee. Bring your own Anthropic API key — the AI cost is yours, at
cost, and never passes through me.

---

## Facts — measured 2026-08-26

Anything above that is a number came from here. Re-measure before reusing this doc; several of these
change with every release.

| Claim | Value | How it was measured |
|---|---|---|
| Version | 2.6.1 | `<Version>` in the csproj |
| Installer size | 51.8 MB (54,296,696 bytes) | `ING-AutoLister-Setup.msi`, and the byte count served by inglisting.com |
| Sold records | 927,218 | `SELECT COUNT(*) FROM SoldListings`, `C:\INGListing\Data\Marketplace.db` |
| Pricing DB size | 2.3 GB | file size of the same database |
| Demand signals | 14,041 | `SELECT COUNT(*) FROM DemandSignal` |
| Download URL | https://inglisting.com | the site's own download button |
| Platform | Windows 10 & 11 | installer target |

**Claims deliberately removed from the previous version of this file**, because they had stopped
being true:

- *"Single .exe, 108 MB, no install"* — it is a 51.8 MB MSI installer now.
- *"Download from GitHub releases"* — the canonical download is inglisting.com.
- *"100% Freeware"* and *"free public beta"* — the site no longer uses either framing. The accurate
  wording, and what the site says today, is **no subscription and no per-listing fee**.
- *"Key ING-BETA-2025 unlocks all features"* — still true, still built in, but it is not a selling
  point and reads oddly next to a product that no longer calls itself a beta.

**Claims to avoid** unless someone re-measures them: any user count, revenue figure, or "X listings
created" number. None is instrumented, so any figure would be invented.
