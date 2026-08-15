using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The one way anything in this app puts something on the desktop, and the seam between the web
/// server and the Windows tray icon that can actually draw it.
/// </summary>
/// <remarks>
/// <para>
/// The radar runs inside the same process as the tray icon, but nothing in <c>Services</c> should
/// know that a <c>NotifyIcon</c> exists — a background service reaching into WinForms is how a
/// headless run (this app also installs as a Windows service) turns into a crash at 3am with nobody
/// watching. So this holds a queue and an event; <c>Program.cs</c> subscribes the tray icon to it if
/// there is one.
/// </para>
/// <para>
/// <see cref="Channel"/> is the honest part. A Windows service runs in session 0 and physically
/// cannot show a balloon, so with no tray attached this reports <c>browser</c> and the UI says the
/// tab has to stay open rather than promising a notification that will never appear. Nothing here
/// ever pretends a notification was delivered.
/// </para>
/// </remarks>
#if HOSTED
/// <summary>
/// The hosted build's version: same shape, does nothing, and says so.
/// </summary>
/// <remarks>
/// There is no desktop. The seller is on a browser somewhere else entirely, and the one honest
/// answer to "can this process put a balloon on your screen?" is no — which is exactly what
/// <see cref="Channel"/> reporting <c>browser</c> already means, so the radar UI needs no hosted
/// special case: it keeps telling the seller the tab has to stay open, and that is now the truth
/// rather than a fallback.
///
/// Kept as a no-op rather than removed because the type is a constructor parameter of
/// <c>DealRadarService</c> and five endpoints, all of which are platform-neutral and stay that way.
/// It also drops the history queue: a list of notifications nothing could ever deliver is a log of
/// non-events, and <c>Recent</c> is read by the UI to tick "sent to desktop".
/// </remarks>
public sealed class DesktopNotifier
{
    // Nothing raises this here — no tray, no balloon. Declared so the type keeps one shape across
    // both builds; a hosted-only compile error on a subscriber would be a Windows/Linux drift bug.
#pragma warning disable CS0067
    public event Action<DesktopNotification>? Notified;
#pragma warning restore CS0067

    public void AttachDesktopChannel() { }

    public void DetachDesktopChannel() { }

    /// <summary>Always <c>browser</c>. Nothing in a container can reach the seller's screen.</summary>
    public string Channel => RadarChannels.Browser;

    public IReadOnlyList<DesktopNotification> Recent => [];

    /// <summary>Always false — no channel took it, because there is no channel.</summary>
    public bool Send(DesktopNotification notification) => false;
}
#else
public sealed class DesktopNotifier
{
    /// <summary>How many recent notifications to remember, for the "already told you" check.</summary>
    private const int HistoryLimit = 20;

    private readonly object _lock = new();
    private readonly Queue<DesktopNotification> _history = new();
    private volatile bool _hasDesktopChannel;

    /// <summary>
    /// Raised on the caller's thread when something wants the desktop's attention. Program.cs marshals
    /// it onto the UI thread; nothing else subscribes.
    /// </summary>
    public event Action<DesktopNotification>? Notified;

    /// <summary>
    /// Called once by the tray icon when it is live. Until then <see cref="Channel"/> is
    /// <see cref="RadarChannels.Browser"/> and the UI says so.
    /// </summary>
    public void AttachDesktopChannel() => _hasDesktopChannel = true;

    public void DetachDesktopChannel() => _hasDesktopChannel = false;

    /// <summary>tray | browser — where an alert can actually surface from this process.</summary>
    public string Channel => _hasDesktopChannel ? RadarChannels.Tray : RadarChannels.Browser;

    /// <summary>What was sent recently, newest last. Read by the UI to tick "sent to desktop".</summary>
    public IReadOnlyList<DesktopNotification> Recent
    {
        get { lock (_lock) return [.. _history]; }
    }

    /// <summary>
    /// Puts one thing on the desktop, if anything can. Returns whether a channel took it.
    /// </summary>
    /// <remarks>
    /// Never throws. A notification is the least important thing happening at the moment it fires —
    /// the find is already saved — and a tray icon that has just been disposed by a seller quitting
    /// the app must not take the scan down with it.
    /// </remarks>
    public bool Send(DesktopNotification notification)
    {
        if (notification is null) return false;

        lock (_lock)
        {
            _history.Enqueue(notification);
            while (_history.Count > HistoryLimit) _history.Dequeue();
        }

        var handler = Notified;
        if (handler is null) return false;

        try
        {
            handler(notification);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
#endif
