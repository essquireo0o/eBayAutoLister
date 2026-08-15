# Builds the server's /etc/ing-listing-engine/.env from the real local credential stores and
# writes it to a temp file with LF line endings, ready to be piped to the server.
#
# Nothing here prints a secret. The two keys that do not exist anywhere yet — the token-encryption
# key and the owner dashboard key — are generated once, here, and recorded in web-credentials.json
# so a later session can find them rather than rotating them by accident. Rotating
# CREDENTIALS_ENCRYPTION_KEY makes every stored eBay connection unreadable (HOSTING.md section 5).

$ErrorActionPreference = 'Stop'

$webCredPath = "C:\Users\nsquires\.claude\projects\C--Users-nsquires-source-repos-ING-eBay-AutoLister\web-credentials.json"
$appCredPath = "C:\Users\nsquires\source\repos\ING eBay AutoLister\ING eBay AutoLister\credentials.json"

$web = Get-Content $webCredPath -Raw | ConvertFrom-Json
$app = Get-Content $appCredPath -Raw | ConvertFrom-Json

# The Anthropic key is the OWNER'S key: on the hosted trial it pays for every user's generations,
# which is why the per-user daily cap below is not optional.
$anthropic = [string]$app.AnthropicApiKey
if ([string]::IsNullOrWhiteSpace($anthropic)) { throw "No AnthropicApiKey in $appCredPath" }

$compsUrl = [string]$web.hosted_comps_api.endpoint
$compsKey = [string]$web.hosted_comps_api.api_key

# The live sold-comps API. This is the key that makes live lookups work on the hosted app at all —
# the browser scraper it replaced needed Chrome and a person to clear eBay's bot check, so a server
# could never run one. It is PAID, FINITE and does not refill (50,000 calls, shared with
# BitData/owninja_collect.py), which is why LiveComps__DailyLookupLimit below is not optional.
# web-credentials.json is the source of truth; the desktop credentials.json is the fallback so a
# machine that only has it there still deploys.
$owninjaKey = [string]$web.openwebninja.api_key
if ([string]::IsNullOrWhiteSpace($owninjaKey)) { $owninjaKey = [string]$app.OpenWebNinjaApiKey }
if ([string]::IsNullOrWhiteSpace($owninjaKey)) {
    throw "No openwebninja.api_key in $webCredPath and no OpenWebNinjaApiKey in $appCredPath — the hosted app would have no live sold-comps lookups."
}

# Sign-up notifications go to the owner. Sent over Gmail SMTP with the app password already stored
# for this account. Gmail requires From to be the authenticated user, so the notice comes FROM the
# gmail login and goes TO the owner's real address; the new user's address rides in Reply-To.
# Spaces are stripped because Google displays app passwords in groups of four and SMTP wants none.
$gmailUser = [string]$web.gmail.login
$gmailPass = ([string]$web.gmail.app_password) -replace '\s', ''
$notifyTo  = 'ns@ingmining.com'
if ([string]::IsNullOrWhiteSpace($gmailUser)) { throw "No gmail.login in $webCredPath" }
if ([string]::IsNullOrWhiteSpace($gmailPass)) { throw "No gmail.app_password in $webCredPath" }

# The eBay APPLICATION's registration — the Client ID, Secret and RuName that name this software
# on eBay's consent screen. Shared by every hosted user on purpose: a seller signing up has no eBay
# developer account and cannot be asked for one. What is NOT here, and must never be, is any eBay
# token: those are each seller's own grant, they arrive from their browser, and they are written to
# their row encrypted. HostedEbayCredentialsTests fails if the server can read one.
$ebayClientId     = [string]$app.EbayClientId
$ebayClientSecret = [string]$app.EbayClientSecret
$ebayDevId        = [string]$app.EbayDevId
if ([string]::IsNullOrWhiteSpace($ebayClientId))     { throw "No EbayClientId in $appCredPath" }
if ([string]::IsNullOrWhiteSpace($ebayClientSecret)) { throw "No EbayClientSecret in $appCredPath" }

