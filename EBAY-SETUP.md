# eBay setup for app.inglisting.com — the part only you can do

Everything else about the hosted eBay connection is done. The app has your Client ID, Client
Secret and RuName, it builds a real eBay consent URL, and it has an endpoint waiting for eBay to
come back to. **One thing is missing, it lives in your eBay developer account, and it cannot be
automated: eBay has to be told that `https://app.inglisting.com/api/ebay/callback` is a place it
is allowed to send people.**

Until that is done, a seller on app.inglisting.com who clicks *Log into eBay* reaches a real eBay
consent screen, approves it, and is then sent to `http://localhost:9332` — the address your
desktop copy answers on, and which means nothing on their machine. It fails at the end, not the
start.

---

## What you need to know first

eBay does not take a redirect URL in the sign-in link. It takes a **RuName** — an opaque token —
and the URL that RuName stands for is stored on eBay's side. So this is a change to your eBay
account, not to the app.

**Your existing RuName is already spoken for.** It points at the relay on inglisting.com, which
redirects to `localhost:9332`, which is how the desktop app works. A RuName has one accepted URL.
**Do not repoint it** — that breaks every desktop copy, including yours.

So: **make a second one.**

---

## The steps

1. Sign in at **<https://developer.ebay.com>** with the developer account that owns the production
   keyset this app already uses. (If you have more than one developer account, it is the one whose
   App ID matches the Client ID in your `credentials.json`.)

2. Go to your **application keys** page, find the **Production** keyset, and open the section for
   redirect / OAuth settings. eBay has called this "User Tokens", "Get a Token from eBay via Your
   Application", and "Redirect URL" (RuName) at various times — I am not going to guess which
   wording your console shows today. It is the page where your existing RuName is listed.

3. **Add a new redirect URL entry.** eBay will generate a new RuName for it. Set:

   * **Your auth accepted URL** → `https://app.inglisting.com/api/ebay/callback`
   * **Your auth declined URL** → `https://app.inglisting.com/?ebay_error=declined`
   * **Your privacy policy URL** → whatever you use today (eBay usually requires one)

   Leave your existing RuName alone.

4. **Paste the new RuName back to me** — it looks like `Firstname_Lastname-Something-AbcDe-xxxxxx`.
   Send that string on its own. It is not a secret, but it is the only thing I cannot work out
   from here.

That is all. When I have it, the change on the server is one line in
`/etc/ing-listing-engine/.env`:

```
Credentials__EbayRuName=<the new RuName>
```

…and a container restart. No rebuild, no redeploy, nobody signed out.

---

## How you will know it worked

Sign in at <https://app.inglisting.com>, click **Log into eBay**, and approve the permissions. The
eBay tab should close itself and the app tab — which never went anywhere — should say
**Connected to eBay**.

If the eBay tab lands on a "this site can't be reached" page for `localhost:9332`, the RuName on
the server is still the old one. If eBay itself shows an error *before* the consent screen, saying
something about an invalid redirect_uri or an unauthorized client, then the RuName and the Client
ID are not from the same keyset.

---

## While you are in there: Send Offer

Separate problem, found on the way, and it affected your desktop app too.

The app used to ask eBay for five permissions, and the fifth — `sell.negotiation`, which is what
"Send Offer to Interested Buyers" runs on — is **not enabled on your production keyset**. eBay does
not skip a permission it will not grant; it refuses the whole sign-in. Every new eBay connection,
desktop or hosted, was dying at
`auth2.ebay.com/oauth2/errorOauth?errorId=invalid_scope` before the consent screen appeared.

The app now asks for the four it needs to list, and sign-in works. Send Offer will return 403 with
a message saying so. If you want it back:

1. On the same keyset page, find where eBay lists the API permissions or business policies your
   application is opted into, and enable the negotiation / Send Offer one. (This is sometimes an
   opt-in form rather than a checkbox — again, I am not guessing at today's wording.)
2. Tell me it is done, and I add one line to the server env file:
   `Ebay__RequestNegotiationScope=true`.

Do not set that line before eBay has granted it, or every sign-in breaks again.

---

## What must not happen

* **Do not put the hosted URL on the existing RuName.** Every installed desktop copy signs in
  through the relay against that one.
* **Do not paste tokens.** No access token or refresh token of yours goes anywhere near the
  server — hosted sellers connect their own eBay accounts and the tokens are stored encrypted
  per user. There is a test that fails if anyone ever tries to configure one server-side.
* **Do not put the Client Secret in an email, an issue, or this repository.** It is already on the
  server in `/etc/ing-listing-engine/.env`, root-owned, mode 600. That is the only copy it needs.
