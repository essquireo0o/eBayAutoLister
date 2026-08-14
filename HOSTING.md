# Hosting ING Listing Engine

How to deploy the **hosted** build — the multi-user, browser-only configuration — onto a Linux
server. If you are looking for the Windows desktop app, this is the wrong document; that one is an
MSI and lives in `build-installer.ps1`.

The two builds are one codebase with one compile-time switch. The desktop build is
`net10.0-windows` with a tray icon, a single-instance mutex, a fixed port and one seller's
`credentials.json`. The hosted build is `net10.0`, binds whatever port it is given, requires a
sign-in for every endpoint, and keeps each user's credentials as an encrypted row in the database.
`-p:Hosted=true` is what chooses, and the Dockerfile passes it.

---

## 1. What you need before you start

* A Linux host with Docker.
* A **public hostname with HTTPS** — a domain, and a reverse proxy that terminates TLS
  (Caddy, nginx, Traefik). Not optional, for two independent reasons:
  * The session cookie is marked `Secure` unconditionally. Over plain `http://` the browser
    accepts the sign-in response and silently discards the cookie, so signing in "works" and the
    next page is signed out again. There is no setting that relaxes this.
  * eBay will not register a redirect URL that is not HTTPS. See §6.
* An **Anthropic API key**, if AI listing generation is meant to work. On a hosted deployment this
  is the *owner's* key and it pays for every user's generations — see §3 and §5.

---

## 2. Build and run

```bash
docker build -t ing-listing-engine .
```

The build publishes the HOSTED configuration for `linux-x64` and runs as a non-root user
(UID 1654, the unprivileged `app` account the .NET images provide). Nothing in the image runs as
root, and nothing in the image contains a secret — every secret arrives at run time.

```bash
docker run -d --name ing-listing-engine \
  --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -v ing-listing-data:/data \
  -e CREDENTIALS_ENCRYPTION_KEY="…" \
  -e ANTHROPIC_API_KEY="…" \
  -e Ai__DailyGenerationLimit=5 \
  ing-listing-engine
```

`-p 127.0.0.1:8080:8080` and not `-p 8080:8080`. The second form publishes the container on every
interface, which puts a plain-HTTP copy of the app on the public internet beside the HTTPS one,
and Docker writes its own iptables rules — so a host firewall that looks closed does not
necessarily close it. Bind to loopback and let the proxy be the only way in.

### docker compose

```yaml
services:
  app:
    image: ing-listing-engine
    restart: unless-stopped
    ports:
      - "127.0.0.1:8080:8080"
    volumes:
      - ing-listing-data:/data
    env_file: /etc/ing-listing-engine/.env   # root-owned, chmod 600. See §5.
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "5"

volumes:
  ing-listing-data:
```

### Reverse proxy

Caddy needs no more than this, and obtains and renews the certificate itself:

```
app.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

---

## 3. Environment variables

Configuration is read from environment variables. Hierarchical keys use a **double underscore**
for the `:` separator — `Credentials:EncryptionKey` is `Credentials__EncryptionKey`. Two keys also
accept the conventional flat name, because that is the form every host's settings screen hands
you.

### Required

| Variable | Secret | What it does |
|---|---|---|
| `CREDENTIALS_ENCRYPTION_KEY`<br>(or `Credentials__EncryptionKey`) | **YES** | The AES-256-GCM key that every user's stored eBay refresh token is encrypted with. At least 16 characters, or 32 bytes of base64. **The app refuses to start without it** — see §8. |
| `ANTHROPIC_API_KEY`<br>(or `Credentials__AnthropicApiKey`) | **YES** | The AI key every user's listing generation is billed to. Without it the app runs and AI generation fails; nothing else is affected. |

### Storage and networking

| Variable | Secret | What it does |
|---|---|---|
| `XDG_DATA_HOME` | no | **The database path.** Everything persisted goes under `$XDG_DATA_HOME/ING AutoLister/`. The image sets it to `/data`, so the SQLite database is `/data/ING AutoLister/App_Data/ing_listing_engine.db`. See §4 before changing it. |
| `HOME` | no | Set to `/data` in the image as the fallback root, used only if `XDG_DATA_HOME` is ever cleared. |
| `ASPNETCORE_URLS` | no | What the app binds. `http://+:8080` in the image. The hosted build has no opinion about the port — unlike the desktop build, which will only ever use 9332. |

### Optional

| Variable | Secret | What it does |
|---|---|---|
| `Ai__DailyGenerationLimit` | no | AI generations allowed per user per day. Defaults to **10**. This is the only thing between a public sign-up form and an unbounded bill on your own Anthropic key — set it deliberately. |
| `Auth__SignUpCode` | **YES** | When set, sign-up also requires this code. Unset means **anyone on the internet can create an account** and spend your AI budget. Set it unless open sign-up is a decision you have actually made. |
| `Credentials__AdminKey` | **YES** | The key for the owner dashboard at `/owner?k=…`. One is generated in memory at startup if unset, which means it changes on every restart. |
| `Credentials__StripeSecretKey`<br>`Credentials__StripeWebhookSecret`<br>`Credentials__StripePublishableKey` | **YES** (first two) | Billing. Only needed if you are charging. |
| `Credentials__MarketCompsApiUrl`<br>`Credentials__MarketCompsApiKey` | **YES** (the key) | The hosted sold-comps API. Without it, pricing falls back to whatever other sources are configured. |
| `OPENAI_API_KEY` | **YES** | Optional secondary AI provider. |