# The RuName is the one value the hosted deployment cannot inherit from the desktop app. The
# desktop RuName's accepted URL is the relay on inglisting.com, which redirects to localhost:9332 —
# a dead end for anyone on app.inglisting.com. A RuName has ONE accepted URL, so repointing it
# would break every installed desktop copy. The owner registers a SECOND one against
# https://app.inglisting.com/api/ebay/callback and it is recorded here. See EBAY-SETUP.md.
if ($web.PSObject.Properties.Name -contains 'hosted_app' -and
    -not [string]::IsNullOrWhiteSpace([string]$web.hosted_app.ebay_runame)) {
    $ebayRuName      = [string]$web.hosted_app.ebay_runame
    $ebayRuNameOwner = 'hosted (registered to https://app.inglisting.com/api/ebay/callback)'
} else {
    $ebayRuName      = [string]$app.EbayRuName
    $ebayRuNameOwner = 'DESKTOP RELAY — returns to localhost:9332. EBAY-SETUP.md is not done yet.'
}

function New-B64Key { $b = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($b); [Convert]::ToBase64String($b) }
function New-HexKey { $b = [byte[]]::new(16); [System.Security.Cryptography.RandomNumberGenerator]::Fill($b); ([BitConverter]::ToString($b) -replace '-','').ToLower() }

# Reuse the keys if this script has already run, so re-deploying does not sign everybody out or
# make every stored eBay token unreadable.
if ($web.PSObject.Properties.Name -contains 'hosted_app') {
    $encKey   = [string]$web.hosted_app.credentials_encryption_key
    $adminKey = [string]$web.hosted_app.admin_key
    $reused   = $true
    if ([string]::IsNullOrWhiteSpace($encKey)) { throw "hosted_app exists but has no credentials_encryption_key" }
    # The provisioning step wrote the encryption key but not an owner-dashboard key. Generate that
    # one now and keep it, so /owner?k= does not change its key on every restart.
    if ([string]::IsNullOrWhiteSpace($adminKey)) {
        $adminKey = New-HexKey
        $web.hosted_app | Add-Member -NotePropertyName admin_key -NotePropertyValue $adminKey -Force
        $web.hosted_app | Add-Member -NotePropertyName admin_key_note -NotePropertyValue "Opens the owner dashboard at https://app.inglisting.com/owner?k=. Generated 2026-08-13; without it the app makes a new one in memory on every restart." -Force
        $web | ConvertTo-Json -Depth 10 | Set-Content -Path $webCredPath -Encoding utf8NoBOM
    }
} else {
    $encKey   = New-B64Key
    $adminKey = New-HexKey
    $reused   = $false
    $web | Add-Member -NotePropertyName hosted_app -NotePropertyValue ([PSCustomObject]@{
        note = "Secrets for the HOSTED deployment at https://app.inglisting.com (Hetzner 178.156.154.41). These live on the server in /etc/ing-listing-engine/.env, root-owned mode 600. Generated 2026-08-13 by the deploy task. Rotating credentials_encryption_key makes every user's stored eBay refresh token unreadable and forces every user to reconnect eBay - see HOSTING.md section 5. admin_key opens the owner dashboard at /owner?k=."
        credentials_encryption_key = $encKey
        admin_key = $adminKey
        anthropic_key_source = "The owner's key, copied from 'ING eBay AutoLister/credentials.json' (AnthropicApiKey). Not duplicated here - that file is the source of truth."
        server = "root@178.156.154.41, ssh -i ~/.ssh/ing_hetzner"
    })
    $web | ConvertTo-Json -Depth 10 | Set-Content -Path $webCredPath -Encoding utf8NoBOM
}

