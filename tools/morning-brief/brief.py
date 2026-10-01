"""
ING Morning Brief - one ordered to-do list for the owner, built every morning from:
  * Microsoft 365 inbox (ns@ingmining.com): eBay / Amazon / shipping / returns / payment / appointment
    mail, buyer questions, incoming package notices  (via %LOCALAPPDATA%\\ING\\OutlookMCP\\outlook_mcp.py)
  * eBay Sell APIs: orders waiting to ship, ship-by dates, shipments in transit, open returns,
    cancellation requests awaiting an answer                               (ebay_orders.py)
  * Telegram: the ING alert bot's chat (replies the owner sent to the bot)  (telegram.dpapi)
Amazon orders come from Amazon's e-mails only (SP-API is not live). Personal Telegram chats and
WhatsApp need a one-time sign-in from the owner before they can be read - the brief says so.

Output: Markdown saved under %LOCALAPPDATA%\\ING AutoLister\\morning-brief\\, e-mailed to the owner
and pushed to the owner's Telegram.   python brief.py [--no-send] [--days N]
"""
import ctypes, ctypes.wintypes as wt, json, os, re, sys, time, urllib.parse, urllib.request
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.expandvars(r"%LOCALAPPDATA%\ING\OutlookMCP"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import outlook_mcp as ol      # noqa: E402
import ebay_orders as eb      # noqa: E402

OUT_DIR = os.path.expandvars(r"%LOCALAPPDATA%\ING AutoLister\morning-brief")
TG_CONFIG = os.path.expandvars(r"%LOCALAPPDATA%\ING AutoLister\antminer-watch\telegram.dpapi")
OWNER = "ns@ingmining.com"
LOCAL_TZ = timezone(timedelta(hours=-4))   # owner is on US Eastern; good enough for day boundaries

# ---------- helpers ----------
class _BLOB(ctypes.Structure):
    _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

def dpapi_unprotect(data: bytes) -> bytes:
    inp = _BLOB(len(data), ctypes.cast(ctypes.create_string_buffer(data, len(data)), ctypes.POINTER(ctypes.c_char)))
    out = _BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(inp), None, None, None, None, 0, ctypes.byref(out)):
        raise OSError("DPAPI unprotect failed")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)

def when(iso):
    try:
        return datetime.fromisoformat(iso.replace("Z", "+00:00")).astimezone(LOCAL_TZ)
    except Exception:
        return None

def ago(iso):
    d = when(iso)
    if not d:
        return ""
    h = (datetime.now(LOCAL_TZ) - d).total_seconds() / 3600
    return f"{int(h)}h ago" if h < 48 else f"{int(h // 24)}d ago"

def day(iso):
    d = when(iso)
    return d.strftime("%a %b %d") if d else "?"

# ---------- e-mail classification ----------
NOISE_SENDERS = re.compile(r"linkedin|newsletter|noreply@redditmail|producthunt|substack|dccc|berniesanders|ccsend|"
                           r"@e\.|@em\.|@m\.|@mail1\.|@go\.|@ez\.|@g\.|@newsletter|@info\.|specials@|enews@|deals@|store-news@|"
                           r"marketing|promo|rewards@|crowdstreet|sofi|rexmd|trtnat|imktg|eventbrite|wikimedia|gembah|"
                           r"canadianinsulin|bitcoin\.com|1800flowers|thingsremembered|valimont|teamdefend|blueontheballot|"
                           r"engineering10|peytonendowment|storeroomstudio|labusagrowth|northstepclear|strategicleadz|"
                           r"interactivebrokers|goodchop|valvoline|altairtech|reply\.ebay\.com|notification\.capitalone|"
                           r"nytimes|tractorsupply|menswearhouse|acehardware|fredmeyer|dcsg\.com|ecstuning|airgundepot|"
                           r"sell\.amazon\.com|imazing|eztexting|wfbusiness", re.I)
