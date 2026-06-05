# ING Listing Engine — Marketing Posts

Ready-to-post content for each platform. Copy, paste, post.

---

## Reddit — r/flipping

**Title:** I built an AI tool that fills your entire eBay listing from a product URL — free for 30 days

**Post:**
Hey r/flipping — I've been selling on eBay for years and got tired of the manual listing grind, so I built something.

**ING Listing Engine** — paste any product URL, it reads the page and auto-fills your entire eBay listing in ~10 seconds:

- Title (keyword-optimized, under 80 chars)
- Full HTML description with specs table
- Category + item specifics (brand, model, compatibility, 20+ fields)
- Condition, pricing, shipping dimensions
- Pulls all product photos from the page
- Publishes directly to eBay with one click

**Bulk import:** paste a collection/category page URL and it processes every product on the page at once — each one opens as its own tab, all pre-filled.

It runs on your Windows PC as a single .exe — no install, no subscription to start. 30-day free trial, then $49.99/mo if you want to keep it.

Works for any product category — electronics, clothing, tools, collectibles, sporting goods, whatever you sell.

Download: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

Happy to answer any questions. What features would make this more useful for your workflow?

---

## Reddit — r/Entrepreneur / r/SideProject

**Title:** Launched an AI eBay listing tool — paste URL, AI writes the whole listing. Here's what I learned building it.

**Post:**
Six months ago I was spending 20-30 minutes per eBay listing. Title research, description writing, finding the right category, filling 20+ item specifics fields — all manual.

So I built **ING Listing Engine**. Here's the core loop:

1. Paste any product URL (supplier page, manufacturer site, existing listing)
2. Claude AI reads the page
3. Your entire eBay listing appears in ~10 seconds — title, description, category, item specifics, pricing, photos, shipping

For bulk sellers there's a collection import mode — paste a category page, every product on it gets its own pre-filled tab simultaneously.

**What worked:** Making it a single Windows .exe with no install. Sellers don't want to deal with setup. The 30-day free trial converts way better than a freemium model.

**What's hard:** eBay's Sell API has a lot of edge cases. Item specifics vary wildly by category. Getting the AI prompts right for generic products (not just one niche) took a lot of iteration.

**Stack:** ASP.NET Core 10 backend, vanilla JS frontend, Claude API for analysis, eBay Sell API for publishing. Ships as a single self-contained exe.

Free trial: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

What would make you actually pay for something like this?

---

## Reddit — r/ecommerce

**Title:** Built an AI tool that turns any product URL into a complete eBay listing — 30-day free trial

**Post:**
For anyone selling on eBay: I built **ING Listing Engine**.

Paste any product URL → Claude AI fills your entire listing automatically (title, description, item specifics, photos, pricing, shipping). Direct publish to eBay. Bulk import entire collections at once.

Single Windows exe, no install required. Free 30-day trial.

https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

Works for any product type. Happy to answer questions.

---

## Hacker News — Show HN

**Title:** Show HN: ING Listing Engine – AI fills eBay listings from a product URL

**Post:**
I built a Windows desktop app that takes any product URL and generates a complete, publish-ready eBay listing using Claude AI.

The core flow: paste URL → Claude reads the page → title (under 80 chars, keyword-optimized), full HTML description, category ID, 20+ item specifics, photos pulled from the page, pricing estimate, shipping dimensions. One click publishes via eBay Sell API.

For bulk sellers: paste a collection page URL, every product gets processed in parallel into separate tabs.

**Technical details:**
- ASP.NET Core 10 minimal API, serves embedded wwwroot via EmbeddedFileProvider (single-file exe)
- No install — ContentRootPath pinned to exe directory so credentials survive single-file extraction
- Claude claude-sonnet-4-6 with structured JSON prompts for listing data
- eBay Sell API for inventory creation and OAuth token management
- Stripe for $49.99/mo Pro subscriptions
- 30-day trial enforced server-side with TrialGuard middleware

The single-file exe challenge was the interesting part — standard PublishSingleFile doesn't embed wwwroot content. Had to mark UI files as EmbeddedResource and use EmbeddedFileProvider. Also had to pin ContentRootPath to the exe directory rather than the temp extraction dir so credentials.json persists.

Download (free trial): https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest
Source: https://github.com/essquireo0o/ING-eBay-Autolister

---

## eBay Community Forum — Seller Tools Board

**Title:** Free AI listing tool — paste any URL and it fills your entire listing automatically (30-day trial)

**Post:**
Hey everyone — I wanted to share a tool I built specifically for eBay sellers.

**ING Listing Engine** automates the listing creation process using AI. Here's how it works:

**Single product:** Paste any product URL (your supplier, the manufacturer's site, another marketplace listing) → AI reads the page and fills your entire eBay listing automatically:
- Keyword-optimized title (80 chars)
- Professional HTML description with spec table
- eBay category selection
- Item specifics (brand, model, compatibility, condition, and 20+ more)
- Product photos pulled from the source page
- Estimated price based on current eBay sold comps
- Shipping package dimensions

**Bulk import:** Paste a category or collection page URL → every product on the page gets processed into its own tab simultaneously. Review and publish each one.

You connect your own eBay seller account via OAuth — it publishes directly using the eBay Sell API.

Works for any product category. Free 30-day trial, then $49.99/month.

Download here: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

Windows only. No install required — double-click the .exe and you're running.

---

## Facebook Group Post (eBay Sellers)

**Post:**
🚀 For anyone who lists on eBay — I built a tool that saves hours every week.

**ING Listing Engine** — paste any product URL, AI fills your entire eBay listing in about 10 seconds.

✅ Title, description, category, item specifics — all auto-filled
✅ Pulls product photos from the source page
✅ Bulk import — paste a collection URL, every product gets its own pre-filled tab
✅ One click publishes directly to eBay
✅ Works for ANY product — electronics, clothing, tools, collectibles, sports gear, anything

FREE 30-day trial. No credit card needed to start.

👉 Download: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

Windows only. Double-click to run — no install.

Drop a comment if you have questions!

---

## Twitter / X Thread

**Tweet 1 (main):**
I built an AI tool for eBay sellers. Paste any product URL → complete listing filled in 10 seconds. Title, description, category, 20+ item specifics, photos, pricing. One click publishes to eBay.

30-day free trial: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

**Tweet 2:**
The bulk import feature is wild — paste a supplier's category page URL and every product on it gets processed simultaneously, each in its own tab, all pre-filled and ready to publish.

**Tweet 3:**
Works for anything you sell. Electronics, clothing, tools, collectibles, sporting goods, auto parts — the AI adapts to any product category automatically.

**Tweet 4:**
Built on Claude AI + eBay Sell API. Ships as a single Windows .exe — no install, just double-click. Your credentials stay local, never leave your machine.

**Tweet 5:**
Free 30 days. Then $49.99/mo if you want to keep it.

For eBay sellers who spend hours on listings — this is for you.
👇 https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

---

## LinkedIn Post

**Post:**
I spent 6 months building a product that solves a problem I had personally: eBay listings take forever to create manually.

**ING Listing Engine** uses Claude AI to turn any product URL into a complete, publish-ready eBay listing in about 10 seconds.

What it fills automatically:
→ Keyword-optimized title (80 chars, eBay ranking matters)
→ Professional HTML product description
→ eBay category selection
→ 20+ item specifics fields
→ Product photos from the source page
→ Price estimate from current eBay sold comps
→ Shipping dimensions and packaging

For sellers with large inventory: paste a collection page URL and every product gets processed in parallel — each into its own draft tab.

Built with ASP.NET Core 10 + Claude API + eBay Sell API. Ships as a single Windows exe with no install required.

30-day free trial, then $49.99/mo.

If you sell on eBay or know someone who does: https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest

#ecommerce #ebay #AI #automation #SaaS

---

## Indie Hackers Post

**Title:** I automated the worst part of selling on eBay — the listing form

**Post:**
**The problem:** Every eBay listing requires filling out 15–20 fields manually. Title research, description writing, finding the right category ID, item specifics (some categories have 30+ fields), condition notes, shipping dimensions. Multiply that by 50 products and it's a full-time job.

**What I built:** ING Listing Engine — paste a product URL, Claude AI reads the page and fills your entire listing. Takes about 10 seconds. One more click publishes to eBay directly via the Sell API.

**Numbers so far:**
- Single exe file, 108 MB, bundles the entire .NET runtime
- Works for any product category
- 30-day free trial to remove friction
- $49.99/mo after trial

**Interesting technical challenges:**
1. Single-file .NET exe with embedded UI — had to use EmbeddedFileProvider instead of static files middleware, mark HTML/CSS/JS as EmbeddedResource in csproj
2. ContentRootPath for single-file apps points to the temp extraction dir — credentials would be lost on restart. Fixed by pinning ContentRootPath to the exe directory explicitly
3. Getting Claude to fill 25+ structured fields consistently required careful JSON schema prompting
4. eBay item specifics vary by category — clothing needs Size/Color/Material, electronics need Connectivity/RAM/Storage, mining hardware needs Algorithm/Hashrate

**What's working:** The bulk import — paste a supplier's collection page and all products get processed simultaneously into separate tabs. Sellers love this.

**Download / source:** https://github.com/essquireo0o/ING-eBay-Autolister

Happy to answer any questions about the build or the eBay API.

---

## Product Hunt Launch Description

**Tagline:** Paste any product URL → AI fills your entire eBay listing in 10 seconds

**Description:**
ING Listing Engine is a Windows app that automates eBay listing creation using Claude AI.

Paste any product URL — supplier page, manufacturer site, or existing listing. The AI reads the page and fills your complete eBay listing: keyword-optimized title, professional HTML description, category, 20+ item specifics, product photos, pricing estimate, and shipping dimensions. One click publishes directly to eBay.

**Bulk import:** Paste a collection page URL and every product on it gets processed simultaneously — each into its own draft tab, ready to review and publish.

Works for any product category. No install — just double-click the exe.

30-day free trial → $49.99/mo Pro.

---

## YouTube Video Description (for demo video)

**Title:** AI Fills Your Entire eBay Listing From a URL — ING Listing Engine Demo

**Description:**
In this video I show how ING Listing Engine uses Claude AI to automatically fill a complete eBay listing from any product URL — title, description, category, item specifics, photos, pricing, and shipping. Then one click publishes it directly to eBay.

I also demo the bulk import feature — paste a collection page URL and every product gets processed at once.

🔗 Free 30-day trial (no credit card): https://github.com/essquireo0o/ING-eBay-Autolister/releases/latest
📖 Setup guide: https://github.com/essquireo0o/ING-eBay-Autolister

Timestamps:
0:00 - Intro
0:30 - Single product URL import
2:00 - AI-filled listing walkthrough
3:30 - Item specifics
4:30 - Publish to eBay
5:30 - Bulk collection import
7:00 - Draft management
8:00 - Pricing & trial

#ebay #reselling #aitools #ecommerce #flipping

---

*All posts created for https://github.com/essquireo0o/ING-eBay-Autolister*