$lines = @(
    "# ING Listing Engine - hosted deployment secrets.",
    "# Root-owned, mode 600, referenced by /opt/ing-listing-engine/docker-compose.yml (env_file:).",
    "# Never copy this file into the repository, the image, or a log line. See HOSTING.md section 5.",
    "",
    "# The AES-256-GCM key every user's stored eBay refresh token is encrypted with.",
    "# The app refuses to start without it, on purpose.",
    "CREDENTIALS_ENCRYPTION_KEY=$encKey",
    "",
    "# The owner's AI key. It pays for every user's generations, which is what the cap below is for.",
    "ANTHROPIC_API_KEY=$anthropic",
    "Ai__DailyGenerationLimit=5",
    "",
    "# The database path. Everything persisted lands under `$XDG_DATA_HOME/ING AutoLister/, so the",
    "# SQLite database is /data/ING AutoLister/App_Data/ing_listing_engine.db on the mounted volume.",
    "XDG_DATA_HOME=/data",
    "HOME=/data",
    "",
    "# The owner dashboard at /owner?k=. Set explicitly so it survives a restart.",
    "Credentials__AdminKey=$adminKey",
    "",
    "# The hosted sold-comps API, so pricing has real comparables.",
    "Credentials__MarketCompsApiUrl=$compsUrl",
    "Credentials__MarketCompsApiKey=$compsKey",
    "",
    "# Live sold-comps lookups. 50,000 PAID calls that do not refill, shared with the bulk",
    "# collector, so the per-account daily cap is the thing standing between a loop in somebody's",
    "# browser and the whole budget. Set LiveComps__Enabled=false to turn live lookups off",
    "# entirely and fall back to stored comps.",
    "Credentials__OpenWebNinjaApiKey=$owninjaKey",
    "LiveComps__Enabled=true",
    # Raised from 10 to 100 (2026-08-14): the Opportunity board now auto-verifies its top 3 rows
    # with live comps on every scan and offers a per-row 'get real sold price' button, so 10/day was
    # ~3 searches before the cap. 100/account still leaves the 50,000 pool years of headroom at
    # today's user count; revisit if signups climb. See the reprice-row endpoint + board auto-verify.
    "LiveComps__DailyLookupLimit=100",
    "",
    "# The eBay APPLICATION this deployment signs sellers in as. Shared, because a seller signing",
    "# up has no eBay developer account. Their OAuth tokens are NOT here and never will be — those",
    "# go in each user's encrypted row. See EBAY-SETUP.md.",
    "Credentials__EbayClientId=$ebayClientId",
    "Credentials__EbayClientSecret=$ebayClientSecret",
    "Credentials__EbayDevId=$ebayDevId",
    "Credentials__EbayRuName=$ebayRuName",
    "",
    "# The URL eBay must be registered to return to. Not sent to eBay — the RuName is — but it is",
    "# what the app reports and what EBAY-SETUP.md tells the owner to register.",
    "Ebay__RedirectUri=https://app.inglisting.com/api/ebay/callback",
    "",
    "# Sign-up notifications: email the owner every time someone creates an account. Sent via Gmail",
    "# SMTP with an app password. Remove any of these and SignupNotifier silently stops sending.",
    "Notify__Smtp__Host=smtp.gmail.com",
    "Notify__Smtp__Port=587",
    "Notify__Smtp__User=$gmailUser",
    "Notify__Smtp__Password=$gmailPass",
    "Notify__Smtp__From=$gmailUser",
    "Notify__Smtp__To=$notifyTo",
    ""
)

$tmp = Join-Path $env:TEMP "ing-listing-engine.env"
[System.IO.File]::WriteAllText($tmp, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding($false)))

"wrote $tmp"
"keys reused from web-credentials.json: $reused"
"anthropic key length: $($anthropic.Length)"
"comps url set: $(-not [string]::IsNullOrWhiteSpace($compsUrl)); comps key set: $(-not [string]::IsNullOrWhiteSpace($compsKey))"
"live comps key length: $($owninjaKey.Length); per-account daily cap: 10"
"encryption key length: $($encKey.Length); admin key length: $($adminKey.Length)"
"ebay client id length: $($ebayClientId.Length); secret length: $($ebayClientSecret.Length); dev id length: $($ebayDevId.Length)"
"ebay runame length: $($ebayRuName.Length) - $ebayRuNameOwner"
"signup notify: from $gmailUser -> $notifyTo (app-password length $($gmailPass.Length))"