### eBay — the application, not the accounts

| Variable | Secret | What it does |
|---|---|---|
| `Credentials__EbayClientId`<br>`Credentials__EbayDevId` | no | Your eBay **application's** identifiers, from your developer keyset. Shared by every user: the consent screen names your application, not theirs. |
| `Credentials__EbayClientSecret` | **YES** | The other half of the same registration. Without it a sign-in reaches eBay's consent screen and then fails at the moment tokens are issued. |
| `Credentials__EbayRuName` | no | The RuName whose registered URL is this deployment's callback. **Not the one your desktop copies use** — see `EBAY-SETUP.md`. |
| `Ebay__RedirectUri` | no | The URL eBay must be registered to return to: `https://your-host/api/ebay/callback`. Defaults to the desktop relay (`https://inglisting.com/api/ebay/callback`), which is wrong for every hosted deployment. |
| `Ebay__RequestNegotiationScope` | no | Set to `true` **only if** your keyset is enabled for Send Offer to Interested Buyers. eBay refuses the *entire* sign-in with `invalid_scope` if it is not — not the feature, the sign-in. Defaults to off. |

Setting all four is what lets somebody sign up and connect eBay without owning an eBay developer
account. Leave them unset and each user is asked for their own, which is the desktop behaviour and
is fine if that is what you want — blank means "not configured" and never overwrites a value a user
entered themselves.

**`Ebay__RedirectUri` does not go on the wire.** eBay's `redirect_uri` parameter takes the RuName,
and the URL it stands for lives in eBay's console where this app cannot see it. The setting is what
the app *reports* — in the log, in `/api/ebay/status`, and in the sentence telling somebody which
URL to register. Getting it wrong misinforms; getting the console entry wrong breaks the sign-in.

**Deliberately still not on this list: any eBay token.** `EbayUserToken` and `EbayRefreshToken` are
one seller's eighteen-month grant to sell on their account. They are read only from that user's own
encrypted row, never from configuration, and `HostedEbayCredentialsTests` fails if a property that
could hold one is ever added to `ServerCredentials`. The business policies and the sandbox flag are
per user for the same reason.

---

## 4. Data, and what happens if you get it wrong

Everything the deployment cannot lose is under `/data`:

```
/data/ING AutoLister/App_Data/ing_listing_engine.db   ← users, listings, encrypted credentials
/data/ING AutoLister/App_Data/auth-keys/              ← the data-protection key ring
/data/ING AutoLister/generated-photos/                ← images referenced by live listings
/data/ING AutoLister/photos/                          ← each seller's own photo library
```

* **Mount a volume at `/data`.** Without one, the container writes to its own filesystem and every
  account, listing and eBay connection is destroyed the moment the container is replaced — which
  is what deploying a new version does.
* **`auth-keys/` matters as much as the database.** It is the key ring the session cookies are
  signed with. Lose it and every signed-in user is signed out; it is on the same volume so that
  this is one decision rather than two.
* **Named volume, or chown your bind mount.** With a named volume (`-v ing-listing-data:/data`)
  Docker copies the image's ownership and it works. With a bind mount
  (`-v /srv/ing/data:/data`) the host directory's ownership wins, and the app — running as UID
  1654 — cannot write. Fix with `chown -R 1654:1654 /srv/ing/data`.
* **Back up the whole `/data` volume**, not just the `.db`. A SQLite `-wal` sidecar holds
  committed transactions that are not yet in the main file, so copying the `.db` alone from a
  running container can lose the most recent writes. `docker compose stop` first, or use
  `sqlite3 … ".backup"`.

---

## 5. Secrets — what must never be committed

**These belong in the host's environment settings, and nowhere else:**

* `CREDENTIALS_ENCRYPTION_KEY`
* `ANTHROPIC_API_KEY` (and `OPENAI_API_KEY`)
* `Auth__SignUpCode`
* `Credentials__AdminKey`
* every `Credentials__Stripe*` value
* `Credentials__MarketCompsApiKey`
* `Credentials__EbayClientSecret` — your eBay application's, and the only eBay secret you handle
* each user's eBay OAuth tokens — which you never handle: they arrive from the user's browser and
  are written to their row encrypted.

**Never** put any of them in this repository, in `appsettings.json`, in the Dockerfile, in a
`docker-compose.yml` that is committed, in a `docker build --build-arg`, or in a log line. A
build argument is visible in `docker history` on any machine that pulls the image; committed is
committed even after a later deletion, because it stays in the git history.

Where they should go: a root-owned env file, mode 600, outside the repository —
`/etc/ing-listing-engine/.env`, referenced by `env_file:` — or your host's secrets UI. `.env`,
`credentials.json`, `secrets.json` and every `*.db` are excluded by both `.gitignore` and
`.dockerignore`, so the accident is prevented in two places, but neither of them can help with a
secret you type into a file with a new name.

### Rotating the encryption key

Changing `CREDENTIALS_ENCRYPTION_KEY` makes every stored eBay connection unreadable — the app
detects it and stops writing to those rows rather than overwriting them, but every user has to
reconnect their eBay account. It is recoverable, not free. Generate one once and keep it:

