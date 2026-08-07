using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A saved key and a working key are different claims, and the app used to make only the first one.
/// These pin the difference: which Anthropic answers mean "you have to fix this", which mean
/// "ask again later", and — the one that costs the seller if it is wrong — which are allowed to
/// take a tick off a step.
/// </summary>
public class AiKeyCheckTests
{
    private static FailureInfo Failure(string message) =>
        FailureTranslator.Translate(new InvalidOperationException(message), FailureDomain.Ai);

    // ── The three answers that are about the key ─────────────────────────────

    [Fact]
    public void ARefusedKeyIsSomethingTheSellerHasToFix()
    {
        // What Anthropic sends for a key with a character lost in the paste, or one since deleted.
        var verdict = AiKeyCheck.FromFailure(Failure(
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));

        Assert.Equal(AiKeyCheck.Rejected, verdict.State);
        Assert.False(verdict.Ok);
        Assert.True(verdict.Definitive);
        // And it points at the page that has the keys on it, not the billing page.
        Assert.Equal(AiKeyCheck.KeysUrl, verdict.Link);
    }

    [Fact]
    public void AnAccountWithNoCreditIsNotAKeyProblemAndDoesNotSayItIs()
    {
        // Much the most likely first-run failure: a brand-new Anthropic account with a valid key
        // and no money on it. Telling that seller to re-copy their key wastes their evening.
        var verdict = AiKeyCheck.FromFailure(Failure(
            """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API"}}"""));

        Assert.Equal(AiKeyCheck.NoCredit, verdict.State);
        Assert.True(verdict.Definitive);
        Assert.Equal(AiKeyCheck.BillingUrl, verdict.Link);
        Assert.Contains("key is fine", verdict.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoKeyAtAllIsItsOwnAnswer()
    {
        var verdict = AiKeyCheck.FromFailure(Failure("Anthropic API key is not configured. Open Settings to add it."));

        Assert.Equal(AiKeyCheck.Missing, verdict.State);
        Assert.True(verdict.Definitive);
    }

    // ── The answers that are about the moment, not the key ───────────────────

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"rate_limit_error","message":"too many requests"}}""")]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")]
    [InlineData("""{"type":"error","error":{"type":"api_error","message":"Internal server error"}}""")]
    public void AFailureThatProvesNothingAboutTheKeyChangesNothing(string message)
    {
        var verdict = AiKeyCheck.FromFailure(Failure(message));

        // A rate limit actually proves the key authenticated. None of these may be recorded, and
        // none of them may untick a step — that is what Definitive is for.
        Assert.Equal(AiKeyCheck.Unreachable, verdict.State);
        Assert.False(verdict.Definitive);
        Assert.False(AiKeyCheck.IsDefinitive(verdict.State));
    }

    [Fact]
    public void ATimeoutIsNotAVerdictOnTheKey()
    {
        var verdict = AiKeyCheck.FromFailure(
            FailureTranslator.Translate(new OperationCanceledException(), FailureDomain.Ai));

        Assert.Equal(AiKeyCheck.Unreachable, verdict.State);
        Assert.False(verdict.Definitive);
    }

    [Fact]
    public void AnUnrecognisedFailureIsTreatedAsUnknownRatherThanAsABadKey()
    {
        // The safe direction to be wrong in. Guessing "rejected" from an error nobody has seen
        // before would untick a working key and send the seller to re-paste it for nothing.
        var verdict = AiKeyCheck.FromFailure(Failure("something nobody has ever seen"));

        Assert.Equal(AiKeyCheck.Unreachable, verdict.State);
        Assert.False(verdict.Definitive);
    }

    // ── Success, and reading a stored state back ─────────────────────────────

    [Fact]
    public void AKeyThatAnsweredIsTheOnlyOkState()
    {
        var verdict = AiKeyCheck.Working();

        Assert.Equal(AiKeyCheck.Works, verdict.State);
        Assert.True(verdict.Ok);
        Assert.False(verdict.Definitive);
        Assert.NotNull(verdict.CheckedAt);
        // Nothing to go and do, so nowhere to send them.
        Assert.Equal("", verdict.Link);
    }

    [Fact]
    public void EveryStateSaysWhatToDoAboutIt()
    {
        foreach (var state in AiKeyCheck.All)
        {
            var verdict = AiKeyCheck.Describe(state, DateTimeOffset.UtcNow);
            Assert.Equal(state, verdict.State);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Headline), $"'{state}' has no headline");
            Assert.False(string.IsNullOrWhiteSpace(verdict.WhatToDo), $"'{state}' has nothing to do about it");
            // A link is either a real one or absent — never a label with nowhere to go.
            Assert.Equal(verdict.Link.Length > 0, verdict.LinkLabel.Length > 0);
        }
    }

    [Fact]
    public void AnythingUnrecognisedReadsBackAsUntested()
    {
        // What a database written by a later build, or a corrupted row, has to degrade to.
        foreach (var junk in new[] { null, "", "  ", "WORKS?", "expired" })
            Assert.Equal(AiKeyCheck.Untested, AiKeyCheck.Normalize(junk));

        Assert.False(AiKeyCheck.IsDefinitive("expired"));
        Assert.False(AiKeyCheck.Describe("expired").Ok);
    }

    [Fact]
    public void StateNamesAreCaseInsensitiveButKeepOneSpelling()
    {
        Assert.Equal(AiKeyCheck.NoCredit, AiKeyCheck.Normalize("NO-CREDIT"));
        Assert.Equal(AiKeyCheck.Works, AiKeyCheck.Normalize(" works "));
    }

    [Fact]
    public void OnlyTheThreeActionableStatesAreDefinitive()
    {
        Assert.True(AiKeyCheck.IsDefinitive(AiKeyCheck.Rejected));
        Assert.True(AiKeyCheck.IsDefinitive(AiKeyCheck.NoCredit));
        Assert.True(AiKeyCheck.IsDefinitive(AiKeyCheck.Missing));

        Assert.False(AiKeyCheck.IsDefinitive(AiKeyCheck.Works));
        Assert.False(AiKeyCheck.IsDefinitive(AiKeyCheck.Unreachable));
        Assert.False(AiKeyCheck.IsDefinitive(AiKeyCheck.Untested));
    }

    [Fact]
    public void DefinitiveOnAVerdictAndOnAStateAgree()
    {
        // Two ways of asking the same question, in two different files. They cannot disagree.
        foreach (var state in AiKeyCheck.All)
            Assert.Equal(AiKeyCheck.IsDefinitive(state), AiKeyCheck.Describe(state).Definitive);
    }
}
