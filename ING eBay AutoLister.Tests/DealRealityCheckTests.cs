using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Claude "reality check" behind POST /api/opportunities/analyze-deal. The eBay-scanner board
/// prices rows with a mechanical comp-matcher that cannot tell a component from a whole unit, so a
/// "S19 XP hashboard" gets whole-miner comps and a fantasy ROI. <see cref="ClaudeService.CheckDealAsync"/>
/// asks the model to read the listing the way a reseller does; these pin the two halves that make
/// that safe — the reply is parsed into a <see cref="DealRealityCheck"/> the UI can gate on, and the
/// call is metered against the AI quota exactly like every other AI path.
/// </summary>
public class DealRealityCheckTests
{
    // The canonical failure this feature exists to catch: a component priced off whole-unit comps.
    private const string HashboardJson = """
        {
          "whatItIs": "a single S19 XP hashboard (a mining component, not a whole miner)",
          "itemType": "component_or_part",
          "compsMatchItem": false,
          "realisticResaleLow": 150,
          "realisticResaleHigh": 300,
          "verdict": "false_positive",
          "reason": "The estimate used whole-miner comps; a hashboard alone resells for ~$150-300."
        }
        """;

    // ── Parsing a real reply ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsEveryFieldOfARejectionVerdict()
    {
        var check = ClaudeService.ParseDealRealityCheck(HashboardJson);

        Assert.Equal("a single S19 XP hashboard (a mining component, not a whole miner)", check.WhatItIs);
        Assert.Equal("component_or_part", check.ItemType);
        Assert.False(check.CompsMatchItem);
        Assert.Equal(150m, check.RealisticResaleLow);
        Assert.Equal(300m, check.RealisticResaleHigh);
        Assert.Equal("false_positive", check.Verdict);
        Assert.Equal("The estimate used whole-miner comps; a hashboard alone resells for ~$150-300.", check.Reason);
    }

    [Fact]
    public void Parse_ReadsARealDealVerdict()
    {
        var check = ClaudeService.ParseDealRealityCheck("""
            {
              "whatItIs": "a whole Bitmain Antminer S19j Pro 104TH miner",
              "itemType": "whole_unit",
              "compsMatchItem": true,
              "realisticResaleLow": 380,
              "realisticResaleHigh": 460,
              "verdict": "real_deal",
              "reason": "Whole-miner comps match this whole miner; the estimate looks sound."
            }
            """);

        Assert.Equal("whole_unit", check.ItemType);
        Assert.True(check.CompsMatchItem);
        Assert.Equal("real_deal", check.Verdict);
    }

    [Fact]
    public void Parse_SurvivesCodeFencesAndSurroundingProse()
    {
        // The extractor pulls the object out of markdown fences and any stray commentary, the same
        // way every other ClaudeService parse does.
        var check = ClaudeService.ParseDealRealityCheck(
            "Here is the analysis:\n```json\n" + HashboardJson + "\n```\nHope that helps.");

        Assert.Equal("component_or_part", check.ItemType);
        Assert.Equal("false_positive", check.Verdict);
    }

    // ── Defensive normalisation — the two fields the UI gates on ──────────────

    [Fact]
    public void Parse_UnknownVerdictBecomesUncertainRatherThanFlaggingTheRow()
    {
        // An unrecognised verdict must not be read as a rejection — "uncertain" never drops a row the
        // model didn't mean to reject.
        var check = ClaudeService.ParseDealRealityCheck("""
            {"whatItIs":"a thing","itemType":"whole_unit","compsMatchItem":true,
             "realisticResaleLow":10,"realisticResaleHigh":20,"verdict":"MAYBE_OK","reason":"x"}
            """);

        Assert.Equal("uncertain", check.Verdict);
    }

    [Fact]
    public void Parse_UnknownItemTypeBecomesOther()
    {
        var check = ClaudeService.ParseDealRealityCheck("""
            {"whatItIs":"a thing","itemType":"gizmo","compsMatchItem":true,
             "realisticResaleLow":10,"realisticResaleHigh":20,"verdict":"real_deal","reason":"x"}
            """);

        Assert.Equal("other", check.ItemType);
    }