```bash
openssl rand -base64 32
```

---

## 6. eBay OAuth: the hosted deployment needs its own redirect URL

**This is the step that is easy to miss, and it fails after the user has already approved
everything.**

The desktop app's sign-in has four hops, and the last one is hard-coded:

```
app → eBay consent → https://inglisting.com/api/ebay/callback (relay) → http://localhost:9332
```

`localhost:9332` exists because eBay will not accept a redirect URL that is not HTTPS, so the
desktop app cannot be registered with eBay directly — a relay on a real domain is registered
instead, and it bounces the browser back to the app on the user's own machine.

**A hosted deployment cannot use that chain.** The last hop would send the user's browser to port
9332 *on their own computer*, where your server is not. The symptom is not an error message from
this app: the consent screen appears, the user approves it, and the browser lands on a dead tab or
a connection-refused page that eBay's own wording blames on the application.

### What to do

1. In the [eBay developer console](https://developer.ebay.com/my/keys), on the application whose
   Client ID is being used, add a **redirect URL / RuName** whose *auth accepted URL* is:

   ```
   https://your-host.example.com/api/ebay/callback
   ```

   Exactly that path. HTTPS, your real public hostname, no trailing slash. Keep the existing
   `https://inglisting.com/api/ebay/callback` entry — the desktop app still needs it. An eBay
   application can hold more than one.

2. Paste the **RuName** for that new entry into the app under Settings → eBay → Advanced,
   alongside the Client ID and Client Secret.

   Remember from §3 that eBay credentials are per user. If your users bring their own eBay
   developer applications, each of them has to do step 1 in their own console, against your
   hostname — so put the URL somewhere they can copy it. If instead the deployment serves one
   seller, this is a one-time job.

`/api/ebay/callback` is the app's direct callback: eBay redirects straight to it with the
authorization code, and the app exchanges the code for tokens itself instead of collecting them
from the relay. It requires a signed-in session, which is what attributes the tokens to the right
user — and it gets one, because the session cookie is `SameSite=Lax` and eBay's redirect is a
top-level navigation, which is precisely the case `Lax` still sends cookies on.

---

## 7. What does not work in the container

Stated plainly so it is not discovered during a demo. These features drive a real browser through
Playwright and Node, neither of which is in the image:

* **Terapeak research** and the saved eBay research session.
* **Facebook Marketplace** listing and its saved session.
* **Desktop notifications** and the tray icon — there is no desktop.

Everything else — AI listing generation, eBay publishing through the Sell API, pricing from stored
and hosted comps, the money and restock boards — is HTTP and works.

---

## 8. Verifying a deployment

Run these against the real hostname after deploying. Each one has a right answer.

```bash
# 1. Alive, and no TLS warning.
curl -I https://your-host.example.com/health          # 200

# 2. Signed out is refused — not 200, and not a login page dressed as JSON.
curl -o /dev/null -w '%{http_code}\n' \
     https://your-host.example.com/api/earnings/summary  # 401

# 3. Plain HTTP goes to HTTPS.
curl -I http://your-host.example.com                  # 301/308

# 4. No secret is served to the browser. Expect no output at all.
curl -s https://your-host.example.com/signin.html | grep -iE 'sk-ant|sk-live|-----BEGIN'
```

`/health`, sign-in and sign-up are the *only* three endpoints reachable without a session. Every
other endpoint the app maps is closed by a fallback authorization policy — closed by default,
rather than by a list somebody has to remember to add to.

### The two open endpoints are the ones worth guessing at

Closed-by-default protects the other 189. It does nothing for the three that are open on purpose,
and one of them takes a password. Two limits stand behind it, and they answer different attacks:

| | Counted per | Limit | Then | Where |
|---|---|---|---|---|
| **Account lockout** | email address | 5 consecutive failures | refused for **15 minutes**, correct password included | `SignInThrottle` |
| **Spray limit** | client IP address | 20 attempts per **5 minutes**, sign-in and sign-up together | refused with `Retry-After` | `AddRateLimiter`, `HostedAuth` |

Neither is configurable and neither needs to be. The first stops one account being ground through
a word list; the second stops one host trying two passwords against ten thousand accounts, where no
single account ever reaches five. The counter is a row in the same SQLite database as everything
else, so it survives the restart that a deploy is — an in-memory counter would hand an attacker
five fresh attempts every time you shipped a version.

Both of those are about *online* guessing. The third limit is about the password itself, and it is
the one that matters if the database is ever copied: `PasswordPolicy` refuses anything under **12
characters**, anything equal to the email address it is being set on, and anything on a small
built-in list of the passwords that get tried first. There is deliberately **no** "must contain a
digit and a symbol" rule — that produces `Passw0rd!`, which is on every cracking dictionary, while
length is what actually costs an attacker. Enforced server-side in `UserStore.Create`; the sign-up
page's `minlength` is a courtesy that saves a round trip and decides nothing.

What is stored is **PBKDF2-HMAC-SHA256, 210,000 iterations, a 16-byte random salt per row and a
32-byte derived key**, compared in constant time — see `PasswordHash`. The iteration count is
written into each row rather than read from the constant, so it can be raised without locking out
anyone who signed up before the change. `PasswordHashTests` recomputes a hash from the outside and
requires it to match byte for byte, so the scheme cannot be quietly swapped for a cheaper one.

Three things about it are deliberate and look like bugs if you do not know:

* **A correct password during a lockout is still refused.** A lock that a correct guess opens is
  not a lock; it is a delay on the guess that would have worked.
* **An address that never signed up locks out too**, in the same words. If the lock applied only to
  real accounts, the sixth attempt would say "locked" for a registered address and "wrong password"
  for an invented one — and the form would be a way to ask which addresses have accounts here. The
  same reasoning is why both refusals use one sentence and why an unregistered address is made to
  cost a PBKDF2 verification it does not need (`PasswordHash.VerifyNothing`), so a stopwatch cannot
  tell them apart either.
* **The lockout message says plainly that it is a lockout and roughly when it lifts.** That is not
  a leak. Somebody guessing passwords already knows they have been guessing; the only person a
  vaguer message fools is the seller who mistyped and is now staring at "email and password do not
  match" while typing the right one.

The cost is that anyone who knows a seller's address can keep that seller out by guessing wrong
five times every quarter of an hour. That is the standard trade and it is the right way round —
fifteen minutes of inconvenience against somebody else's eBay account.

Prove it against the real site, from off the box, with accounts nobody is using:

```bash
bash deploy-bruteforce-check.sh          # 5 x 401, then 429, and a neighbour still at 200
bash deploy-bruteforce-check.sh lift     # 15+ minutes later: 200, with nothing cleared by hand
```

**Use throwaway accounts, and do not run it twice in a row.** The lockout is per account so the
owner's account is untouched — but the spray limit is per IP and this spends about half of it, so
the second run refuses the sign-in page you are sitting in front of for a few minutes.

### If it will not start

Read the logs first (`docker logs ing-listing-engine`); the failure modes below are the ones that
announce themselves.

| Symptom | Cause |
|---|---|
| Exits immediately with *"The hosted build stores each user's eBay tokens encrypted and has no key to do it with"* | `CREDENTIALS_ENCRYPTION_KEY` is not set. Deliberate: a hosted build with nowhere to get the key fails on the way up, rather than at the first token write with the tokens in the clear. |
| Starts, then `UnauthorizedAccessException` writing to `/data` | Bind mount owned by the wrong user. `chown -R 1654:1654` the host directory (§4). |
| Sign-in returns 200, next request is signed out again | The site is being served over plain HTTP. The session cookie is `Secure` and the browser is discarding it (§1). |
| Everyone is signed out after a redeploy | `auth-keys/` was not on a persistent volume (§4). |
| eBay consent completes, browser lands nowhere | The redirect URL is still the desktop one (§6). |
| AI generation fails, everything else is fine | `ANTHROPIC_API_KEY` is unset or wrong. |
| *"You've used your AI generations for today"* | Working as configured. `Ai__DailyGenerationLimit`, per user per day (§3). |
| *"Too many failed sign-in attempts. This account is locked…"* — and the password is right | Working as designed. Five consecutive misses lock the address for 15 minutes and a correct password does not open it early (§8). It lifts on its own; there is nothing to clear. To end it now: `DELETE FROM sign_in_failures WHERE email_normalized='…';` in the database. |
| *"Too many sign-in attempts from this connection"* on the first try of the day | The per-IP budget, not the account (§8). 20 attempts per 5 minutes shared by sign-in and sign-up — usually a script on the same network, or `deploy-bruteforce-check.sh` run a moment ago. `Retry-After` says how long. |
| eBay's tab lands on `errorOauth?errorId=invalid_scope` | The keyset is not enabled for a scope being asked for. Almost always `Ebay__RequestNegotiationScope=true` on a keyset without Send Offer. Unset it. |
| eBay's tab lands on a dead `localhost:9332` after consent | `Credentials__EbayRuName` is a RuName registered to the desktop relay. It needs one registered to `Ebay__RedirectUri` — `EBAY-SETUP.md`. |
| *"Add your eBay Client ID and Client Secret in Settings first"* on a hosted deployment | `Credentials__EbayClientId` / `__EbayClientSecret` are unset, so each user is being asked for their own (§3). |
| Everyone signed out at once, and the browser warns about the certificate | The certificate expired. The session cookie is `Secure`, so losing HTTPS logs everyone out (§1). Renewal is §13. |
| The container is gone after a reboot | Something stopped it deliberately — `unless-stopped` does not restart a container that was stopped by hand (§10). |

---

## 9. Deploying a new version

The live deployment is `app.inglisting.com` — a Hetzner cpx11, Ubuntu 24.04, 2 GB of RAM, reached
with `ssh -i ~/.ssh/ing_hetzner root@178.156.154.41`. Three files on it are the whole deployment:

| Path | What it is |
|---|---|
| `/opt/ing-listing-engine/docker-compose.yml` | the service definition — restart policy, port binding, volume, log caps |
| `/etc/ing-listing-engine/.env` | **every secret**, root-owned, mode 600, referenced by `env_file:`. Never in the repository — §5 |
| `/etc/caddy/Caddyfile` | the public front door and the TLS certificate — §13 |

**The image is built on a workstation and shipped in.** Not built on the server: the .NET SDK
build stage wants more memory than a 2 GB box has, and an out-of-memory build that takes the
running app down with it is a bad way to learn that. On this workstation Docker lives in WSL, not
in PowerShell.

```bash
# 1. Build. In WSL, from the repository root.
docker build --provenance=false --sbom=false -t ing-listing-engine:latest .

# 2. Ship it over ssh — save | gzip | load, no registry involved.
bash deploy-ship-image.sh

# 3. Recreate the container from the new image. /data is untouched by this.
ssh -i ~/.ssh/ing_hetzner root@178.156.154.41 \
    'cd /opt/ing-listing-engine && docker compose up -d'

# 4. Prove it from off the box — real DNS, real certificate, no -k.
bash deploy-verify.sh
```

`--provenance=false --sbom=false` is not optional here. Without it buildx writes an attestation
manifest list rather than a plain image, and `docker save` on one end and `docker load` on the
other then produce something the daemon holds but will not run under the tag you asked for.

`docker compose up -d` recreates the container only if something it can see has changed. The
image ID changes on a rebuild, so a real deploy is picked up; an unchanged rebuild is a no-op, and
`--force-recreate` is the way to insist.

The database builds its own schema on start — each store creates its tables if they are missing and
adds new columns in place — so there is no migration step to run. Roll back by rebuilding from the
previous commit, but note that a rollback does not un-migrate anything: it is safe across versions
that added columns, and not across one that changed the meaning of an existing one.

**Take a backup before deploying a version you have not run before** — `ing-backup.sh` on the
server, §12. It is the only step here that cannot be undone by rebuilding.

### Changing a secret

Secrets never travel as command arguments, because `argv` is world-readable in `ps`:

```bash
pwsh -File deploy-make-env.ps1      # writes the env file to the Windows temp folder
bash deploy-push-env.sh             # pushes it over ssh stdin, shreds the local copy, prints
                                    # the permissions and the key names — never the values
ssh -i ~/.ssh/ing_hetzner root@178.156.154.41 \
    'cd /opt/ing-listing-engine && docker compose up -d'   # env_file is read at create time only
```

That last step is the one that gets forgotten. `env_file:` is read when the container is
**created**, so editing `/etc/ing-listing-engine/.env` changes nothing at all until the container
is recreated — and the symptom is a deployment that behaves exactly as it did before the change,
which reads as "the edit did not save".

---

## 10. Restarts: what comes back by itself

Nothing here needs a human after a reboot or a crash.

```yaml
restart: unless-stopped
```

in `/opt/ing-listing-engine/docker-compose.yml` is the whole mechanism, and it covers both cases
because Docker's own service is enabled at boot (`systemctl is-enabled docker` → `enabled`) and
restores the containers it was running. There is no systemd unit for the app to maintain, and
there should not be one: two things that both believe they own the container is worse than either.

* **Crash** — the process exits non-zero, Docker restarts it, with a backoff that grows if it
  keeps happening.
* **Reboot** — Docker starts at boot and brings the container back with it.
* **`docker compose stop`** — stays stopped, including across a reboot. That is what
  `unless-stopped` means and why it is the right policy rather than `always`: a deliberate stop
  for maintenance survives someone rebooting the box in the middle of it.

**`docker kill` counts as a deliberate stop too**, which is worth knowing before you use it to
test any of this. The daemon records that a human asked for the container to end, and the restart
policy is then not applied — so `docker kill ing-listing-engine` leaves the site returning 502
from Caddy until somebody runs `docker compose up -d`. That is correct behaviour and not a bug in
the policy, but it makes `docker kill` a test that always "fails".

To test crash recovery honestly, kill the process from the **host**, outside the container's PID
namespace — the daemon did not ask for that, so it is an ordinary crash:

```bash
kill -9 "$(docker inspect ing-listing-engine --format '{{.State.Pid}}')"
docker inspect ing-listing-engine --format '{{.State.Status}} restarts={{.RestartCount}}'
```

`RestartCount` going up by one is the proof. Signalling PID 1 from *inside* the container does
nothing at all, for a third reason: the kernel does not deliver a signal with a default action to
PID 1 of a namespace, so `docker exec … kill -9 1` returns success and kills nothing.

Caddy is a normal enabled systemd service and comes back the same way, so the certificate and the
redirect come back with it.

To check the policy is actually on the *running* container — the compose file describes what the
container would be created with, not necessarily what it was:

```bash
docker inspect ing-listing-engine --format '{{.HostConfig.RestartPolicy.Name}}'   # unless-stopped
```

Verified by rebooting the live server; the transcript is at the end of §14.

---

## 11. Reading the logs

```bash
# The app. Follow it, or ask for a window.
cd /opt/ing-listing-engine && docker compose logs -f
docker logs ing-listing-engine --since 30m
docker logs ing-listing-engine --tail 200

# The front door: TLS, certificate renewal, and every request that never reached the app.
journalctl -u caddy -f
journalctl -u caddy --since today --no-pager

# The nightly backup.
cat /var/log/ing-listing-engine-backup.log

# Is it healthy, and if not, what did the last probe say?
docker inspect ing-listing-engine --format '{{.State.Health.Status}}'
docker inspect ing-listing-engine --format '{{json .State.Health.Log}}' | tail -c 800
```

**The app's logs are capped and old ones are gone**, deliberately:

```yaml
logging:
  driver: json-file
  options:
    max-size: "10m"
    max-file: "5"
```

Fifty megabytes, rotated, per container. Uncapped is the default, and the default means one
repeating exception can fill a 38 GB disk overnight — at which point SQLite cannot write, Caddy
cannot write, and the box needs a human before it serves anything again. Fifty megabytes of
history is worth more than that risk. If you need to keep more than the last few days, ship the
logs somewhere rather than raising the cap.

A log line is only the app's own output. Anything the reverse proxy answered by itself — a TLS
failure, a 502 while the container was restarting — appears in `journalctl -u caddy` and never in
the app's log at all. When the app's log looks suspiciously quiet during an outage, that is
because the requests were not reaching it.

---

## 12. Backups, and how to restore one

A nightly cron job dumps the database to `/var/backups` and keeps seven days.

| | |
|---|---|
| What runs | `/usr/local/bin/ing-backup.sh` (in the repository as `ing-backup.sh`) |
| When | 03:17 daily, from `/etc/cron.d/ing-listing-engine-backup` |
| Where | `/var/backups/ing-listing-engine/ing-listing-engine-YYYY-MM-DD.tar.gz`, mode 600 |
| Retention | the newest **7** files; older ones are deleted on each run |
| Log | `/var/log/ing-listing-engine-backup.log`, rotated monthly |
| Install / reinstall | `bash deploy-install-backup.sh` from the repository root |

Each archive holds two things, and it holds both on purpose:

```
ing_listing_engine.db   users, listings, encrypted eBay credentials
auth-keys/              the key ring the session cookies are signed with
```

A database restored without `auth-keys/` comes up with every user signed out, because the cookies
in their browsers were signed with a key ring that no longer exists. They are one recovery unit.

The database is copied with **`sqlite3 .backup`, not `cp`**. A SQLite database in WAL mode keeps
committed transactions in a `-wal` sidecar until a checkpoint moves them, so copying the `.db`
alone from underneath a running app loses the most recent writes — and loses them invisibly, until
the restore. `.backup` takes the same locks the app does. Nothing is stopped: a backup that needs
downtime is a backup that gets skipped.

Every run then reads back what it just wrote with `PRAGMA integrity_check` and refuses to keep an
archive that fails. A backup nobody has read is a hope, not a backup.

### Check it is still running

```bash
tail -3 /var/log/ing-listing-engine-backup.log      # a good night is one line starting "ok"
ls -la /var/backups/ing-listing-engine/             # newest file should be dated today or yesterday
```

Failures are lines beginning `FAILED`. There is no MTA on this box, so nothing is emailed —
the log is the only place a failure appears, and an ageing newest-file is what a monitor should
watch for.

### Get a copy off the box

Seven days of backups on the same disk as the database survives a mistake, not a dead server.

```bash
scp -i ~/.ssh/ing_hetzner \
    root@178.156.154.41:/var/backups/ing-listing-engine/ing-listing-engine-2026-08-13.tar.gz .
```

### Restore

Destructive and deliberate: this replaces the live database with the one in the archive, and
everything written since that archive was taken is gone. Read it through before starting.

```bash
cd /opt/ing-listing-engine
ARCHIVE=/var/backups/ing-listing-engine/ing-listing-engine-2026-08-13.tar.gz
DATA=$(docker volume inspect ing-listing-engine_ing-listing-data --format '{{.Mountpoint}}')/'ING AutoLister'/App_Data

# 1. Stop the app. Restoring underneath a running process corrupts what you are restoring.
docker compose stop

# 2. Keep what is being replaced. This is the step that makes a wrong choice of archive survivable.
mv "$DATA/ing_listing_engine.db" "$DATA/ing_listing_engine.db.before-restore"
mv "$DATA/auth-keys" "$DATA/auth-keys.before-restore"

# 3. Unpack the archive over the top.
tar xzf "$ARCHIVE" -C "$DATA"

# 4. Hand it to the account inside the container, which is UID 1654 and not root.
chown -R 1654:1654 "$DATA"

# 5. Back up.
docker compose up -d
docker compose logs --tail 40
```

Then sign in and look at something real — a listing, the money board — before deleting the
`.before-restore` copies. Two things make the restore look broken when it is not: a `-wal` or
`-shm` sidecar left behind from the old database (delete them in step 2 along with the `.db`; the
archive contains a checkpointed database and needs neither), and forgetting step 4, which produces
`UnauthorizedAccessException` on the way up because root now owns the files and the app does not
run as root.

---

## 13. When the certificate does not renew

Caddy obtains and renews the Let's Encrypt certificate itself, in-process, starting about 30 days
before expiry. There is no certbot and no cron job — which means there is also nothing to check
until something is wrong.

```bash
# How long is left, asked from outside.
echo | openssl s_client -connect app.inglisting.com:443 -servername app.inglisting.com 2>/dev/null \
  | openssl x509 -noout -subject -issuer -dates

# What Caddy thinks about it.
journalctl -u caddy --no-pager | grep -iE 'certificate|acme|renew|error' | tail -30
```

Renewal starts a month early, so a failure is not urgent on the day you notice it — but it is not
self-healing either, and the thirty days are the whole budget. Causes, in the order they actually
happen:

| What you will see | Cause and fix |
|---|---|
| `could not get certificate from issuer` / timeouts on the HTTP challenge | **Port 80 is not reachable.** Let's Encrypt validates by connecting to it from outside, so a firewall that allows only 443 breaks renewal while the site still works. `ufw status` must show 80 and 443 allowed. |
| `no such host`, or the challenge reaching someone else's server | **DNS moved.** `dig +short app.inglisting.com` must still be `178.156.154.41`. |
| `too many certificates already issued` | **Rate limited** — five duplicate certificates per week. Caused by restarting Caddy in a loop while debugging something else. Do not keep restarting; the existing certificate is still valid and the limit clears in a week. Use Let's Encrypt's staging endpoint if you must iterate. |
| Renewal is fine but the browser still warns | The old certificate is cached in the proxy's memory. `systemctl reload caddy`. |

Force an attempt and watch it, rather than waiting for the timer:

```bash
systemctl reload caddy && journalctl -u caddy -f
```

If it cannot be fixed in time, the certificate expiring does not take the app down — it takes
**HTTPS** down, and §1 explains why that is the same thing in practice: the session cookie is
`Secure`, so over plain HTTP everyone is signed out on every page. There is no configuration that
degrades gracefully here. Fix the certificate.

The certificate and the account key live in `/var/lib/caddy/.local/share/caddy`. That directory is
worth keeping if you ever rebuild the box — losing it is not fatal, but it means requesting
everything again from a rate limit you may already have spent.

---

## 14. The hardening, as verified on 2026-08-13

Run against the live server, not a copy of it. Reproduce any of it with the `deploy-*.sh` scripts
in the repository root.

### The server was rebooted, and nothing was started by hand

```
$ ssh -i ~/.ssh/ing_hetzner root@178.156.154.41 'uptime -s; systemctl reboot'
2026-08-13 21:31:22

$ bash deploy-reboot-check.sh
waiting for the box to come back...
ssh answered after ~10s

=== uptime (proves this is post-reboot) ===
up 0 minutes
2026-08-13 22:34:04

=== waiting for the healthcheck to settle ===
  health=starting
  health=healthy

=== docker compose ps, with nothing started by hand ===
NAME                 IMAGE                       COMMAND                  SERVICE   STATUS
ing-listing-engine   ing-listing-engine:latest   "dotnet AutoListerB1…"   app       Up 9 seconds (healthy)

=== the site, from off the server ===
HTTP/2 200
/health              -> 200
/api/earnings/summary -> 401
http:// redirect      -> 308
```

The box booted at 22:34:04 and the container was running at 22:34:15 — eleven seconds, no ssh
session involved. `CREATED 25 minutes ago` against `Up 9 seconds` is the tell that Docker
restarted the existing container rather than anything recreating it.

```
docker       enabled / active
containerd   enabled / active
caddy        enabled / active
cron         enabled / active
restart=unless-stopped status=running health=healthy
log=json-file map[max-file:5 max-size:10m]
```

### A crash brings it back too

```
$ docker inspect ing-listing-engine --format 'status={{.State.Status}} restarts={{.RestartCount}}'
status=running restarts=0

$ kill -9 "$(docker inspect ing-listing-engine --format '{{.State.Pid}}')"      # host pid = 2519
$ docker inspect ing-listing-engine --format 'status={{.State.Status}} restarts={{.RestartCount}}'
status=running restarts=1

  health=healthy
https://app.inglisting.com/health -> 200
```

### The backup runs, keeps seven, and is readable

```
$ /usr/local/bin/ing-backup.sh
2026-08-13T22:30:38+00:00 ok /var/backups/ing-listing-engine/ing-listing-engine-2026-08-13.tar.gz
    (11979 bytes, integrity_check=ok, 1 kept)

$ tar tzvf /var/backups/ing-listing-engine/ing-listing-engine-2026-08-13.tar.gz
-rw-r--r-- root/root    229376 2026-08-13 22:30 ing_listing_engine.db
drwxr-xr-x 1654/1654         0 2026-08-13 21:14 auth-keys/
-rw------- 1654/1654      1001 2026-08-13 21:14 auth-keys/key-cef5b6b6-….xml
```

Fired by cron, not just by hand — proved by putting the same line on a one-minute schedule and
watching two nights' worth arrive in two minutes:

```
$ cat /var/log/ing-listing-engine-backup.log
2026-08-13T22:31:01+00:00 ok … (11978 bytes, integrity_check=ok, 1 kept)
2026-08-13T22:32:01+00:00 ok … (11979 bytes, integrity_check=ok, 1 kept)

$ journalctl -u cron | grep ing-backup
Aug 13 22:31:01 CRON[9207]: (root) CMD (/usr/local/bin/ing-backup.sh >> /var/log/…log 2>&1)
Aug 13 22:32:01 CRON[9289]: (root) CMD (/usr/local/bin/ing-backup.sh >> /var/log/…log 2>&1)
```

Retention, tested by faking nine days of history:

```
$ ls -1 /var/backups/ing-listing-engine | wc -l
9
$ /usr/local/bin/ing-backup.sh
2026-08-13T22:33:08+00:00 pruned 2 backup(s) beyond the newest 7
2026-08-13T22:33:08+00:00 ok … (integrity_check=ok, 7 kept)
```

And the archive is restorable, not merely present — unpacked and read:

```
$ sqlite3 "$T/ing_listing_engine.db" 'PRAGMA integrity_check;'
ok
$ sqlite3 "$T/ing_listing_engine.db" "SELECT name FROM sqlite_master WHERE type='table';"
ai_usage deals facebook_sold fee_profile flips listing_category_memory listing_cost_basis
local_listings onboarding radar_alerts radar_seen radar_settings radar_watches sqlite_sequence
terapeak_cache_stats terapeak_price_cache user_credentials users work_in_progress
$ sqlite3 "$T/ing_listing_engine.db" "SELECT 'users=' || COUNT(*) FROM Users;"
users=3
```

### `/health` answers without a session and says nothing

```
$ curl -s -o /dev/null -w '%{http_code}\n' https://app.inglisting.com/health     # 200
$ curl -s -I -o /dev/null -w '%{http_code}\n' https://app.inglisting.com/health  # 200  (HEAD too)
$ curl -s https://app.inglisting.com/health
{"status":"ok"}
```

No cookie, no `Authorization` header, and a body that is a constant — it reads nothing, touches no
secret and reaches no database, so it cannot leak one and cannot report the app down because a
dependency is slow. GET **and** HEAD, because uptime monitors send HEAD and a matched path with an
unmatched method selects a 405 endpoint that carries none of this one's `AllowAnonymous`.

---

## 15. Guessing a password does not work, as verified on 2026-08-14

Before this: **eight consecutive wrong passwords for one account returned 401 eight times, at full
speed, with nothing between the attacker and the ninth.** Run against the live site with throwaway
accounts, from off the server. See §8 for what the two limits are and why.

### One account, guessed at

```
$ bash deploy-bruteforce-check.sh
=== 0. Two throwaway accounts ===
sign up throwaway-victim-20260814134614@example.com    -> 200
sign up throwaway-neighbour-20260814134614@example.com -> 200

=== 1. Five wrong passwords: each one genuinely was wrong ===
  attempt 1  -> 401
  attempt 2  -> 401
  attempt 3  -> 401
  attempt 4  -> 401
  attempt 5  -> 401

=== 2. The sixth is not answered at all ===
  attempt 6  -> 429
  body: {"error":"Too many failed sign-in attempts. This account is locked for about
         another 15 minutes — try again then.","locked":true,
         "lockedUntil":"2026-08-14T14:01:16.7674102+00:00"}

=== 3. The RIGHT password, during the lock, is refused with it ===
throwaway-victim-…    with the right password -> 429

=== 4. The account next door signs in normally ===
throwaway-neighbour-… with the right password -> 200
```

### The countdown is real, and it lifts on its own

Asked again eleven minutes in, the same account, the same right password:

```
$ curl -s -X POST $H/api/auth/sign-in -d '{"email":"throwaway-victim-…","password":"<the right one>"}'
{"error":"Too many failed sign-in attempts. This account is locked for about another 4 minutes
 — try again then.","locked":true,"lockedUntil":"2026-08-14T14:01:16.7674102+00:00"}
```

and once the quarter of an hour is actually up — the lock expired at `14:01:16.767Z`, asked at
`14:01:37Z`, same account, same right password:

```
status: 200
body:   {"email":"throwaway-victim-20260814134614@example.com"}
```

Nobody is emailed, no row is cleared by hand and no support step exists. A seller who mistyped
five times waits a quarter of an hour and signs in — which is why the refusal says plainly that it
is a lockout and counts down rather than repeating "email and password do not match".

`lift` refuses to answer before the fifteen minutes are up, and prints PASS or FAIL rather than a
status code under an encouraging heading. The first version of this script printed
*"200 means the fifteen minutes are up"* underneath whatever it actually got, and a 429 read at a
glance as a pass.

### One host, spraying many accounts

Each address below is one nobody has ever failed on, so the account lockout cannot be what refuses
it. The budget ran out at the twentieth attempt of the five-minute window — nine here plus eleven
already spent by the run above:

```
fresh account 1..9  -> 401  {"error":"That email address and password do not match an account."}
fresh account 10    -> 429  {"error":"Too many sign-in attempts from this connection.
                                      Wait a few minutes and try again."}

HTTP/1.1 429 Too Many Requests
Retry-After: 300
```

### The counter is a row, not a process's memory

Read out of the live database mid-lockout — five failures and a lock on the account that was
guessed at, one failure and no lock on each sprayed address:

```
$ sqlite3 … "SELECT email_normalized,failures,locked_until FROM sign_in_failures;"
throwaway-victim-20260814134614@example.com|5|2026-08-14T14:01:16.7674102+00:00
spray-1786715194-1@example.com|1|
spray-1786715195-2@example.com|1|
…
```

A deploy or a reboot therefore does not hand an attacker five fresh attempts, which is the failure
mode of every in-memory counter. Proved directly — the container was restarted mid-lockout and
asked again with the right password:

```
bad password 1..5     -> 401
bad password 6        -> 429
$ docker compose restart
6th-again after restart, RIGHT password -> 429
```

Rows past the window are swept once per window, so the table cannot grow into a list of every
address anybody has ever guessed at. The sweep, watched on the live database:

```
rows BEFORE (a lockout, nine sprayed addresses, an expired lock)  12
  … one failed sign-in arrives more than 15 minutes after the last sweep …
rows AFTER                                                         1
```

Nothing that was still deciding anything was deleted: a row older than the window is already read
as a fresh start, and a lock still running is left alone.
