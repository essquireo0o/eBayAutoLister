namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The last things the app did, in the seller's words — what the Logs screen shows.
/// </summary>
/// <remarks>
/// <para>
/// One list in memory, which was the whole design while the app had one seller. On a server it is
/// one list for everybody: item titles, eBay's refusals, the sources a Deal Radar watch reads, the
/// name of a photo somebody just saved — every account's, handed to any account that opened Logs.
/// So under a per-user <see cref="UserScope"/> each entry remembers who was signed in when it was
/// written, and <see cref="Recent"/> returns only the caller's own.
/// </para>
/// <para>
/// <b>Entries nobody was signed in for</b> — startup, the background loops — belong to no seller
/// and are shown to none. They are the server talking about itself (where its data folder is,
/// which database it found), and the person that is for is the owner, who reads everything through
/// <see cref="RecentForOwnerDashboard"/>.
/// </para>
/// <para>
/// The desktop build has one owner, so every entry is theirs and nothing here changes for it.
/// </para>
/// </remarks>
public sealed class ActionLog
{
    /// <summary>How many entries one seller keeps. The whole log, on the desktop build.</summary>
    public const int PerOwnerLimit = 100;

    /// <summary>
    /// How many are kept across every account on a server. A ceiling on memory rather than a
    /// feature: past it the oldest entry goes, whoever wrote it.
    /// </summary>
    public const int TotalLimit = 2000;

    private readonly object _sync = new();
    private readonly List<(long? Owner, ActionLogEntry Entry)> _entries = [];
    private readonly UserScope _scope;

    public ActionLog() : this(UserScope.Desktop) { }

    public ActionLog(UserScope scope)
    {
        _scope = scope;
        // The server's own line, whoever's request happened to be the first to need a log: on a
        // server it belongs to no account, on the desktop to the one seller.
        Add(scope.IsPerUser ? null : UserScope.DesktopOwner,
            "Info", "ING Listing Engine™ started", "Official product of ING Mining LLC — ready.");
    }

    // Read once per entry, never cached — see UserScope.OwnerId.
    public void Add(string level, string title, string detail) => Add(_scope.OwnerId, level, title, detail);

    private void Add(long? owner, string level, string title, string detail)
    {
        lock (_sync)
        {
            _entries.Insert(0, (owner, new ActionLogEntry(DateTimeOffset.UtcNow, level, title, detail)));

            // Each seller's own hundred. One shared hundred would let a busy account push a quiet
            // one's warnings off the end before they were ever read.
            var mine = 0;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Owner != owner || ++mine <= PerOwnerLimit) continue;
                _entries.RemoveAt(i--);
            }

            if (_entries.Count > TotalLimit) _entries.RemoveRange(TotalLimit, _entries.Count - TotalLimit);
        }
    }

    /// <summary>
    /// What the caller may read: everything on the desktop build, their own entries on a server,
    /// and nothing at all when a server has nobody signed in on this call.
    /// </summary>
    public IReadOnlyList<ActionLogEntry> Recent()
    {
        var owner = _scope.OwnerId;

        lock (_sync)
        {
            if (!_scope.IsPerUser) return _entries.Select(e => e.Entry).ToList();
            if (owner is null) return [];

            return _entries.Where(e => e.Owner == owner).Select(e => e.Entry).ToList();
        }
    }

    /// <summary>
    /// Every account's entries and the server's own, newest first. For the owner dashboard only —
    /// the one caller that has shown the admin key, and the one place a problem on somebody else's
    /// account can be seen by the person who has to fix it.
    /// </summary>
    public IReadOnlyList<ActionLogEntry> RecentForOwnerDashboard(int limit = 200)
    {
        lock (_sync)
        {
            return _entries.Take(Math.Max(0, limit)).Select(e => e.Entry).ToList();
        }
    }
}

public sealed record ActionLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Title,
    string Detail);
