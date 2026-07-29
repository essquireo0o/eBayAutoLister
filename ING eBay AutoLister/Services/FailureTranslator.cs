using System.Text.Json;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns any exception thrown on a critical path into a <see cref="FailureInfo"/> the seller can act
/// on: what happened, what to do, and whether retrying is worth their time.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the app's worst moments were being reported as its least informative ones.
/// Before this, an Anthropic 529 ("overloaded" — the single most common real failure, and fully
/// recoverable by waiting eight seconds) reached the seller as the literal text
/// <c>Anthropic.SDK.Messaging.MessageResponse ... 529</c>, and a missing API key produced the same
/// shaped red bar as a genuine outage. Both look like "the app is broken"; one is a 10-second wait
/// and the other is a 20-second settings edit.
/// </para>
/// <para>
/// Everything here is pure and string-driven, deliberately. The point is to survive an SDK or API
/// changing its exception types: matching on the message means an unrecognised error degrades to
/// <see cref="FailureKind.Unknown"/> with the raw text preserved, rather than a type-check silently
/// falling through to a generic 500.
/// </para>
/// </remarks>
public static class FailureTranslator
{
    // ── Public entry point ───────────────────────────────────────────────────

    public static FailureInfo Translate(Exception? ex, FailureDomain domain, int attempts = 1)
    {
        if (ex is AppFailureException already)
            return already.Failure with { Attempts = Math.Max(already.Failure.Attempts, attempts) };

        var inner = Unwrap(ex);
        var message = (inner?.Message ?? "").Trim();
        var technical = Truncate(message, 600);

        var basic = Classify(inner, message, domain);
        return basic with
        {
            Domain = domain,
            Technical = technical,
            Attempts = Math.Max(1, attempts),
            RetryAfterSeconds = basic.RetryAfterSeconds ?? RetryAfterFrom(message),
        };
    }

    /// <summary>
    /// Unwraps the layers that hide the message that actually explains the failure —
    /// <see cref="AggregateException"/> from a faulted task, and the inner
    /// <see cref="HttpRequestException"/> under a wrapping service exception.
    /// </summary>
    public static Exception? Unwrap(Exception? ex)
    {
        var current = ex;
        var guard = 0;
        while (guard++ < 10)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
                continue;
            }

            // Keep a wrapper whose own message is the informative one; only descend when the
            // wrapper says nothing the inner exception doesn't already say better.
            if (current?.InnerException is not null && string.IsNullOrWhiteSpace(current.Message))
            {
                current = current.InnerException;
                continue;
            }

