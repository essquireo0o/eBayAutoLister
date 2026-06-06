# ING Listing Engine™
### by ING Mining LLC

**AI-powered eBay listing automation.** Paste a product URL — Claude AI reads the page, fills your title, description, category, item specifics, photos, and shipping automatically. Bulk-import entire collections in one click. Publish directly to eBay.

---

## Download & Install

👉 **[Download Latest Release → AutoListerB1.exe](../../releases/latest)**

1. Download `AutoListerB1.exe`
2. Double-click — no install, no .NET required
3. Your browser opens at `http://localhost:9330`
4. Enter your API keys in Settings and you're ready

> Windows only. Everything is bundled inside the single exe.

---

## Features

### Dashboard — Inventory at a Glance

![Dashboard Overview](docs/screenshots/01-dashboard-overview.png)

The main dashboard shows your entire eBay seller account at a glance: active listing count, total inventory quantity, catalog value, and items needing review. Existing listings load as photo cards directly from eBay. The Recent Activity feed shows connection status and last actions in real time.

---

### Single Product Import — Paste URL, AI Does the Rest

![URL Product Import](docs/screenshots/04-url-product-import.png)

Paste any product URL (supplier page, manufacturer site, existing eBay listing) into the top bar and click **Analyze**. The AI reads the page and auto-fills every field — title, subtitle, category, condition, description, photos, item specifics, pricing, and shipping dimensions.

---

### AI-Filled Listing — Ready in Seconds

After analysis, every section is pre-populated with keyword-optimized content. The description is written in eBay-safe HTML with a formatted KEY SPECIFICATIONS table. You can review, edit any field, re-analyze, or improve the SEO and description with one click before publishing.

---

### Item Specifics — Fully Auto-Filled

![Item Specifics Autofill](docs/screenshots/07-item-specifics-autofill.png)

![Advanced Item Specifics](docs/screenshots/08-advanced-item-specifics.png)

eBay item specifics (Brand, Model, Compatible Series, Power Interface, Display, Firmware Version, Algorithm, Cooling, Country of Manufacture, and 20+ more fields) are detected and filled automatically from the product page. Each field can be edited inline before publishing.

---

### Bulk Collection Import — Entire Catalogs in One Click

![Bulk Collection Import Progress](docs/screenshots/05-bulk-collection-import-progress.png)

![Multi-Tab Bulk Import](docs/screenshots/10-multi-tab-bulk-import.png)

Paste a **collection or category page URL** (e.g. a supplier's product listing page) into the Import bar and click **Import All**. The AI processes every product on the page and opens each one as its own tab — title, description, photos, and specifics filled for all of them at once. Each tab is independent: review, edit, save, or publish individually.

---

### Inventory View — Card & Table Modes

![Dashboard Inventory](docs/screenshots/03-dashboard-inventory.png)

Switch between card view and table view to browse your existing eBay listings. Each card shows the product photo, current price, SKU, condition, and spread. Use **Import Listings** to pull in your full inventory from eBay, or **Create New AI Listing** to start fresh.

---

### Shipping, Policies & Publish

![Publish Ready Listing](docs/screenshots/09-publish-ready-listing.png)

Before publishing, the app fills in package dimensions (weight, length, width, height), country of origin, and automatically applies your saved seller policies for shipping/fulfillment, payment, and returns. When everything looks right, click **Publish to eBay** to push the live listing directly via the eBay Sell API.

---

### Freeware — No Subscription, No Expiry

ING Listing Engine™ is **freeware**. All features are unlocked with no time limit and no subscription required.

Activate with key **`ING-BETA-2025`** in the License page to enable everything.

---

## First-Time Setup

### Step 1 — Get an Anthropic API Key

The AI that reads product pages and writes your listings runs on Claude.

1. Go to **[console.anthropic.com](https://console.anthropic.com)**
2. Sign up or log in → click **API Keys** → **Create Key**
3. Copy the key (starts with `sk-ant-`)
4. Paste it into **Settings → Anthropic API Key** in the app

> Typical cost: under $0.01 per listing.

---

### Step 2 — Set Up an eBay Developer Account

#### 2a. Create a developer account

1. Go to **[developer.ebay.com](https://developer.ebay.com)**
2. Sign in with your regular eBay seller account
3. Accept the developer agreement if prompted

#### 2b. Create a Production Application

1. Go to **Application Keys** → **Create Application**
2. Name it anything (e.g. `AutoLister`) → select **Production** → **Create**
3. Copy all three keys into the app's Settings:

| eBay Key | Where to paste |
|----------|---------------|
| App ID (Client ID) | Settings → eBay App ID |
| Dev ID | Settings → eBay Dev ID |
| Cert ID (Client Secret) | Settings → eBay Cert ID |

#### 2c. Request Full API Access from eBay

> **Important:** By default, new eBay developer accounts are limited to sandbox access only. To publish real listings you must **open a support ticket with eBay** to request production API access for your application.
>
> 1. Log in to **[developer.ebay.com](https://developer.ebay.com)**
> 2. Click **Support** → **Open a Case**
> 3. Request: *"Production API access for my application — I am a registered seller and want to use the Sell API to create listings programmatically."*
> 4. eBay typically approves within 1–3 business days

#### 2d. Set Up Your OAuth Redirect (RuName)

1. In the developer portal, go to **User Tokens**
2. Click **Get a Token from eBay via Your Application**
3. Under **Your auth accepted URL**, add your OAuth callback URL
4. Copy the **RuName** shown → paste into **Settings → eBay RuName**

#### 2e. Connect Your eBay Seller Account

1. Fill in App ID, Dev ID, Cert ID, and RuName in Settings
2. Click **Connect eBay Account** — a browser window opens
3. Log in with your eBay seller account and click **Agree**
4. The app stores your token automatically

> Token lasts 18 months and auto-refreshes. Do this once.

---

### Step 3 — Set Listing Defaults (Optional)

In **Settings → Listing Defaults**, pre-fill your postal code, default handling time, package weight/dimensions, and seller policies so they appear automatically on every new listing.

---

## How to Use

### Single Product
1. Click **New Listing** → paste any product URL → click **Analyze**
2. AI fills everything in ~10 seconds
3. Review and edit if needed → click **Publish to eBay**

### Bulk Import
1. Click **New Listing** → paste a **collection or category page URL** into the **Import All** bar
2. Click **Import All** — each product opens as a tab
3. Review and publish each tab

### Managing Drafts
- **Save Draft** — saves locally before publishing
- **Open All Drafts** — reloads previously saved drafts
- **Clear All** — wipes all drafts and tabs to start fresh

---

## Troubleshooting

**App doesn't open in browser**
- Try opening `http://localhost:9330` manually
- Make sure nothing else is running on port 9330

**eBay token expired**
- Settings → click **Connect eBay Account** again

**AI analysis fails**
- Check your Anthropic API key is correct in Settings
- Verify you have credits on your Anthropic account

**"Address already in use" error**
- An old copy is running in the background
- Open Task Manager → find `AutoListerB1.exe` → End Task

**Publish fails with "Developer account not authorized"**
- You need to open a ticket with eBay to enable production API access (see Step 2c above)

---

## Built With

- [Claude AI](https://anthropic.com) — product analysis and listing generation
- [eBay Sell API](https://developer.ebay.com) — listing creation and publishing
- ASP.NET Core 10 — backend server
- Vanilla JS — frontend UI

---

*ING Listing Engine™ is a product of ING Mining LLC. All rights reserved.*