CUSTOMER_WORDS = re.compile(r"toughbook|bios|inquiry|quote|question|order|invoice|miner|antminer|phenibut|research supply", re.I)
INVISIBLE = re.compile(r"[͏​-‏⁠﻿­]+|\s*͏\s*")

def clean(s):
    return re.sub(r"\s+", " ", INVISIBLE.sub(" ", s or "")).strip()

RULES = [  # (bucket, subject regex, sender regex)
    ("ebay_sold",      r"you made the sale|sold, ship now|^sold:|sale confirmed", r"ebay\.com"),
    ("ebay_cancel",    r"wants to cancel|cancel(lation)? request", r"ebay\.com"),
    ("ebay_return",    r"^return \d+|return approved|return request|refund|item not as described|case opened|request for refund", r"ebay\.com"),
    ("ebay_question",  r"sent a message about|new message from|has a question", r"ebay\.com"),
    ("ebay_offer",     r"new offer|best offer|counter ?offer", r"ebay\.com"),
    ("ebay_shipping",  r"shipping label|print your label|ship by", r"ebay\.com"),
    ("amazon_question",r"inquiry from amazon customer|product details inquiry|update from seller on order|message from amazon customer|buyer message", r"amazon\.com"),
    ("amazon_sold",    r"sold, ship now|new order|action required.*order|order .* placed", r"amazon\.com"),
    ("amazon_case",    r"resolution for caseid|case id|a-to-z|claim", r"amazon\.com"),
    ("delivery",       r"out for delivery|delivered|arriving|has shipped|shipment|package|tracking|delivery (update|confirmation|exception)|on its way|label created|^ordered:|^ordered \d|your order", r"ups\.com|usps\.com|fedex\.com|amazon\.com|pirateship|ebay\.com|dhl"),
    ("payment",        r"payment|payout|statement|deposit|transfer|invoice|receipt|funds|wire|ach\b", r"bank|capitalone|schwab|paypal|stripe|pirateship|amazon|ebay|beacon|wellsfargo|zen"),
    ("appointment",    r"appointment|reminder|confirmation.*(visit|appointment)|your visit", r"."),
    ("security",       r"new sign-in|sign-in|verification code|security alert|password", r"."),
]

def classify(m):
    s, f = (m.get("subject") or ""), (m.get("from") or "")
    for bucket, sre, fre in RULES:
        if re.search(sre, s, re.I) and re.search(fre, f, re.I):
            return bucket
    if NOISE_SENDERS.search(f) or re.search(r"unsubscribe|% off|sale ends|flash sale|last chance", s + " " + (m.get("preview") or ""), re.I):
        return "noise"
    return "other"

def mail_section(days):
    cfg = ol.load_config()
    msgs = ol.outlook_unread(cfg, top=100, days=days)["messages"]
    # Amazon copies the seller's own replies back as "Your e-mail to <name>": anyone on that list
    # with a reply newer than their question has been answered already.
    answered = {}
    for m in msgs:
        mm = re.match(r"Your e-mail to (.+)$", m.get("subject") or "", re.I)
        if mm and "amazon" in (m.get("from") or "").lower():
            answered[mm.group(1).strip().lower()] = max(answered.get(mm.group(1).strip().lower(), ""), m["received"])
    buckets, seen = {}, set()
    for m in msgs:
        m["subject"] = clean(m.get("subject")); m["preview"] = clean(m.get("preview"))
        b = classify(m)
        m["ago"] = ago(m["received"])
        if b == "amazon_question":
            who = re.search(r"customer (\w+)|^Update from Seller", m["subject"], re.I)
            name = (m.get("from") or "").split("<")[0].strip().lower()
            if name in answered and answered[name] > m["received"]:
                b = "answered"
        if b in ("ebay_question", "amazon_question"):
            key = (b, re.sub(r"^re: ", "", m["subject"].lower())[:60])   # same buyer, same item -> one line
            if key in seen:
                m["dup"] = True
            seen.add(key)
        buckets.setdefault(b, []).append(m)
    return msgs, buckets