    [Fact]
    public void Parse_MixedCaseEnumsAreNormalisedToLowercase()
    {
        var check = ClaudeService.ParseDealRealityCheck("""
            {"whatItIs":"a thing","itemType":"Component_Or_Part","compsMatchItem":false,
             "realisticResaleLow":10,"realisticResaleHigh":20,"verdict":"FALSE_POSITIVE","reason":"x"}
            """);

        Assert.Equal("component_or_part", check.ItemType);
        Assert.Equal("false_positive", check.Verdict);
    }

    [Fact]
    public void Parse_SquaresUpAnInvertedOrNegativeResaleRange()
    {
        // A high below the low, or a negative floor, is a model slip — the row must never draw
        // "$300–$150" or a negative number.
        var check = ClaudeService.ParseDealRealityCheck("""
            {"whatItIs":"a thing","itemType":"whole_unit","compsMatchItem":true,
             "realisticResaleLow":-5,"realisticResaleHigh":-10,"verdict":"real_deal","reason":"x"}
            """);

        Assert.Equal(0m, check.RealisticResaleLow);
        Assert.True(check.RealisticResaleHigh >= check.RealisticResaleLow);
    }

    [Fact]
    public void Parse_ThrowsOnAReplyThatIsNotAJsonObject()
    {
        // The endpoint catches this and answers { checked:false } — but the parse itself must throw
        // rather than hand back a blank verdict that would render as an empty badge.
        Assert.Throws<JsonException>(() => ClaudeService.ParseDealRealityCheck("I couldn't tell what this is."));
    }

    // ── Metering: the same quota gate /api/analyze relies on ──────────────────

    [Fact]
    public async Task CheckDealAsync_IsRefusedWhenTheDailyAiAllowanceIsGone()
    {
        // The gate lives inside ClaudeService.CallModelAsync, so a deal check is metered by virtue of
        // going through it — no per-endpoint opt-in. Here the allowance is already spent, so the call
        // is refused before it can reach the network, exactly as /api/analyze would be.
        const long userId = 7;
        var dbPath = Path.Combine(Path.GetTempPath(), $"deal_check_quota_{Guid.NewGuid():N}.db");
        var usage = new AiUsageStore(dbPath);
        var now = DateTimeOffset.UtcNow;
        var day = AiUsageStore.DayOf(now);
        for (var i = 0; i < AiQuota.DefaultDailyLimit; i++)
            usage.TryConsume(userId, day, AiQuota.DefaultDailyLimit, now);

        var gate = new AiQuotaGate(usage, UserScope.PerUser(() => userId), AiQuota.DefaultDailyLimit, () => now);
        var claude = new ClaudeService(
            new CredentialsStore(Path.Combine(Path.GetTempPath(), $"deal_check_creds_{Guid.NewGuid():N}.json")),
            new ActionLog(),
            gate);

        var input = new DealCheckInput
        {
            Title = "Bitmain Antminer S19 XP 141TH Hashboard",
            Category = "Cryptocurrency Miners",
            BuyPrice = 220m,
            EstimatedResale = 994m,
            CompCount = 2,
        };

        await Assert.ThrowsAsync<AiQuotaExceededException>(() => claude.CheckDealAsync(input));

        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* temp file */ }
    }

    [Fact]
    public async Task CheckDealAsync_IsRefusedWhenNoUserIsSignedIn()
    {
        // Background work has no signed-in user and so no allowance to draw from — the metered path
        // refuses it rather than billing the owner's key to nobody.
        var dbPath = Path.Combine(Path.GetTempPath(), $"deal_check_nouser_{Guid.NewGuid():N}.db");
        var gate = new AiQuotaGate(new AiUsageStore(dbPath), UserScope.PerUser(() => null), AiQuota.DefaultDailyLimit);
        var claude = new ClaudeService(
            new CredentialsStore(Path.Combine(Path.GetTempPath(), $"deal_check_creds_{Guid.NewGuid():N}.json")),
            new ActionLog(),
            gate);

        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.CheckDealAsync(new DealCheckInput { Title = "anything" }));

        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* temp file */ }
    }
}
