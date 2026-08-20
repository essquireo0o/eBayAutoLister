#!/usr/bin/env python3
"""
Perpetual supervisor for the ING AutoLister autonomous loop. NEVER stops (until you kill it):
  - keeps the queue topped up to TARGET with fresh, guardrailed, high-value tasks, and
  - relaunches the claude-queue worker if it ever dies.

Every queued task still build+test+commit-or-reverts on the isolated branch, free beta, no paywalls.

Run:   python queue_forever.py         (launch via the queue in the background)
Stop:  kill this python process (Stop-Process), or delete this and stop the worker.
"""
import subprocess, sys, os, json, time

CQ   = [sys.executable, r"C:/Users/nsquires/source/repos/claude-queue/claude-queue.py"]
QF   = r"C:/Users/nsquires/.claude-queue/tasks.json"
LOG  = r"C:/Users/nsquires/source/repos/claude-queue-worker.log"
PROJ = r"C:/Users/nsquires/source/repos/ING eBay AutoLister"
TARGET   = 0       # 2026-08-20: NO invented tasks. The owner queues work explicitly; the bot only runs that.
                   # (It was 30 - a self-refilling backlog that ran Claude around the clock and competed
                   # with the owner's own session for usage. 'Make sure it doesn't exhaust the claude session.')
QUIET_START, QUIET_END = 23, 6   # local hours the worker may run: 23:00 -> 06:00, when nobody is using Claude
INTERVAL = 150     # seconds between supervisor cycles

GUARD = r"""GUARDRAILS - FULL AUTONOMY; NEVER ask the user, NEVER wait, NEVER stop to confirm - decide and proceed. Free beta, NO paywalls/subscriptions/gating.
CURRENT TOP PRIORITY (owner, 2026-08-15): AI Listing for Amazon via SP-API (SANDBOX ONLY) - mirror the eBay flow exactly. If Amazon work is unfinished, prefer it over anything else.
VISION: optimize for (1) "how does Nick make money?" grow the seller's profit/sales; (2) "what is Nick missing?" vs Vendoo/List Perfectly/ZIK/Crosslist; (3) "the most valuable eBay-AND-Amazon-seller app in the world."
- FIRST run: git log --oneline -30 and read WORK_COMPLETED.md; do NOT duplicate - pick the highest-leverage NEW or unfinished work.
- Repo: C:\Users\nsquires\source\repos\ING eBay AutoLister on branch auto/queue-features-20260726. ONE cohesive END-TO-END change, high quality.
- BEFORE finishing run BOTH: dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug  AND  dotnet test "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj".
- Fail -> revert (git checkout -- . ; git clean -fd) and STOP. Green -> git add -A && git commit; bump wwwroot/index.html ?v= if app.js changed; document in WORK_COMPLETED.md.
- NEVER: git push, publish live eBay, read/print secrets, delete user data, change keys, rm -rf, git reset --hard, touch files outside the repo."""

# WhatsNot vision, carried in every WhatsNot task because the worker is stateless per task.
# The tab already exists (nav data-page="whatsnot", section id="whatsnot-section", showWhatsNotSection
# in app.js, styles in style.css): an embedded-browser panel with an address bar + iframe + "Open in
# browser" fallback pointed at a live-selling feed (Whatnot etc.).
WN = ("WHATSNOT FEATURE (the 'WhatsNot' tab: nav data-page=\"whatsnot\", section #whatsnot-section, "
 "showWhatsNotSection/bindWhatsNot in app.js, .wn-* styles in style.css). VISION: real-time LIVE-AUCTION "
 "ARBITRAGE. While an item is on screen in a live-selling feed (Whatnot), BEFORE and DURING the bid, show "
 "the seller as many decision statistics as possible so they know whether to bid: recent SOLD comps on "
 "eBay (start with eBay; other markets later), the SELL-THROUGH RATE, price low/median/high spread, sell "
 "velocity/how fast it moves, and a suggested MAX BID for a target margin (current bid vs eBay resale = the "
 "arbitrage). This is the Opportunity Finder treatment applied live - REUSE Opportunity Finder / the app's "
 "existing eBay market + sell-through services, don't reinvent them. Answer within seconds. HARD "
 "CONSTRAINTS: (1) do NOT stop, disable, or remove sold-comps anywhere - it stays fully working, WhatsNot "
 "is additive; (2) an <iframe> can't read a cross-origin feed (Whatnot sends X-Frame-Options/CSP), so make "
 "honest incremental progress (type/paste the item on screen -> instant arbitrage card; groundwork for real "
 "feed capture) rather than faking a live read. One cohesive, tested, working increment per task. ")

