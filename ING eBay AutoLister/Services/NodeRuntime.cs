using System.Diagnostics;

namespace ING_eBay_AutoLister.Services;

public sealed record NodeRunResult(string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Shared plumbing for the app's browser-automation scrapes: finding node.exe, finding the
/// installed Playwright package, and running a throwaway script to completion under a timeout.
///
/// Extracted from TerapeakService when FacebookMarketplaceService needed the identical setup —
/// both drive a real logged-in browser session because neither site offers a search API. The
/// node/PATH resolution below in particular is the kind of thing that must not exist in two
/// copies: it encodes a Windows-specific failure mode that took a while to diagnose.
/// </summary>
public static class NodeRuntime
{
    public static string PlaywrightDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "playwright");

    // A GUI process (double-click, startup shortcut, tray auto-launch) inherits whatever PATH
    // its parent (usually explorer.exe) had cached at logon — installing Node.js later doesn't
    // reach it until a full sign-out, even though a freshly-opened terminal sees it immediately.
    // Relying on bare "node" + PATH search silently breaks for exactly that reason, so resolve a
    // concrete node.exe path up front instead of trusting the inherited environment.
    private static readonly Lazy<string> ResolvedNodeExe = new(() =>
    {
        string[] candidates =
        [
            Environment.GetEnvironmentVariable("ProgramFiles") is { } pf ? Path.Combine(pf, "nodejs", "node.exe") : "",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { } pf86 ? Path.Combine(pf86, "nodejs", "node.exe") : "",
            Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(dir => { try { return Path.Combine(dir, "node.exe"); } catch { return ""; } })
                .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p)) ?? ""
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "node"; // last resort: let Process.Start try PATH itself
    });

    public static string NodeExe => ResolvedNodeExe.Value;

    /// <summary>Escapes a Windows path for embedding in a single-quoted JS string literal.</summary>
    public static string JsPath(string path) => path.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>
    /// JavaScript defining <c>raise(hard)</c> and <c>raiseBurst()</c> for the interactive login
    /// scripts. Paste it into a script that already has <c>ctx</c> (BrowserContext) and
    /// <c>page</c> in scope; both Terapeak and Facebook Marketplace embed this exact text.
    ///
    /// Why a burst and not a single call: <c>page.bringToFront()</c> only focuses the tab inside
    /// Chrome, and even the CDP minimize->normal cycle — which does move the real OS window —
    /// loses races against Windows' focus rules, the app's own window, and Chrome's own startup
    /// window placement, which happens over the first second or two of the browser's life. One
    /// attempt at t=0 therefore lands *behind* the app often enough that sellers reported the
    /// login as "not connecting" while the window was sitting there, buried. Repeating the hard
    /// cycle a handful of times across the first ~5 seconds wins those races.
    ///
    /// Why it stops after ~5 seconds: a window that keeps re-raising itself steals focus and
    /// eats keystrokes while someone is typing a password or clearing a CAPTCHA. Grabbing
    /// attention is a startup behaviour only — the wait loops that follow use bare
    /// <c>bringToFront()</c> at a much slower cadence, which does not disturb typing.
    /// </summary>
    public const string RaiseToFrontJs = """
          let cdp = null;
          async function cdpSession() { if (!cdp) cdp = await ctx.newCDPSession(page); return cdp; }
          // hard=false: cheap tab focus. hard=true: minimize->normal, the only thing that
          // actually lifts the OS window above whatever the user is currently looking at.
          async function raise(hard) {
            try {
              await page.bringToFront().catch(() => {});
              if (!hard) return;
              const s = await cdpSession();
              const { windowId } = await s.send('Browser.getWindowForTarget');
              await s.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'minimized' } });
              await s.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'normal' } });
            } catch (_) {}
          }
          // Launch-time only: hard lifts at these offsets, gentle focus in between, then STOP —
          // never extend this past the window below or it will interrupt the user mid-login.
          const RAISE_BURST_MS = 5000;
          async function raiseBurst() {
            const hardAt = [0, 400, 1200, 2400, 4000];
            const start = Date.now();
            let next = 0;
            while (Date.now() - start < RAISE_BURST_MS) {
              const elapsed = Date.now() - start;
              if (next < hardAt.length && elapsed >= hardAt[next]) { next++; await raise(true); }
              else await raise(false);
              await page.waitForTimeout(250).catch(() => {});
            }
          }
        """;

    /// <summary>
    /// Writes <paramref name="script"/> to a temp .cjs file, runs it under node with the
    /// Playwright package directory as the working directory, and returns its output. The
    /// script file is always deleted, and a run that overruns <paramref name="timeout"/> has
    /// its whole process tree killed rather than being left holding a browser open.
    /// </summary>
    public static async Task<NodeRunResult> RunAsync(
        string script, TimeSpan timeout, string filePrefix, Action? beforeStart = null)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"{filePrefix}_{Guid.NewGuid():N}.cjs");
        await File.WriteAllTextAsync(scriptFile, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = NodeExe,
                ArgumentList           = { scriptFile },
                WorkingDirectory       = PlaywrightDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            beforeStart?.Invoke();

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new NodeRunResult("", "", TimedOut: true);
            }

            return new NodeRunResult((await stdoutTask).Trim(), await stderrTask, TimedOut: false);
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }
}
