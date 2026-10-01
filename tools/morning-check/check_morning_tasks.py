"""
Acceptance check for the owner's morning to-do list.

Several sessions build that list (tools/morning-brief/brief.py, ING Assistant\\Morning\\ing_morning.py,
the Morning Desk queue). They all meet in ONE place: the `morning_tasks` table of the shared ING
memory database. This script reads that table and says, with examples, where the list is wrong -
so "the list is right" is something a session can run, not something it has to believe.

READ-ONLY: the database is opened mode=ro and nothing is ever written to it.

    python check_morning_tasks.py            # check the table
    python check_morning_tasks.py --live     # also compare against eBay right now (read-only calls)

Exit code 0 = no FAIL, 1 = at least one FAIL, 2 = the table could not be read.
FAIL = a wrong line the owner would act on (or a missing one). WARN = a judgement call.
"""
import json, os, re, sqlite3, sys, time
from collections import Counter
from datetime import datetime

DB = os.path.expandvars(r"%LOCALAPPDATA%\Packages\PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0"
                        r"\LocalCache\Local\ING Personal Form Assistant\user-memory.sqlite3")
BRIEF_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "morning-brief")
OWNER_ADDRESSES = ("ns@ingmining.com",)
OWNER_EBAY_USER = "ingmining"
DO_FIRST_MAX = 20          # more "do it now" lines than this is an inbox, not a list
STALE_HOURS = 26           # a daily collector must have touched every open line within a day

# A sender nobody answers. Deliberately narrow: only what is unmistakably an advertisement.
AD_SENDER = re.compile(r"specials@|rewards@|enews@|deals@|offers@|promo|marketing|newsletter|substack\.com|"
                       r"@(e|e1|m|em|go|g|mail|email|info|guest|notification|notifications|reply)\.", re.I)
AD_SUBJECT = re.compile(r"\d+% off|\$\d+ off|flash sale|sale ends|last chance|cash bonus|restocked|weekly ad", re.I)

results = []   # (level, rule, message, examples)

def report(level, rule, message, examples=()):
    results.append((level, rule, message, list(examples)[:5]))

def sender_of(row):
    try:
        meta = json.loads(row["metadata"] or "{}")
    except ValueError:
        meta = {}
    return str(meta.get("from") or meta.get("sender") or "")

def check(con, live):
    con.row_factory = sqlite3.Row
    rows = con.execute("SELECT * FROM morning_tasks WHERE status='open'").fetchall()
    now, today = time.time(), datetime.now().strftime("%Y-%m-%d")
    if not rows:
        report("FAIL", "empty", "no open lines at all - the collector has not run or everything was swept")
        return

    # 1. The list mails itself to the owner, then lists its own mail as something to answer.
    own = [r["title"] for r in rows if any(a in sender_of(r).lower() for a in OWNER_ADDRESSES)]
    if own:
        report("FAIL", "own-mail", f"{len(own)} line(s) are mail the owner (or this system) sent to himself", own)

    # 2. A thread the owner started is not a question waiting on him.
    mine = [r["title"] for r in rows if re.search(rf"\b{OWNER_EBAY_USER} sent a message", r["title"], re.I)]
    if mine:
        report("FAIL", "own-thread", f"{len(mine)} line(s) are the owner's OWN eBay message, listed as a buyer question", mine)

    # 3. Received tomorrow: dates are UTC days, so an evening message lands on the wrong day.
    future = [f"{r['due']}  {r['title']}" for r in rows if (r["due"] or "")[:10] > today and not json.loads(r["metadata"] or "{}").get("setup")]
    if future:
        report("FAIL", "utc-date", f"{len(future)} line(s) are dated after today ({today}) - the date is a UTC day, not the owner's", future)

    # 4. The same line twice (four receipts are four receipts, but the owner reads one line).
    dup = [f"x{n}  {t}" for t, n in Counter(r["title"] for r in rows).items() if n > 1]
    if dup:
        report("WARN", "duplicate", f"{len(dup)} title(s) appear more than once - fold them into one line with a count", dup)

    # 5. Lines the collector stopped looking at: they can no longer clear themselves.
    stale = [f"{int((now - r['last_seen']) / 3600)}h  {r['title']}" for r in rows if now - r["last_seen"] > STALE_HOURS * 3600]
    if stale:
        report("FAIL", "stale", f"{len(stale)} open line(s) not refreshed in {STALE_HOURS}h - is the collector still scheduled?", stale)

    # 6. Amazon mails "Your e-mail to <name>" when the owner answers; the question is then done.
    answered = []
    for r in rows:
        m = re.search(r"amazon (?:buyer|customer) (\w+)", r["title"], re.I)
        if not m:
            continue
        hit = con.execute("SELECT label FROM records WHERE kind='email' AND content LIKE ? ORDER BY created DESC LIMIT 1",
                          (f"%Subject: Your e-mail to {m.group(1)}%",)).fetchone()
        if hit and hit["label"][:10] >= (r["due"] or "")[:10]:
            answered.append(f"{r['title']}  <- answered ({hit['label'][:10]})")
    if answered:
        report("FAIL", "answered", f"{len(answered)} Amazon question(s) still open although the reply already went out", answered)

    # 7. An advertisement is not something to reply to.
    ads = [r["title"] for r in rows if r["priority"] <= 3 and r["source"] == "email"
           and (AD_SENDER.search(sender_of(r)) or AD_SUBJECT.search(r["title"]))]
    if ads:
        report("WARN", "ad-as-task", f"{len(ads)} do-it-now line(s) look like advertising", ads)

    # 8. One link for many lines: the owner cannot click through to the item.
    links = Counter(r["link"] for r in rows if r["link"] and r["priority"] <= 3)
    shared = [f"x{n}  {l[:80]}" for l, n in links.items() if n > 1]
    if shared:
        report("WARN", "shared-link", f"{len(shared)} link(s) are shared by several lines, so they open a folder, not the item", shared)

    # 9. Size. The owner asked what to do FIRST.
    first = [r for r in rows if r["priority"] <= 3]
    if len(first) > DO_FIRST_MAX:
        by = Counter(r["source"] for r in first)
        report("WARN", "too-long", f"{len(first)} do-it-now lines (limit {DO_FIRST_MAX}): " + ", ".join(f"{s} {n}" for s, n in by.most_common()))

    # 10. Sources still waiting on the owner. Not a defect, but the list is incomplete without them.
    setup = [r["title"] for r in rows if json.loads(r["metadata"] or "{}").get("setup")]
    if setup:
        report("INFO", "not-connected", f"{len(setup)} source(s) are waiting on a one-time sign-in from the owner", setup)

    if live:
        check_live(rows)

