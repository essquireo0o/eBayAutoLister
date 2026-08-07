using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The rule this file exists to hold: only an answer that says something about <em>this seller's
/// account</em> may take the tick off step 2. eBay being down, and never having asked, must leave
/// a working connection looking exactly as it did.
/// </summary>
public class EbayLinkCheckTests
{
    private static ConnectionCheck Doctor(ConnectionState state, bool connected = false) =>
        new("eBay OAuth", connected, state, "reason", "next action");

    // ── The mapping ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConnectionState.Ok, EbayLinkCheck.Works)]
    [InlineData(ConnectionState.AuthRejected, EbayLinkCheck.Rejected)]
    [InlineData(ConnectionState.SessionExpired, EbayLinkCheck.Expired)]
    [InlineData(ConnectionState.NoSession, EbayLinkCheck.NoSession)]
    [InlineData(ConnectionState.NotConfigured, EbayLinkCheck.NotConfigured)]
    [InlineData(ConnectionState.Unreachable, EbayLinkCheck.Unreachable)]
    public void EveryStateTheDoctorCanReturnHasAVerdict(ConnectionState state, string expected)
    {
        Assert.Equal(expected, EbayLinkCheck.StateOf(state));
        Assert.Equal(expected, EbayLinkCheck.FromCheck(Doctor(state)).State);
    }

    [Fact]
    public void NoConnectionStateIsLeftUnmapped()
    {
        // A state added to the doctor and forgotten here would be filed under "eBay was
        // unreachable", which is the one bucket that changes nothing on the checklist.
        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            var mapped = EbayLinkCheck.StateOf(state);
            Assert.Contains(mapped, EbayLinkCheck.All);
            if (state != ConnectionState.Unreachable)
                Assert.NotEqual(EbayLinkCheck.Unreachable, mapped);
        }
    }

    [Fact]
    public void OnlyOkIsAWorkingConnection()
    {
        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            var verdict = EbayLinkCheck.FromCheck(Doctor(state));
            Assert.Equal(state == ConnectionState.Ok, verdict.Ok);
        }
    }

    // ── What is allowed to untick step 2 ─────────────────────────────────────

    [Theory]
    [InlineData(EbayLinkCheck.Rejected)]
    [InlineData(EbayLinkCheck.Expired)]
    [InlineData(EbayLinkCheck.NoSession)]
    [InlineData(EbayLinkCheck.NotConfigured)]
    public void TheFourFailuresTheSellerMustActOnAreDefinitive(string state)
    {
        Assert.True(EbayLinkCheck.IsDefinitive(state));
        Assert.True(EbayLinkCheck.Describe(state).Definitive);
        Assert.False(EbayLinkCheck.Describe(state).Ok);
    }

    [Theory]
    [InlineData(EbayLinkCheck.Unreachable)]
    [InlineData(EbayLinkCheck.Untested)]
    [InlineData(EbayLinkCheck.Works)]
    [InlineData("something eBay has never said")]
    public void NothingElseIsAllowedToUnticktheStep(string state)
    {
        Assert.False(EbayLinkCheck.IsDefinitive(state));
        Assert.False(EbayLinkCheck.Describe(state).Definitive);
    }

    [Fact]
    public void AnEbayOutageIsNotAVerdictOnTheAccount()
    {
        var verdict = EbayLinkCheck.FromCheck(Doctor(ConnectionState.Unreachable));

        Assert.False(verdict.Definitive);
        Assert.False(verdict.Ok);
        // And it has to say so: "couldn't reach eBay" read as "your sign-in is broken" sends a
        // seller through a re-login that fixes nothing.
        Assert.Contains("says nothing about your account", verdict.WhatToDo, StringComparison.OrdinalIgnoreCase);
    }

    // ── Reading a stored verdict back ────────────────────────────────────────

    [Fact]
    public void AStoredStateReadsBackAsTheSameVerdict()
    {
        var at = DateTimeOffset.UtcNow;

        foreach (var state in EbayLinkCheck.All)
        {
            var fresh = EbayLinkCheck.Describe(state, at);
            var readBack = EbayLinkCheck.Describe(EbayLinkCheck.Normalize(state), at);

            Assert.Equal(fresh.State, readBack.State);
            Assert.Equal(fresh.Headline, readBack.Headline);
            Assert.Equal(fresh.WhatToDo, readBack.WhatToDo);
            Assert.Equal(fresh.Definitive, readBack.Definitive);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("connected")]
    [InlineData("ok")]
    public void AnythingUnrecognisedIsUntestedRatherThanAGuess(string? stored)
    {
        Assert.Equal(EbayLinkCheck.Untested, EbayLinkCheck.Normalize(stored));
        Assert.Equal(EbayLinkCheck.Untested, EbayLinkCheck.Describe(stored).State);
    }

    [Fact]
    public void NormalizeAcceptsTheStoredSpellingWhateverCaseItComesBackIn()
    {
        Assert.Equal(EbayLinkCheck.Rejected, EbayLinkCheck.Normalize("REJECTED"));
        Assert.Equal(EbayLinkCheck.NoSession, EbayLinkCheck.Normalize(" No-Session "));
    }

    [Fact]
    public void EveryVerdictSaysWhatToDoNext()
    {
        foreach (var state in EbayLinkCheck.All)
        {
            var verdict = EbayLinkCheck.Describe(state);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Headline), $"{state} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(verdict.WhatToDo), $"{state} says nothing to do");
        }
    }

    [Fact]
    public void TheFourFailuresDoNotAllGiveTheSameAdvice()
    {
        // An expired sign-in and a consent screen missing the selling permissions are both fixed by
        // signing in again — but a seller told the same sentence for all four learns nothing about
        // which one happened, and "not configured" is not fixed by clicking Connect at all.
        var advice = new[] { EbayLinkCheck.Rejected, EbayLinkCheck.Expired, EbayLinkCheck.NoSession, EbayLinkCheck.NotConfigured }
            .Select(state => EbayLinkCheck.Describe(state).Headline)
            .ToArray();

        Assert.Equal(advice.Length, advice.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheUntestedVerdictCarriesNoDate()
    {
        // A date on "never asked" is a claim that something was established. Nothing was.
        Assert.Null(EbayLinkCheck.Describe(EbayLinkCheck.Untested, DateTimeOffset.UtcNow).CheckedAt);
        Assert.NotNull(EbayLinkCheck.Describe(EbayLinkCheck.Works, DateTimeOffset.UtcNow).CheckedAt);
    }

    [Fact]
    public void TheCheckStampsWhenItRan()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);

        Assert.Equal(at, EbayLinkCheck.FromCheck(Doctor(ConnectionState.Ok), at).CheckedAt);
    }
}
