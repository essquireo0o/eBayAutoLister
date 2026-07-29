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
    /// The hard cycle is minimize->normal, which is the only in-browser call that actually lifts
    /// the OS window — and it is VISIBLE: the window drops to the taskbar and springs back. Doing
    /// it repeatedly made the login window strobe for the first five seconds, right while eBay's
    /// CAPTCHA page was loading, which reads as a broken app rather than a helpful one.
    ///
    /// So it now fires ONCE, at t=0, while the window is still blank — a flash of an empty window
    /// nobody is looking at yet costs nothing. Winning the focus races that follow is
    /// <see cref="LoginWindowFocus.PinNewBrowserWindowBriefly"/>'s job: it holds the window topmost
    /// natively and re-asserts foreground only when something steals it, none of which is visible.
    /// The gentle <c>bringToFront()</c> calls in between are tab-level only and never move a window.
    ///
    /// Why it still stops after ~5 seconds: a window that keeps re-raising itself steals focus and
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
          // Launch-time only: ONE hard lift while the window is still blank, then gentle
          // tab-focus only. The hard cycle is visible, so repeating it strobes the window in the
          // user's face for the whole page load — LoginWindowFocus does the durable, invisible
          // version of this natively. Never move raise(true) into the loop, and never add another.
          const RAISE_BURST_MS = 5000;
          async function raiseBurst() {
            await raise(true);
            const start = Date.now();
            while (Date.now() - start < RAISE_BURST_MS) {
              await raise(false);
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
    /// <param name="ct">
    /// Cancelling kills the process tree too, and throws. Without this a browser outlived the
    /// request that asked for it: the caller gave up, the endpoint returned, and a headless Chrome
    /// carried on loading Facebook in the background until its own timeout — several of them at once
    /// on a machine where someone kept clicking Search.
    /// </param>
    public static async Task<NodeRunResult> RunAsync(
        string script, TimeSpan timeout, string filePrefix, Action? beforeStart = null,
        CancellationToken ct = default)
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
                // Node writes UTF-8. Without saying so, .NET decodes it with the console's legacy
                // code page and every non-ASCII character arrives mangled — a Marketplace listing
                // for a "Luminária" came back as "LuminÃ¡ria". It corrupts the JSON these scripts
                // emit, so it is a data bug, not a display one.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            beforeStart?.Invoke();

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                // Kill first, decide what to report second: either way no browser is left running.
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
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