POOL = [
 # AMAZON is the current top priority (owner, 2026-08-15). Kept FIRST and generative so the endless
 # loop keeps advancing AI Listing for Amazon after the explicit amzp-* phases drain, rather than
 # drifting back to generic work. SANDBOX ONLY; the amzp-* phases already queued do the initial build.
 ("AMAZON — THE TOP PRIORITY. Advance AI Listing for Amazon over eBay parity (SP-API, SANDBOX ONLY). Read git log + WORK_COMPLETED.md for what the amzp-* Amazon phases already did, then build the ONE highest-value next cohesive tested increment toward listing on Amazon exactly like eBay: LWA/SP-API auth, product-type schemas, AI filling required attributes, sandbox offer-on-ASIN then create-product, and the Amazon UI beside eBay. Mirror EbayService/EbayAuthFlow/Models.ListingData/the AI Listing screen. NEVER publish to a live Amazon account; NEVER invent GTIN/brand/identifier values; reuse CrossListingExporter's Amazon rules. If Amazon is genuinely complete and tested end to end in sandbox, say so in WORK_COMPLETED.md and only then pick other high-value work.", "amz-endless"),
 (WN + "Build the core 'arbitrage card' for WhatsNot: seller enters the item currently on screen (title, or eBay/Whatnot URL) and instantly sees recent eBay SOLD comps, sell-through rate, low/median/high, velocity, and a BID/PASS read with a suggested max bid for a target margin. Reuse Opportunity Finder + existing eBay market services. Shown beside the feed, answer in seconds.", "wn-arbitrage-card"),
 (WN + "Wire the WhatsNot arbitrage card to the app's real eBay sold-comps + sell-through data path so its numbers are real and fast; add a confidence/freshness note. Additive to sold comps, never replacing it. Add tests.", "wn-live-data"),
 (WN + "Improve the WhatsNot embedded-browser panel: navigation (back/forward/reload), remember the last feed URL, clearer handling when a site refuses to embed, general reliability/UX.", "wn-embed"),
 (WN + "Sharpen the arbitrage read for LIVE BIDDING: show suggested max bid for chosen margin, break-even price, and a clear go/no-go as the current bid climbs; make it glanceable in the seconds a live auction gives you.", "wn-bid-signal"),
 (WN + "Read the item on screen automatically. PRIMARY path (far easier than video CV): Whatnot's item on a live show is STRUCTURED - their internal GraphQL exposes the show's current listing (title, current bid, image, category). Prefer pulling that title/current-bid and feeding it straight to the eBay arbitrage card. SECONDARY: run the listing image through the app's EXISTING Claude vision product-photo pipeline to confirm identity/condition. FALLBACK: screen-region capture of the video -> that same vision pipeline. NOTE the ToS/legal tradeoff of using Whatnot's private GraphQL in a code comment; keep it behind the panel; small tested step.", "wn-capture"),
 (WN + "Polish, harden, and test the WhatsNot arbitrage flow end to end: accessibility, keyboard, responsive, error/empty states, and unit tests for any new WhatsNot services. No logic breakage elsewhere.", "wn-polish"),
 # Generative: this is what makes the WhatsNot cycle truly endless. Each run INVENTS the next
 # capability rather than executing a pre-written one - read what WhatsNot already does, decide
 # the single highest-value thing still missing for a live-auction arbitrage tool, then build it.
 (WN + "INVENT THE NEXT WHATSNOT FEATURE. Read git log + WORK_COMPLETED.md to see what WhatsNot already does, then decide the ONE highest-leverage capability it's still missing to be the best live-auction arbitrage tool for a reseller (e.g. bid-timing alerts, per-category profit heatmap, watchlist of shows worth joining, auto-flag when current bid drops below your max, condition-grading from the image, shipping/fees baked into the margin, multi-market resale not just eBay, a running session P&L of what you bought vs its resale). Pick the best NEW one not yet built, then build one cohesive tested increment of it and record it in WORK_COMPLETED.md. Never duplicate existing work.", "wn-invent"),
 ("Add or improve a high-value seller feature not yet present; document the money impact in WORK_COMPLETED.md.", "auto-feature"),
 ("Polish and harden the local-arbitrage (Craigslist/Facebook) experience: reliability, clearer per-source status, better profit ranking.", "auto-arbitrage"),
 ("Improve the listing workflow (item -> live listing): fewer clicks, smarter defaults, better autofill.", "auto-workflow"),
 ("Premium design/UX polish on a screen so the app feels worth paying for; no logic breakage.", "auto-design"),
 ("Add or expand unit tests for an under-tested service.", "auto-tests"),
 ("Improve pricing/confidence transparency so the numbers are trustworthy.", "auto-pricing"),
 ("Harden error handling / recovery on a critical path so users never lose work.", "auto-reliability"),
 ("Improve SEO / title / item-specifics completeness to get more views and sales.", "auto-seo"),
 ("Add an analytics insight or dashboard that helps the seller decide what to list/source.", "auto-analytics"),
 ("Improve the Opportunity Finder / sourcing recommendations quality and presentation.", "auto-opportunity"),
 ("Accessibility + keyboard + responsive polish on one section.", "auto-a11y"),
 ("Improve the photo pipeline or representative-library UX.", "auto-photos"),
 ("Add a quality-of-life feature that saves the seller time at scale (bulk actions, shortcuts, templates).", "auto-qol"),
 ("Improve cross-listing / multi-marketplace value (more destinations, better exports).", "auto-crosslist"),
 ("Find a rough edge or TODO in the code and polish it properly with tests.", "auto-polish"),
 ("Improve onboarding so a new beta tester immediately understands setup and value.", "auto-onboarding"),
]

