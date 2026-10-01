"""eBay side of the morning brief: orders waiting to ship, shipped-and-in-transit, open returns,
open cancellation requests. Read-only against eBay; uses the token the desktop app already holds
in %LOCALAPPDATA%\\ING AutoLister\\credentials.json and refreshes into its OWN cache file so the
app's file is never written by this tool."""
import base64, json, os, time, urllib.error, urllib.parse, urllib.request
from datetime import datetime, timedelta, timezone

APP_CREDS = os.path.expandvars(r"%LOCALAPPDATA%\ING AutoLister\credentials.json")
CACHE = os.path.expandvars(r"%LOCALAPPDATA%\ING AutoLister\morning-brief\ebay-token.json")
API = "https://api.ebay.com"

def _creds():
    with open(APP_CREDS, encoding="utf-8") as f:
        return json.load(f)

def token():
    """Access token: the cached one if fresh, else refresh with the app's refresh token."""
    try:
        with open(CACHE, encoding="utf-8") as f:
            c = json.load(f)
        if c.get("exp", 0) > time.time() + 120:
            return c["token"]
    except Exception:
        pass
    d = _creds()
    # the app's own access token may still be good — use it before spending a refresh
    try:
        exp = datetime.fromisoformat(d["EbayTokenExpiresAt"].replace("Z", "+00:00"))
        if exp > datetime.now(timezone.utc) + timedelta(minutes=5) and d.get("EbayUserToken"):
            return d["EbayUserToken"]
    except Exception:
        pass
    basic = base64.b64encode(f"{d['EbayClientId']}:{d['EbayClientSecret']}".encode()).decode()
    body = urllib.parse.urlencode({"grant_type": "refresh_token", "refresh_token": d["EbayRefreshToken"]}).encode()
    req = urllib.request.Request(API + "/identity/v1/oauth2/token", data=body,
                                 headers={"Authorization": "Basic " + basic, "Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req, timeout=30) as r:
        j = json.load(r)
    os.makedirs(os.path.dirname(CACHE), exist_ok=True)
    with open(CACHE, "w", encoding="utf-8") as f:
        json.dump({"token": j["access_token"], "exp": time.time() + int(j.get("expires_in", 7200))}, f)
    return j["access_token"]

def get(path, params=None):
    url = API + path + ("?" + urllib.parse.urlencode(params, safe=":{}|,") if params else "")
    # The Post-Order API predates OAuth's Bearer scheme and only accepts "IAF <token>".
    scheme = "IAF " if path.startswith("/post-order/") else "Bearer "
    req = urllib.request.Request(url, headers={"Authorization": scheme + token(), "Accept": "application/json",
                                               "X-EBAY-C-MARKETPLACE-ID": "EBAY_US"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"eBay {path} -> {e.code}: {e.read().decode(errors='replace')[:300]}")

def _order_row(o):
    items = o.get("lineItems", [])
    ship_by = min((li.get("lineItemFulfillmentInstructions", {}).get("shipByDate", "") for li in items), default="")
    addr = ((o.get("fulfillmentStartInstructions") or [{}])[0].get("shippingStep", {}).get("shipTo", {}))
    tracking = []
    for f in o.get("_fulfillments", []):
        tracking.append({"carrier": f.get("shippingCarrierCode"), "number": f.get("shipmentTrackingNumber"), "shipped": f.get("shippedDate")})
    return {
        "orderId": o.get("orderId"), "legacyOrderId": o.get("legacyOrderId"), "created": o.get("creationDate"),
        "buyer": o.get("buyer", {}).get("username"), "status": o.get("orderFulfillmentStatus"), "paid": o.get("orderPaymentStatus"),
        "total": o.get("pricingSummary", {}).get("total", {}).get("value"),
        "items": [f"{li.get('quantity')}x {li.get('title')}" for li in items],
        "shipBy": ship_by, "shipTo": f"{addr.get('fullName','')}, {addr.get('contactAddress',{}).get('city','')} {addr.get('contactAddress',{}).get('stateOrProvince','')}".strip(", "),
        "tracking": tracking,
    }

def orders(days=30):
    """All orders from the last N days, with their shipments attached."""
    since = (datetime.now(timezone.utc) - timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    out, offset = [], 0
    while True:
        j = get("/sell/fulfillment/v1/order", {"filter": f"creationdate:[{since}..]", "limit": "50", "offset": str(offset)})
        for o in j.get("orders", []):
            if o.get("orderFulfillmentStatus") in ("FULFILLED", "IN_PROGRESS"):
                try:
                    o["_fulfillments"] = get(f"/sell/fulfillment/v1/order/{o['orderId']}/shipping_fulfillment").get("fulfillments", [])
                except Exception:
                    o["_fulfillments"] = []
            out.append(_order_row(o))
        offset += 50
        if offset >= j.get("total", 0):
            break
    return out

def returns():
    """Open returns (Post-Order API)."""
    try:
        j = get("/post-order/v2/return/search", {"return_state": "OPEN", "limit": "50"})
    except RuntimeError as e:
        return [{"error": str(e)}]
    rows = []
    for r in j.get("members", []):
        rows.append({"returnId": r.get("returnId"), "state": r.get("state"), "status": r.get("status"),
                     "buyer": r.get("buyerLoginName"), "item": (r.get("creationInfo", {}).get("item") or {}).get("itemTitle") or r.get("creationInfo", {}).get("item", {}).get("itemId"),
                     "reason": r.get("creationInfo", {}).get("reason"), "opened": r.get("creationInfo", {}).get("creationDate", {}).get("value"),
                     "sellerRespondBy": (r.get("sellerResponseDue") or {}).get("respondByDate", {}).get("value"),
                     "refundDue": (r.get("buyerTotalRefund") or {}).get("estimatedRefundAmount", {}).get("value")})
    return rows

def cancellations():
    """Cancellation requests awaiting the seller."""
    try:
        j = get("/post-order/v2/cancellation/search", {"cancel_status": "CANCEL_REQUESTED", "limit": "50"})
    except RuntimeError as e:
        return [{"error": str(e)}]
    rows = []
    for c in j.get("cancellations", []):
        rows.append({"cancelId": c.get("cancelId"), "status": c.get("cancelStatus"), "buyer": c.get("buyerLoginName"),
                     "orderId": c.get("legacyOrderId"), "reason": c.get("cancelReason"),
                     "requested": (c.get("cancelRequestDate") or {}).get("value"),
                     "respondBy": (c.get("sellerResponseDue") or {}).get("value")})
    return rows

if __name__ == "__main__":
    o = orders(14)
    print("orders (14d):", len(o))
    for r in o:
        print(" ", r["status"], r["paid"], r["created"][:10], r["buyer"], r["total"], "| shipBy", r["shipBy"][:10], "|", r["items"][0][:60], "| tracking", [t["number"] for t in r["tracking"]])
    print("returns:", json.dumps(returns(), indent=1)[:1500])
    print("cancellations:", json.dumps(cancellations(), indent=1)[:1500])