# ---------- telegram ----------
def tg_config():
    try:
        return json.loads(dpapi_unprotect(open(TG_CONFIG, "rb").read()))
    except Exception:
        return None

def tg_call(cfg, method, data):
    req = urllib.request.Request(f"https://api.telegram.org/bot{cfg['token']}/{method}", data=json.dumps(data).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=35) as r:
        return json.load(r)

def tg_incoming(cfg, since_hours=24):
    """What the owner typed to the alert bot recently (the only Telegram text a bot can see)."""
    try:
        j = tg_call(cfg, "getUpdates", {"timeout": 0, "allowed_updates": ["message"]})
    except Exception:
        return []
    cutoff = time.time() - since_hours * 3600
    out = []
    for u in j.get("result", []):
        msg = u.get("message") or {}
        if msg.get("chat", {}).get("id") == cfg["chat_id"] and msg.get("date", 0) >= cutoff and msg.get("text"):
            out.append({"when": datetime.fromtimestamp(msg["date"], LOCAL_TZ).strftime("%a %H:%M"), "text": msg["text"]})
    return out

def tg_send(cfg, text):
    for i in range(0, len(text), 3900):
        tg_call(cfg, "sendMessage", {"chat_id": cfg["chat_id"], "text": text[i:i + 3900], "disable_web_page_preview": True})

# ---------- e-mail send (owner only) ----------
def mail_owner(subject, markdown):
    cfg = ol.load_config()
    html = "<pre style='font:14px/1.45 Consolas,monospace;white-space:pre-wrap'>" + (
        markdown.replace("&", "&amp;").replace("<", "&lt;")) + "</pre>"
    ol.graph(cfg, "POST", f"/users/{cfg['mailbox']}/sendMail", body={
        "message": {"subject": subject, "body": {"contentType": "HTML", "content": html},
                    "toRecipients": [{"emailAddress": {"address": OWNER}}]},
        "saveToSentItems": False})

