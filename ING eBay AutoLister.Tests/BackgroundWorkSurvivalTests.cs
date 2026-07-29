using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The seller's page showed "Failed to fetch" because the backend was not running. The question that
/// mattered next was whether it had died on its own, and the shape that would have done it is a
/// fire-and-forget <c>Task.Run</c> whose body throws with nothing to catch it — a server dying
/// silently while its page stays open and keeps clicking.
///
/// It had not: there was no crash.log, the scan endpoint answers 400 rather than throwing, and the
/// rewrite job wraps its whole background body. These tests keep it that way, because the failure
/// they guard against is invisible until it happens to someone.
/// </summary>
public class BackgroundWorkSurvivalTests
{
    [Fact]
    public async Task ARewriteThatFailsImmediatelyEndsAsAnErrorRatherThanAnEscape()
    {
        // The whole background body has to be inside a catch. Given no eBay service at all — the
        // most abrupt failure available, and a stand-in for eBay being down, the token being dead or
        // anything else that throws on the first call — the run has to come back and say so.
        var job = new CopilotSeoJob(ebay: null!, claude: null!, drafts: null!, log: new ActionLog());

        var run = job.Start([]);
        Assert.NotNull(run);

        // Give the background task room to fail and record it. It fails on its first statement, so
        // this is generous rather than tight.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!run.Finished && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(run.Finished, "the run never finished — its exception escaped the background task");
        Assert.Equal("Failed", run.Stage);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
        Assert.NotNull(run.FinishedAt);

        // And the poll the UI runs can read it, which is what turns a dead run into a red bar
        // instead of a spinner that never stops.
        Assert.Equal(0, run.Done);
    }

    [Fact]
    public async Task AFailedRunDoesNotPoisonTheNextOne()
    {
        // One bad run must not leave the job unable to start another — a seller whose first attempt
        // failed will press the button again, and "nothing happens" is worse than the first failure.
        var job = new CopilotSeoJob(ebay: null!, claude: null!, drafts: null!, log: new ActionLog());

        var first = job.Start([]);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!first.Finished && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(first.Finished);

        var second = job.Start([]);
        Assert.NotSame(first, second);

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (!second.Finished && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(second.Finished);
        Assert.Equal("Failed", second.Stage);
    }

    [Fact]
    public void TheCopilotScanEndpointAnswersRatherThanThrows()
    {
        // A minimal-API handler that throws returns a 500 with an HTML error page, which the browser
        // hands the UI as unparseable text. This one catches and answers with a sentence — read from
        // the source because standing up the whole host with a live eBay account is not a unit test.
        var scan = Endpoint("app.MapGet(\"/api/copilot/scan\"");
        Assert.Contains("try", scan);
        Assert.Contains("catch (Exception ex)", scan);
        Assert.Contains("Results.BadRequest(new { error = ex.Message })", scan);
    }

    [Fact]
    public void EveryFireAndForgetTaskCatchesItsOwnExceptions()
    {
        // The license check was the one `Task.Run` in the app whose body could throw with nothing to
        // catch it. Nothing is gated on the answer — the app is free beta — so a check that cannot
        // reach the network is logged and ignored, not left to fault a task nobody awaits.
        var program = ReadSource("Program.cs");
        var licenseCheck = program[program.IndexOf("// Background license check", StringComparison.Ordinal)..];
        licenseCheck = licenseCheck[..licenseCheck.IndexOf("// ── Background maintenance loop", StringComparison.Ordinal)];

        Assert.Contains("try", licenseCheck);
        Assert.Contains("catch (Exception ex)", licenseCheck);
        Assert.Contains("License check failed", licenseCheck);
    }

    [Fact]
    public void AFaultedBackgroundTaskCanNeverTakeTheServerDown()
    {
        // The net under all of them, including any added later: the exception is observed, so it
        // cannot escalate however the runtime is configured, and it is written to the same crash.log
        // that the process-level handler uses so the cause is still readable afterwards.
        var program = ReadSource("Program.cs");
        Assert.Contains("TaskScheduler.UnobservedTaskException", program);
        Assert.Contains("e.SetObserved();", program);
        Assert.Contains("unobserved background task exception (server kept running)", program);
    }

    private static string Endpoint(string signature)
    {
        var program = ReadSource("Program.cs");
        var start = program.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is gone from Program.cs");
        var end = program.IndexOf("\n});", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'");
        return program[start..end];
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