def check_live(rows):
    """The expensive mistakes are the MISSING lines: an order not shipped, a return or cancel not answered."""
    sys.path.insert(0, BRIEF_DIR)
    try:
        import ebay_orders as eb
        orders, rets, cancels = eb.orders(30), eb.returns(), eb.cancellations()
    except Exception as e:
        report("FAIL", "live", f"could not ask eBay: {str(e)[:200]}")
        return
    text = "\n".join(f"{r['title']} {r['detail']} {r['external_id']} {r['metadata']}" for r in rows)
    def missing(kind, ids_and_labels):
        gone = [label for key, label in ids_and_labels if key and str(key) not in text]
        if gone:
            report("FAIL", f"missing-{kind}", f"{len(gone)} {kind}(s) open at eBay but NOT on the list", gone)
    missing("order to ship", [(o["legacyOrderId"], f"{o['legacyOrderId']} {o['buyer']} ship by {o['shipBy'][:10]}")
                              for o in orders if o["status"] in ("NOT_STARTED", "IN_PROGRESS") and o["paid"] == "PAID"])
    missing("return", [(r.get("returnId"), f"{r.get('returnId')} {r.get('buyer')} {r.get('reason')} respond by {(r.get('sellerRespondBy') or '?')[:10]}")
                       for r in rets if not r.get("error") and r.get("state") != "CLOSED"])
    missing("cancel request", [(c.get("cancelId"), f"{c.get('cancelId')} order {c.get('orderId')} {c.get('reason')}")
                               for c in cancels if not c.get("error") and c.get("status") in ("CANCEL_REQUESTED", "CANCEL_PENDING")])
    errs = [x["error"] for x in rets + cancels if x.get("error")]
    if errs:
        report("FAIL", "live", "eBay refused a read: " + errs[0][:200])

def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    try:
        con = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True, timeout=30)
        check(con, "--live" in sys.argv)
    except sqlite3.Error as e:
        print(f"cannot read morning_tasks: {e}")
        return 2
    order = {"FAIL": 0, "WARN": 1, "INFO": 2}
    for level, rule, message, examples in sorted(results, key=lambda r: order[r[0]]):
        print(f"{level}  {rule}: {message}")
        for ex in examples:
            print(f"        {ex[:150]}")
    fails = sum(1 for r in results if r[0] == "FAIL")
    warns = sum(1 for r in results if r[0] == "WARN")
    print(f"\n{'NOT RIGHT YET' if fails else 'OK'}: {fails} fail, {warns} warn")
    return 1 if fails else 0

if __name__ == "__main__":
    sys.exit(main())