            break;
        }
        return current;
    }

    public static bool IsTransient(FailureKind kind) => kind switch
    {
        FailureKind.Timeout => true,
        FailureKind.Network => true,
        FailureKind.RateLimited => true,
        FailureKind.Overloaded => true,
        FailureKind.UpstreamServerError => true,
        FailureKind.AiUnreadableReply => true,
        FailureKind.StorageBusy => true,
        FailureKind.BrowserBusy => true,
        _ => false,
    };

    // ── Classification ──────────────────────────────────────────────────────

    private static FailureInfo Classify(Exception? ex, string message, FailureDomain domain)
    {
        // Browser first, and before the type-driven cases: the scrapes report their own failures as
        // labelled strings on an ordinary exception, and a launch that could not find Chrome must not
        // be read as whatever the message happens to also contain.
        if (domain == FailureDomain.Browser)
            return ClassifyBrowser(ex, message);

        // Type-driven cases first: these are unambiguous regardless of wording, and a cancelled
        // request is the one failure whose message is routinely useless ("A task was canceled.").
        if (ex is OperationCanceledException or TimeoutException)
            return Timeout(domain);

        if (ex is SqliteException sqlite)
            return FromSqlite(sqlite, message);

        if (ex is JsonException && domain == FailureDomain.Ai)
            return UnreadableAiReply();

        if (ex is IOException io && MentionsAny(message, "not enough space", "disk full", "no space left"))
            return DiskFull(io.Message);

        if (ex is UnauthorizedAccessException)
            return new FailureInfo
            {
                Kind = FailureKind.BadInput,
                Headline = "Windows blocked a file the app needed",
                WhatHappened = "The app was not allowed to read or write one of its own files.",
                WhatToDo = "This is usually antivirus or a folder permission. Check the app folder is not "
                         + "quarantined, then try again. Your work is still on screen.",
                Retryable = true,
            };

        // A timeout that arrives as an HttpRequestException rather than a cancellation.
        if (ex is HttpRequestException && MentionsAny(message, "timed out", "timeout"))
            return Timeout(domain);

        return domain switch
        {
            FailureDomain.Ai => ClassifyAi(ex, message),
            FailureDomain.Ebay => ClassifyEbay(ex, message),
            FailureDomain.Photos => ClassifyPhotos(ex, message),
            FailureDomain.Research => ClassifyResearch(ex, message),
            FailureDomain.Storage => ClassifyStorage(ex, message),
            _ => Unknown(domain),
        };
    }

    // ── AI (Anthropic) ──────────────────────────────────────────────────────

    private static FailureInfo ClassifyAi(Exception? ex, string message)
    {
        if (MentionsAny(message, "api key is not configured", "api key is missing", "no api key"))
            return new FailureInfo
            {
                Kind = FailureKind.AiKeyMissing,
                Headline = "No AI key saved yet",
                WhatHappened = "Writing a listing needs an Anthropic API key, and none is saved on this machine.",
                WhatToDo = "Open Settings, paste your Anthropic key, and run this again. Nothing you have "
                         + "entered has been lost.",
                Retryable = false,
                FixAction = "ai-key",
            };

        if (MentionsAny(message, "authentication_error", "invalid x-api-key", "invalid api key", "invalid_api_key")
            || MentionsHttpStatus(message, 401))
            return new FailureInfo
            {
                Kind = FailureKind.AiKeyRejected,
                Headline = "Anthropic rejected the AI key",
                WhatHappened = "The saved Anthropic key was refused, so the request never reached the model.",
                WhatToDo = "Re-copy the key from the Anthropic console into Settings — keys are easy to "
                         + "truncate when pasting. Retrying with the same key will fail the same way.",
                Retryable = false,
                FixAction = "ai-key",
            };

        if (MentionsAny(message, "credit balance", "billing", "payment required", "insufficient_quota")
            || MentionsHttpStatus(message, 402))
            return new FailureInfo
            {
                Kind = FailureKind.AiBilling,
                Headline = "The Anthropic account is out of credit",
                WhatHappened = "Anthropic accepted the key but declined the request for billing reasons.",
                WhatToDo = "Top up the Anthropic account, then run this again. Everything on screen is kept.",
                Retryable = false,
                FixAction = "ai-key",
            };

        if (MentionsAny(message, "rate_limit", "rate limit", "too many requests") || MentionsHttpStatus(message, 429))
            return new FailureInfo
            {
                Kind = FailureKind.RateLimited,
                Headline = "The AI is rate limited right now",
                WhatHappened = "Too many requests went to Anthropic in a short window, so this one was queued out.",
                WhatToDo = "Wait a few seconds and try again — this clears on its own. Your photo and every "
                         + "field you filled in are still here.",
                Retryable = true,
            };

        if (MentionsAny(message, "overloaded") || MentionsHttpStatus(message, 529))
            return new FailureInfo
            {
                Kind = FailureKind.Overloaded,
                Headline = "Anthropic is busy",
                WhatHappened = "Anthropic is temporarily overloaded. This is on their side, not a problem with "
                             + "your listing.",
                WhatToDo = "Try again in a few seconds. Nothing was lost.",
                Retryable = true,
            };

        if (MentionsAny(message, "request too large", "request_too_large", "prompt is too long", "too many tokens")
            || MentionsHttpStatus(message, 413))
            return new FailureInfo
            {
                Kind = FailureKind.InputTooLarge,
                Headline = "That image or description is too big for one request",
                WhatHappened = "The request exceeded the size Anthropic accepts.",
                WhatToDo = "Use a smaller photo (a phone photo is usually plenty) or shorten the description, "
                         + "then run it again.",
                Retryable = false,
            };

        if (MentionsAny(message, "did not contain a json", "unexpected character", "invalid json", "is invalid json"))
            return UnreadableAiReply();

        if (MentionsAny(message, "api_error", "internal server", "bad gateway", "service unavailable", "gateway timeout")
            || MentionsHttpStatus(message, 500) || MentionsHttpStatus(message, 502)
            || MentionsHttpStatus(message, 503) || MentionsHttpStatus(message, 504))
            return new FailureInfo
            {
                Kind = FailureKind.UpstreamServerError,
                Headline = "Anthropic had a server error",
                WhatHappened = "Anthropic returned a server error, so the model never answered.",
                WhatToDo = "Try again — these usually clear within a minute. Your work is untouched.",
                Retryable = true,
            };

        // Anthropic looked at the request and refused it. Found by pointing a real /api/analyze call at
        // a file that was not an image: the SDK surfaces this as an HttpRequestException, so it fell
        // into the network branch below and was reported as "Could not reach Anthropic — check your
        // connection", then retried three times. Both wrong. Anthropic answered immediately and
        // correctly; it is the input that cannot be used, and no number of retries changes that.
        if (MentionsAny(message, "invalid_request_error") || MentionsHttpStatus(message, 400))
        {
            var detail = ApiErrorMessage(message);
            var aboutImage = MentionsAny(detail, "image");
            return new FailureInfo
            {
                Kind = FailureKind.BadInput,
                Headline = aboutImage ? "That image could not be read" : "Anthropic refused the request",
                WhatHappened = detail.Length > 0
                    ? "Anthropic rejected it: " + detail + "."
                    : "Anthropic rejected the request as malformed.",
                WhatToDo = aboutImage
                    ? "Use an ordinary JPEG or PNG — a phone photo or a screenshot is fine. Files that are not "
                    + "really images, or are corrupted in transit, cannot be analysed."
                    : "Change the input and try again. Sending it again unchanged will be refused the same way.",
                Retryable = false,
            };
        }

        // Only a genuine transport failure reaches here now: an API error body proves the request got
        // to Anthropic and came back with an answer.
        if (ex is HttpRequestException && !LooksLikeApiErrorBody(message))
            return NetworkDown("Anthropic");

        return Unknown(FailureDomain.Ai) with
        {
            Headline = "The AI request failed",
            WhatHappened = ApiErrorMessage(message) is { Length: > 0 } apiDetail
                ? "Anthropic returned an error: " + apiDetail + "."
                : "Anthropic returned something the app did not recognise.",
            WhatToDo = "Try again. If it keeps happening, open Logs and send the detail below — nothing you "
                     + "entered has been lost.",
            FixAction = "open-logs",
        };
    }

    /// <summary>True when the text is an API error payload rather than a transport-level message.</summary>
    private static bool LooksLikeApiErrorBody(string message) =>
        MentionsAny(message, "\"type\":\"error\"", "\"error\":{", "invalid_request_error", "authentication_error",
                    "rate_limit_error", "overloaded_error", "api_error", "permission_error", "not_found_error");

    /// <summary>
    /// Pulls the human sentence out of an API error body.
    /// </summary>
    /// <remarks>
    /// Anthropic's body is JSON —
    /// <c>{"type":"error","error":{"type":"invalid_request_error","message":"Could not process image"}}</c>.
    /// "Could not process image" is genuinely useful and worth putting in front of the seller; the JSON
    /// wrapper around it is not, and the whole payload is kept in <see cref="FailureInfo.Technical"/>
    /// regardless.
    /// </remarks>
    public static string ApiErrorMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var match = Regex.Match(message, @"""message""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
        if (!match.Success) return "";
        var text = match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", " ").Trim().TrimEnd('.');
        return text.Length > 200 ? text[..200] : text;
    }

    private static FailureInfo UnreadableAiReply() => new()
    {
        Kind = FailureKind.AiUnreadableReply,
        Headline = "The AI's answer came back unreadable",
        WhatHappened = "The model replied, but not as the structured listing the app needs — usually a reply "
                     + "cut short mid-sentence.",
        WhatToDo = "Run it again. A fresh attempt normally parses cleanly, and everything you filled in stays.",
        Retryable = true,
    };

    // ── eBay ────────────────────────────────────────────────────────────────

    private static FailureInfo ClassifyEbay(Exception? ex, string message)
    {
        if (MentionsAny(message, "seller policies not configured", "policy id"))
            return new FailureInfo
            {
                Kind = FailureKind.EbayPoliciesMissing,
                Headline = "eBay needs your business policies first",
                WhatHappened = "eBay will not accept a listing without a payment, shipping and returns policy, "
                             + "and at least one is not set.",
                WhatToDo = "Open Settings → eBay Seller Policies and pick one of each, then publish again. "
                         + "Your listing is still here, exactly as you left it.",
                Retryable = false,
                FixAction = "ebay-policies",
            };

        if (MentionsAny(message, "not connected", "no ebay token", "no user token", "connect ebay"))
            return new FailureInfo
            {
                Kind = FailureKind.EbayNotConnected,
                Headline = "eBay is not connected",
                WhatHappened = "This action needs your eBay account, and no account is linked yet.",
                WhatToDo = "Click Log into eBay, finish the sign-in, then come back and try again.",
                Retryable = false,
                FixAction = "connect-ebay",
            };

        if (MentionsAny(message, "invalid_grant", "token expired", "invalid access token", "iaf token",
                        "auth token is invalid", "expired token")
            || MentionsHttpStatus(message, 401))
            return new FailureInfo
            {
                Kind = FailureKind.EbayAuthExpired,
                Headline = "Your eBay sign-in expired",
                WhatHappened = "eBay refused the saved token. Sign-ins expire; this is routine and not a "
                             + "problem with your listing.",
                WhatToDo = "Log into eBay again, then repeat this action. Nothing was sent to eBay and nothing "
                         + "was lost.",
                Retryable = false,
                FixAction = "connect-ebay",
            };

        if (MentionsAny(message, "rate limit", "exceeded the number of calls", "call limit", "too many requests"))
            return new FailureInfo
            {
                Kind = FailureKind.RateLimited,
                Headline = "eBay is rate limiting this account",
                WhatHappened = "eBay caps how many API calls an account makes per day, and that cap is hit.",
                WhatToDo = "Wait and try again later — the cap resets. Save this as a draft in the meantime so "
                         + "nothing is lost.",
                Retryable = true,
            };

        if (MentionsAny(message, "internal error", "internal server", "service unavailable", "bad gateway",
                        "unknown error", "try again later")
            || MentionsHttpStatus(message, 500) || MentionsHttpStatus(message, 502) || MentionsHttpStatus(message, 503))
            return new FailureInfo
            {
                Kind = FailureKind.UpstreamServerError,
                Headline = "eBay had a server error",
                WhatHappened = "eBay returned a server-side error rather than rejecting the listing.",
                WhatToDo = "Try again shortly. Check Active Listings first — an eBay server error can arrive "
                         + "after the listing was already created.",
                Retryable = true,
            };

        if (ex is HttpRequestException)
            return NetworkDown("eBay");

        // Anything else from eBay is a considered rejection of this specific listing: eBay looked at
        // it and said no. Retrying identical content produces the identical refusal, so no Retry
        // button, and eBay's own words are the most useful thing that can be shown.
        if (!string.IsNullOrWhiteSpace(message))
            return new FailureInfo
            {
                Kind = FailureKind.EbayRejected,
                Headline = "eBay refused this listing",
                WhatHappened = EbaySentence(message),
                WhatToDo = "Fix what eBay named above and publish again. Retrying without changing anything "
                         + "will get the same answer. Your listing is still open and unsaved changes are kept.",
                Retryable = false,
            };

        return Unknown(FailureDomain.Ebay);
    }

    /// <summary>
    /// Presents eBay's rejection as a sentence, with the API scaffolding around it removed.
    /// </summary>
    /// <remarks>
    /// eBay wraps the useful sentence in call names and HTTP codes
    /// (<c>AddFixedPriceItem failed (HTTP 500): &lt;?xml ...</c>). Sellers stop reading at the first
    /// angle bracket, so the prefix comes off and the length is capped — the untouched original is
    /// still carried in <see cref="FailureInfo.Technical"/>.
    /// </remarks>
    public static string EbaySentence(string message)
    {
        var text = (message ?? "").Trim();

        // Multi-word call names included: the first version of this only matched a single word, so
        // "Inventory item failed (HTTP 400): {...}" kept its prefix, which meant the JSON-body check
        // below never fired and the seller was shown the raw payload. Caught live, against eBay.
        text = Regex.Replace(text, @"^[\w ]{1,40}failed( \(HTTP \d+\))?:\s*", "", RegexOptions.IgnoreCase);

        // eBay's rejection bodies carry the human sentence inside them. `longMessage` is the one
        // written for a person to read; `message` is the terse label; the Trading API uses XML
        // elements instead of JSON keys for the same two things.
        if (text.StartsWith('<') || text.StartsWith('{'))
        {
            var extracted = EbayErrorSentence(text);
            return extracted.Length > 0
                ? extracted
                : "eBay rejected the listing and returned a raw error document — the detail is below.";
        }

        return Truncate(text, 300);
    }

    /// <summary>Pulls eBay's own human-readable sentence out of a JSON or XML error body.</summary>
    private static string EbayErrorSentence(string body)
    {
        foreach (var pattern in new[]
        {
            @"""longMessage""\s*:\s*""((?:[^""\\]|\\.)*)""",
            @"<LongMessage>([^<]+)</LongMessage>",
            @"""message""\s*:\s*""((?:[^""\\]|\\.)*)""",
            @"<ShortMessage>([^<]+)</ShortMessage>",
        })
        {
            var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var text = match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", " ").Trim();
            if (text.Length > 0) return Truncate(text, 300);
        }
        return "";
    }

    // ── Browser scrapes (saved-session sites) ───────────────────────────────

    /// <summary>
    /// Labels the scrape scripts emit for the failures that happen before a page ever loads. The
    /// raw text underneath them differs by Playwright version and by Windows locale, so the script
    /// names what it saw and this side matches the name — with the raw phrasings kept below as a
    /// fallback for anything that reaches here unlabelled.
    /// </summary>
    public const string PlaywrightMissingLabel = "PLAYWRIGHT_MISSING";
    public const string ChromeMissingLabel     = "CHROME_MISSING";
    public const string ChromeBusyLabel        = "CHROME_BUSY";
    public const string SessionExpiredLabel    = "SESSION_EXPIRED";
    public const string BrowserLaunchFailedLabel = "BROWSER_LAUNCH_FAILED";

    /// <summary>
    /// True when a scrape reported that the browser never started, as opposed to loading a page and
    /// finding nothing on it. The distinction is the whole point: "no Chrome on this machine" and
    /// "nothing for sale near you" are the same empty result set and completely different problems.
    /// </summary>
    public static bool IsBrowserLaunchFailure(string? message) =>
        MentionsAny(message ?? "", PlaywrightMissingLabel, ChromeMissingLabel, ChromeBusyLabel,
                    BrowserLaunchFailedLabel);

    private static FailureInfo ClassifyBrowser(Exception? ex, string message)
    {
        if (ex is OperationCanceledException or TimeoutException)
            return Timeout(FailureDomain.Browser);

        // The Playwright package, not the browser. Different fix entirely: one is an npm install,
        // the other is downloading Chrome. Reported as the same "browser problem" before this, which
        // sent people to google.com/chrome to fix a missing node module.
        if (MentionsAny(message, PlaywrightMissingLabel, "cannot find module", "module not found",
                        "playwright is not installed"))
            return new FailureInfo
            {
                Kind = FailureKind.ToolMissing,
                Headline = "The browser automation package isn't installed",
                WhatHappened = "Reading Marketplace drives a real browser through Playwright, and that package "
                             + "isn't on this machine.",
                WhatToDo = "Install Node.js, then run \"npm install -g playwright\" in a terminal and restart "
                         + "the app. Everything else in the app keeps working without it.",
                Retryable = false,
                FixAction = "open-logs",
            };

        if (MentionsAny(message, ChromeMissingLabel, "chromium distribution", "is not found at",
                        "browsertype.launch: executable doesn't exist", "channel \"chrome\"", "channel 'chrome'"))
            return new FailureInfo
            {
                Kind = FailureKind.ToolMissing,
                Headline = "Google Chrome isn't installed on this machine",
                WhatHappened = "The Marketplace scrape uses your real installed Chrome on purpose — Facebook "
                             + "challenges a test browser far more aggressively — and no Chrome was found.",
                WhatToDo = "Install Google Chrome, then try again. Nothing else in the app needs it.",
                Retryable = false,
                FixAction = "open-logs",
            };

        // Chrome is there and refused to start a second copy against the same profile. This is the
        // one browser failure that fixes itself, so it is the one that gets a Retry button.
        if (MentionsAny(message, ChromeBusyLabel, "processsingleton", "singletonlock",
                        "user data directory is already in use", "profile appears to be in use",
                        "target page, context or browser has been closed", "already running"))
            return new FailureInfo
            {
                Kind = FailureKind.BrowserBusy,
                Headline = "Chrome is already running and wouldn't start a second copy",
                WhatHappened = "Chrome refused to open with the profile this app needs, because a Chrome window "
                             + "is already using it.",
                WhatToDo = "Close your Chrome windows — all of them, including any left in the system tray — "
                         + "then try again. Nothing was lost.",
                Retryable = true,
            };

        if (MentionsAny(message, SessionExpiredLabel, "bounced to", "asked to sign in", "checkpoint",
                        "two-factor", "log in again"))
            return new FailureInfo
            {
                Kind = FailureKind.SessionExpired,
                Headline = "The saved Facebook session isn't accepted any more",
                WhatHappened = "Facebook served a login page instead of Marketplace, so the saved sign-in has "
                             + "expired or been challenged.",
                WhatToDo = "Click Reconnect and sign into your own Facebook account again — sessions expire on "
                         + "a password change or a security check, and only you can clear one.",
                Retryable = false,
                FixAction = "connect-facebook",
            };

        if (MentionsAny(message, "watchdog", "timed out", "timeout", "exceeded"))
            return Timeout(FailureDomain.Browser);

        return Unknown(FailureDomain.Browser) with
        {
            Headline = "The browser search couldn't run",
            WhatHappened = "The headless browser this search needs didn't get as far as loading the page.",
            WhatToDo = "Try again. If it keeps happening, open Logs and send the detail below — nothing on your "
                     + "listings was touched.",
            Retryable = true,
            FixAction = "open-logs",
        };
    }

    // ── Photos ──────────────────────────────────────────────────────────────

    private static FailureInfo ClassifyPhotos(Exception? ex, string message)
    {
        if (ex is FormatException || MentionsAny(message, "not a valid base-64", "invalid base-64", "base64"))
            return new FailureInfo
            {
                Kind = FailureKind.BadInput,
                Headline = "That image did not arrive intact",
                WhatHappened = "The image data was incomplete or corrupted on the way to the app, so it could "
                             + "not be saved.",
                WhatToDo = "Drop the photo in again, or use Browse instead of paste. The rest of the listing is "
                         + "untouched.",
                Retryable = true,
            };

        if (MentionsAny(message, "no such file", "could not find file", "file not found", "directory not found"))
            return new FailureInfo
            {
                Kind = FailureKind.NotFound,
                Headline = "That photo is no longer on disk",
                WhatHappened = "The image file the app expected has been moved, renamed or deleted.",
                WhatToDo = "Add the photo again from its current location.",
                Retryable = false,
            };

        if (MentionsAny(message, "not enough space", "disk full", "no space left"))
            return DiskFull(message);

        if (MentionsAny(message, "python", "rembg", "is not recognized", "cannot find the file specified",
                        "no module named"))
            return new FailureInfo
            {
                Kind = FailureKind.ToolMissing,
                Headline = "Background removal is not installed",
                WhatHappened = "Cutting the background out uses Python with rembg, and it is not available on "
                             + "this machine.",
                WhatToDo = "Your photo is saved and usable exactly as it is — background removal is optional "
                         + "and the listing can be published without it.",
                Retryable = false,
            };

        if (ex is HttpRequestException || MentionsAny(message, "no such host", "connection refused"))
            return NetworkDown("that image host") with
            {
                WhatToDo = "Check the link, or save the picture and drop it into the photo box instead. Every "
                         + "photo already added is kept.",
            };

        if (MentionsHttpStatus(message, 403) || MentionsHttpStatus(message, 404))
            return new FailureInfo
            {
                Kind = FailureKind.NotFound,
                Headline = "That image link would not load",
                WhatHappened = "The site refused to serve the image to anything but a browser, or the link is dead.",
                WhatToDo = "Right-click the picture in your browser, copy it, and paste it into the photo box "
                         + "instead.",
                Retryable = false,
            };

        return Unknown(FailureDomain.Photos) with
        {
            Headline = "That photo could not be added",
            WhatHappened = "The image could not be saved.",
            WhatToDo = "Try adding it again. Every other photo and field is untouched.",
        };
    }

    // ── Research / pricing ──────────────────────────────────────────────────

    private static FailureInfo ClassifyResearch(Exception? ex, string message)
    {
        if (ex is HttpRequestException || MentionsAny(message, "no such host", "connection refused",
                                                     "connection reset", "ssl", "tls"))
            return NetworkDown("the sold-price data") with
            {
                WhatToDo = "Check your internet connection and try again. Pricing falls back to whatever data "
                         + "is already cached, so the listing is still usable.",
            };

        if (MentionsAny(message, "session expired", "not connected", "log in"))
            return new FailureInfo
            {
                Kind = FailureKind.EbayNotConnected,
                Headline = "The research session needs reconnecting",
                WhatHappened = "Sold-price research runs through your own logged-in session, and that session "
                             + "has expired.",
                WhatToDo = "Reconnect it in Settings. Until then the app prices from its own comps database and "
                         + "says when data is thin rather than guessing.",
                Retryable = false,
            };

        if (MentionsHttpStatus(message, 429) || MentionsAny(message, "rate limit", "too many requests"))
            return new FailureInfo
            {
                Kind = FailureKind.RateLimited,
                Headline = "The price source is rate limiting us",
                WhatHappened = "Too many lookups went out too quickly.",
                WhatToDo = "Wait a moment and research again. The price already on the listing is unchanged.",
                Retryable = true,
            };

        return Unknown(FailureDomain.Research) with
        {
            Headline = "Sold-price research did not complete",
            WhatHappened = "The comparable-sales lookup failed, so no price recommendation was produced.",
            WhatToDo = "Try again. Nothing on the listing was changed — no price was applied.",
        };
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    private static FailureInfo ClassifyStorage(Exception? ex, string message)
    {
        if (MentionsAny(message, "database is locked", "busy"))
            return StorageBusy();
        if (MentionsAny(message, "not enough space", "disk full", "no space left"))
            return DiskFull(message);
        return Unknown(FailureDomain.Storage) with
        {
            Headline = "The app could not save to its database",
            WhatHappened = "Writing to the app's local database failed.",
            WhatToDo = "Try again. If it persists, restart the app — your listings on eBay are unaffected.",
            Retryable = true,
        };
    }

    private static FailureInfo FromSqlite(SqliteException ex, string message)
    {
        if (MentionsAny(message, "database is locked", "busy") || ex.SqliteErrorCode is 5 or 6)
            return StorageBusy();
        if (MentionsAny(message, "disk is full", "not enough space", "no space left"))
            return DiskFull(message);
        return new FailureInfo
        {
            Kind = FailureKind.Unknown,
            Headline = "The app's database rejected a write",
            WhatHappened = "The local database returned an error while saving.",
            WhatToDo = "Try again. Nothing on eBay was affected.",
            Retryable = true,
            FixAction = "open-logs",
        };
    }

    // ── Shared shapes ───────────────────────────────────────────────────────

    private static FailureInfo Timeout(FailureDomain domain) => new()
    {
        Kind = FailureKind.Timeout,
        Headline = "That took too long and was stopped",
        WhatHappened = domain == FailureDomain.Ebay
            ? "eBay did not answer in time. Because the request may still have gone through, the app checks "
            + "your live listings before offering to send it again."
            : "No answer came back in time, so the request was cancelled rather than left hanging.",
        WhatToDo = domain == FailureDomain.Ebay
            ? "Use Check eBay below rather than publishing again — publishing twice can create two live "
            + "listings for one item."
            : "Try again. Everything you filled in is still here.",
        Retryable = domain != FailureDomain.Ebay,
    };

    private static FailureInfo NetworkDown(string what) => new()
    {
        Kind = FailureKind.Network,
        Headline = "Could not reach " + what,
        WhatHappened = "The connection failed before a reply arrived — usually the internet dropping out or a "
                     + "firewall in the way.",
        WhatToDo = "Check your connection and try again. Nothing was sent and nothing was lost.",
        Retryable = true,
    };

    private static FailureInfo StorageBusy() => new()
    {
        Kind = FailureKind.StorageBusy,
        Headline = "The app's database was busy",
        WhatHappened = "Another part of the app was writing at the same moment, so this save was refused.",
        WhatToDo = "It retries on its own; if you see this, try once more.",
        Retryable = true,
    };

    private static FailureInfo DiskFull(string message) => new()
    {
        Kind = FailureKind.DiskFull,
        Headline = "This drive is out of space",
        WhatHappened = "There was not enough free disk space to write the file.",
        WhatToDo = "Free up some space, then try again. Nothing already saved has been damaged.",
        Retryable = false,
        Technical = Truncate(message, 300),
    };

    private static FailureInfo Unknown(FailureDomain domain) => new()
    {
        Kind = FailureKind.Unknown,
        Domain = domain,
        Headline = "Something went wrong",
        WhatHappened = "The app hit an error it does not have a specific explanation for.",
        WhatToDo = "Try again. If it keeps happening, open Logs and send the detail below.",
        Retryable = false,
        FixAction = "open-logs",
    };

    // ── Matching helpers ────────────────────────────────────────────────────

    public static bool MentionsAny(string message, params string[] needles)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var needle in needles)
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True when the message refers to an HTTP status code, as opposed to merely containing those
    /// three digits.
    /// </summary>
    /// <remarks>
    /// A bare <c>Contains("429")</c> is a real defect waiting to happen: eBay rejections quote
    /// prices, and "Item price 429.00 exceeds..." would be reported to the seller as a rate limit.
    /// The number therefore has to sit next to something that makes it a status — a label before it,
    /// or a status phrase after it.
    /// </remarks>
    public static bool MentionsHttpStatus(string message, int code)
    {
        if (string.IsNullOrEmpty(message)) return false;
        var pattern = $@"(?:http|https|status|statuscode|status code|code|error|responded|returned)\W{{0,12}}{code}\b"
                    + $@"|\({code}\)"
                    + $@"|(?:^|\s){code}\s*(?:too many|rate|overloaded|unauthorized|forbidden|not found|"
                    + @"internal server|bad gateway|service unavailable|gateway timeout|payment required|"
                    + @"request entity|request too large)";
        return Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase);
    }

    /// <summary>Reads a server-supplied wait, so a retry honours it rather than guessing.</summary>
    public static int? RetryAfterFrom(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = Regex.Match(message, @"retry[\s\-_]?after\D{0,6}(\d{1,4})", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return int.TryParse(match.Groups[1].Value, out var seconds) && seconds is > 0 and <= 3600
            ? seconds
            : null;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max] + "…";
}