# ---------- the brief ----------
def build(days=2):
    now = datetime.now(LOCAL_TZ)
    todo, lines = [], []
    def L(s=""): lines.append(s)

    # eBay
    try:
        orders = eb.orders(30)
        ebay_err = None
    except Exception as e:
        orders, ebay_err = [], str(e)
    to_ship = [o for o in orders if o["status"] in ("NOT_STARTED", "IN_PROGRESS") and o["paid"] == "PAID"]
    in_transit = [o for o in orders if o["status"] == "FULFILLED" and when(o["created"]) and when(o["created"]) > now - timedelta(days=7)]
    rets = [r for r in eb.returns() if r.get("state") not in ("CLOSED",) and not r.get("error")]
    cancels = [c for c in eb.cancellations() if c.get("status") in ("CANCEL_REQUESTED", "CANCEL_PENDING") and not c.get("error")]

    # mail
    msgs, b = mail_section(days)
    tg = tg_config()
    tg_msgs = tg_incoming(tg) if tg else []

    # ----- DO FIRST (deadline-ordered) -----
    for o in to_ship:
        d = when(o["shipBy"])
        urgency = 0 if d and d.date() <= now.date() else 1
        todo.append((urgency, d or now, f"SHIP eBay order {o['legacyOrderId']} to {o['buyer']} — {', '.join(o['items'])[:90]} — ship by {day(o['shipBy'])} (${o['total']})"))
    for c in cancels:
        due = c["respondBy"] or next((re.search(r"by (\w{3} \d{1,2}, \d{4})", m["preview"] or "") for m in b.get("ebay_cancel", [])), None)
        due_txt = day(c["respondBy"]) if c["respondBy"] else (due.group(1) if hasattr(due, "group") else "soon")
        buyer = c.get("buyer") or next((o["buyer"] for o in orders if o["legacyOrderId"] == c["orderId"]), "buyer")
        todo.append((0, when(c["respondBy"]) or now, f"ANSWER cancel request from {buyer} on order {c['orderId']} ({c['reason'].lower().replace('_', ' ')}) — respond by {due_txt}"))
    for m in b.get("ebay_cancel", []):
        if cancels:
            break   # the API already listed it with the real deadline
        mm = re.search(r"by (\w{3} \d{1,2}, \d{4})", m["preview"] or "")
        todo.append((0, now, f"ANSWER eBay cancel request — \"{m['subject']}\" ({m['ago']}){' — respond by ' + mm.group(1) if mm else ''}"))
    for m in b.get("amazon_question", []) + b.get("ebay_question", []):
        if m.get("dup"):
            continue
        todo.append((1, when(m["received"]) or now, f"REPLY {m['from'].split('<')[0].strip()[:30]}: {m['subject'][:80]} ({m['ago']}) — \"{m['preview'][:110]}\""))
    for m in b.get("other", []):
        if CUSTOMER_WORDS.search(m["subject"]) and not NOISE_SENDERS.search(m["from"]) and not re.search(r"amazon|ebay|schwab|stripe", m["from"], re.I):
            todo.append((1, when(m["received"]) or now, f"REPLY {m['from'].split('<')[0].strip()[:30]}: {m['subject'][:80]} ({m['ago']})"))
    for r in rets:
        if r["state"] in ("ITEM_SHIPPED", "ITEM_DELIVERED", "RETURN_REQUESTED", "WAITING_FOR_RMA"):   # ball is in the seller's court
            todo.append((1, when(r["sellerRespondBy"]) or now, f"RETURN {r['returnId']} from {r['buyer']} — {r['reason']}, {r['state'].lower().replace('_', ' ')} — inspect, then refund ${r['refundDue']} within 2 days of delivery (deadline {day(r['sellerRespondBy'])})"))
    for m in b.get("amazon_sold", []):
        todo.append((0, now, f"SHIP Amazon — {m['subject'][:80]} ({m['ago']})"))
    for m in b.get("ebay_offer", []):
        todo.append((1, when(m["received"]) or now, f"OFFER {m['subject'][:90]} ({m['ago']})"))
    todo.sort(key=lambda t: (t[0], t[1]))

    L(f"# ING Morning Brief — {now.strftime('%A %B %d, %Y %H:%M')}")
    L()
    L("## Do first")
    if todo:
        for i, (_, _, t) in enumerate(todo, 1):
            L(f"{i}. {t}")
    else:
        L("Nothing is waiting on you. 🎉")
    L()
    L(f"## eBay — orders to ship: {len(to_ship)}")
    for o in to_ship:
        L(f"- {o['legacyOrderId']} | {o['buyer']} | {', '.join(o['items'])[:80]} | ${o['total']} | ship by {day(o['shipBy'])} | to {o['shipTo']}")
    if ebay_err:
        L(f"- (eBay API error: {ebay_err})")
    L()
    L(f"## eBay — shipped this week, in transit: {len(in_transit)}")
    for o in in_transit:
        trk = ", ".join(f"{t['carrier'] or ''} {t['number']}" for t in o["tracking"]) or "no tracking on file"
        L(f"- {day(o['created'])} {o['buyer']} — {o['items'][0][:60]} — {trk}")
    L()
    L(f"## eBay — open returns: {len(rets)}   cancel requests waiting: {len(cancels)}")
    for r in rets:
        L(f"- Return {r['returnId']} {r['buyer']} — {r['reason']} — {r['state']} — ${r['refundDue']} — respond by {day(r['sellerRespondBy'])}")
    for c in cancels:
        L(f"- Cancel {c['cancelId']} order {c['orderId']} — {c['reason']} — respond by {day(c['respondBy']) if c['respondBy'] else '?'}")
    L()
    L("## Amazon (from e-mail — no API yet)")
    for k, label in (("amazon_sold", "Orders"), ("amazon_question", "Customer messages"), ("amazon_case", "Cases")):
        for m in b.get(k, []):
            L(f"- {label}: {m['subject'][:90]} ({m['ago']}) — {m['preview'][:140]}")
    if not any(b.get(k) for k in ("amazon_sold", "amazon_question", "amazon_case")):
        L("- nothing new")
    L()
    L("## Buyer questions & offers (eBay e-mail)")
    for k in ("ebay_question", "ebay_offer"):
        for m in b.get(k, []):
            L(f"- {m['subject'][:100]} ({m['ago']}) — {m['preview'][:160]}")
    for m in b.get("answered", []):
        L(f"- answered already: {m['subject'][:80]}")
    if not (b.get("ebay_question") or b.get("ebay_offer")):
        L("- none")
    L()
    L("## Packages & deliveries")
    for m in b.get("delivery", []) + b.get("ebay_shipping", []):
        L(f"- {m['subject'][:90]} ({m['ago']}) — {m['from'].split('<')[0].strip()[:30]}")
    if not (b.get("delivery") or b.get("ebay_shipping")):
        L("- no delivery notices")
    L()
    L("## Money")
    for m in b.get("payment", []):
        L(f"- {m['subject'][:90]} — {m['from'].split('<')[0].strip()[:30]} ({m['ago']})")
    if not b.get("payment"):
        L("- nothing new")
    L()
    L("## Appointments & security")
    for k in ("appointment", "security"):
        for m in b.get(k, []):
            L(f"- {m['subject'][:90]} — {m['from'].split('<')[0].strip()[:30]} ({m['ago']})")
    if not (b.get("appointment") or b.get("security")):
        L("- nothing")
    L()
    L("## Other unread mail worth a look")
    for m in b.get("other", [])[:15]:
        L(f"- {m['subject'][:90]} — {m['from'].split('<')[0].strip()[:30]} ({m['ago']})")
    L(f"- (+{len(b.get('noise', []))} newsletters/promotions skipped; {len(msgs)} unread in the last {days} days)")
    L()
    L("## Telegram")
    if tg is None:
        L("- alert bot not paired on this PC")
    elif tg_msgs:
        for t in tg_msgs:
            L(f"- {t['when']}: {t['text'][:200]}")
    else:
        L("- nothing sent to the ING alert bot in the last 24h")
    L("- Personal Telegram chats and WhatsApp are NOT connected yet — they need a one-time sign-in from you (say the word).")
    return "\n".join(lines), todo