def queued_count():
    for _ in range(6):
        try:
            d = json.load(open(QF))
            it = d if isinstance(d, list) else (list(d.get("tasks", d).values()) if isinstance(d.get("tasks", d), dict) else d.get("tasks", d))
            return sum(1 for t in it if t.get("status") == "queued")
        except Exception:
            time.sleep(0.4)
    return TARGET  # on read error, assume OK so we don't over-add

def worker_running():
    ps = ("(Get-CimInstance Win32_Process -Filter \"Name='python3.12.exe' OR Name='python.exe'\" | "
          "Where-Object { $_.CommandLine -match 'claude-queue.py worker' } | Measure-Object).Count")
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=30)
        return int((r.stdout or "0").strip() or "0") > 0
    except Exception:
        return True  # if we can't tell, don't spawn a duplicate

def launch_worker():
    f = open(LOG, "a", encoding="utf-8", errors="replace")
    subprocess.Popen(CQ + ["worker"], cwd=PROJ, stdout=f, stderr=f,
                     creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    print("supervisor: launched a worker", flush=True)

def add_task(prompt, session):
    for _ in range(10):
        r = subprocess.run(CQ + ["add", GUARD + "\n\nTASK: " + prompt, "--session", session, "--priority", "0"],
                           capture_output=True, text=True)
        if r.returncode == 0:
            return True
        time.sleep(0.5)
    return False

def in_quiet_hours():
    h = time.localtime().tm_hour
    return h >= QUIET_START or h < QUIET_END


def worker_idle():
    """No task is mid-flight. Only then may a daytime worker be stopped - a task killed halfway is a
    half-written tree the next failure-clean wipes, so a running task always gets to finish."""
    try:
        import json
        with open(QF, encoding="utf-8") as f:
            return not any(t.get("status") == "running" for t in json.load(f))
    except Exception:
        return False


def stop_idle_worker():
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'claude-queue.py worker' } "
                    "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
                   capture_output=True, text=True, timeout=60)
    print("supervisor: daytime - stopped the idle worker; explicit tasks resume at %02d:00" % QUIET_START, flush=True)


def main():
    i = 0
    print("supervisor: loop started - explicit tasks only, worker alive %02d:00-%02d:00" % (QUIET_START, QUIET_END), flush=True)
    while True:
        try:
            if in_quiet_hours():
                if not worker_running():
                    launch_worker()
                    time.sleep(6)
            elif worker_running() and worker_idle():
                stop_idle_worker()
            q = queued_count()
            if q < TARGET:
                need = TARGET - q
                for _ in range(need):
                    prompt, pre = POOL[i % len(POOL)]
                    add_task(prompt, f"{pre}-{int(time.time())}-{i}")
                    i += 1
                print(f"supervisor: topped up +{need} (was {q})", flush=True)
        except Exception as e:
            print("supervisor: cycle error:", e, flush=True)
        time.sleep(INTERVAL)

if __name__ == "__main__":
    main()
