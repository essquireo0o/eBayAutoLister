using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Gets the one-time interactive login browser (Terapeak, Facebook Marketplace) in front of the
/// user instead of behind the app.
///
/// The failure this exists for: everything works — node runs, Playwright launches Chrome, the
/// login page loads, loginInProgress stays true — but the window opens *behind* the app window,
/// so the seller sees a status that says "connecting" and nothing else, and concludes the
/// feature is broken. Nothing is broken; the window is buried.
///
/// Three cooperating nudges, because no single one is reliable on Windows:
///   1. <see cref="Grant"/> — Windows refuses foreground changes from a background process
///      unless the current foreground app hands over its rights first. Without it the spawned
///      Chrome cannot legally raise itself at all.
///   2. <see cref="PinNewBrowserWindowBriefly"/> (here) — pins the newly-launched Chrome window
///      topmost for a few seconds so it wins over anything that grabs focus while Chrome is
///      still starting up, then releases it.
///   3. <see cref="NodeRuntime.RaiseToFrontJs"/> — the in-browser side, repeating a
///      minimize->normal cycle over the same few seconds.
///
/// All three are deliberately short-lived. A login window that keeps forcing itself to the front
/// eats the keystrokes of the password it is asking for.
/// </summary>
public static class LoginWindowFocus
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int ASFW_ANY = -1;
    private static readonly IntPtr HWND_TOPMOST   = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    /// <summary>How long a freshly-launched login window is held above other windows.</summary>
    private static readonly TimeSpan PinWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grants any process a one-time pass to call SetForegroundWindow, so the browser this app
    /// is about to spawn can raise itself. Call immediately before starting the login process.
    /// </summary>
    public static void Grant()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
    }

    /// <summary>
    /// Watches for the Chrome window the login script is about to launch and holds it topmost
    /// for a few seconds, then puts it back to normal z-order. Returns immediately — the watch
    /// runs in the background and is best-effort throughout: a login that still works with the
    /// window merely un-raised is far better than one that throws here.
    /// </summary>
    public static void PinNewBrowserWindowBriefly()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Task.Run(PinLoopAsync);
    }

    [SupportedOSPlatform("windows")]
    private static async Task PinLoopAsync()
    {
        // Anything already on screen belongs to the user's own browsing and must not be
        // touched — only processes that start from here on can be the login window.
        var launchedAfter = DateTime.Now;                                          // Process.StartTime is local time
        var giveUpAt      = DateTime.UtcNow + TimeSpan.FromSeconds(20);            // node + Chrome cold start
        var pinned        = new List<IntPtr>();
        DateTime? firstPinnedAt = null;

        try
        {
            while (DateTime.UtcNow < giveUpAt)
            {
                foreach (var hwnd in NewBrowserWindows(launchedAfter))
                {
                    if (!pinned.Contains(hwnd))
                    {
                        pinned.Add(hwnd);
                        firstPinnedAt ??= DateTime.UtcNow;
                        // Topmost first, then activate: the pin survives whatever else is competing
                        // for the foreground during Chrome's first second.
                        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        SetForegroundWindow(hwnd);
                    }
                    else if (GetForegroundWindow() != hwnd)
                    {
                        // Something took the foreground back while Chrome was still starting. This
                        // re-assert is what replaced the browser-side minimize->normal burst, which
                        // won the same races by visibly strobing the window through the whole page
                        // load. Guarded by the foreground check so a window that is already correct
                        // is left completely alone — an unconditional call here would fight the
                        // user for focus while they type.
                        SetForegroundWindow(hwnd);
                    }
                }

                // Timed from the window appearing, not from the click: Chrome's own startup lag
                // must not eat the few seconds the pin is supposed to be visible for.
                if (firstPinnedAt is { } t && DateTime.UtcNow - t >= PinWindow) break;
                await Task.Delay(250);
            }
        }
        catch { /* best effort */ }
        finally
        {
            // Always un-pin: leaving the login window stuck above everything else outlives the
            // problem it solves, and the user cannot get their own windows back over it.
            foreach (var hwnd in pinned)
                try { SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
                catch { }
        }
    }

    /// <summary>
    /// Visible top-level Chrome windows from processes started after <paramref name="after"/>.
    /// Playwright launches its own Chrome process with a private profile, so a start time later
    /// than the login click identifies it without disturbing the user's existing Chrome windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<IntPtr> NewBrowserWindows(DateTime after)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("chrome"); } catch { yield break; }

        foreach (var proc in procs)
        {
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                // Renderer/GPU children have no window; only the browser process does.
                if (proc.StartTime >= after && proc.MainWindowHandle != IntPtr.Zero)
                    hwnd = proc.MainWindowHandle;
            }
            catch { /* exited or access denied between enumeration and read */ }
            finally { proc.Dispose(); }

            if (hwnd != IntPtr.Zero) yield return hwnd;
        }
    }
}