def main():
    args = sys.argv[1:]
    days = int(args[args.index("--days") + 1]) if "--days" in args else 2
    text, todo = build(days)
    os.makedirs(OUT_DIR, exist_ok=True)
    stamp = datetime.now(LOCAL_TZ).strftime("%Y-%m-%d")
    with open(os.path.join(OUT_DIR, f"{stamp}.md"), "w", encoding="utf-8") as f:
        f.write(text)
    with open(os.path.join(OUT_DIR, "latest.md"), "w", encoding="utf-8") as f:
        f.write(text)
    print(text)
    if "--no-send" in args:
        return
    status = []
    try:
        mail_owner(f"Morning brief {stamp}: {len(todo)} things to do", text)
        status.append("emailed")
    except Exception as e:
        status.append(f"email failed: {e}")
    tg = tg_config()
    if tg:
        try:
            short = text.split("## eBay — shipped this week")[0]   # the to-do list fits a phone screen
            tg_send(tg, short + "\n(full brief in your e-mail)")
            status.append("telegram sent")
        except Exception as e:
            status.append(f"telegram failed: {e}")
    with open(os.path.join(OUT_DIR, "send.log"), "a", encoding="utf-8") as f:
        f.write(f"{datetime.now(LOCAL_TZ).isoformat()} {'; '.join(status)}\n")
    print("\n" + "; ".join(status))

if __name__ == "__main__":
    try:
        main()
    except Exception as e:       # the scheduled task runs windowless; leave a trace
        os.makedirs(OUT_DIR, exist_ok=True)
        with open(os.path.join(OUT_DIR, "error.log"), "a", encoding="utf-8") as f:
            f.write(f"{datetime.now(LOCAL_TZ).isoformat()} {type(e).__name__}: {e}\n")
        raise
