using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The owner's other programs, as GitHub publishes them: what each one is, how many people starred
/// it, what was last released and how many times its release files have been downloaded.
/// </summary>
/// <remarks>
/// <para>
/// Feeds the owner dashboard, which until now could only see this one product. It reads GitHub's
/// public REST API and nothing else — no telemetry exists in any of these programs, so a release
/// download count is the only usage signal there is, and the dashboard says so rather than
/// presenting it as "users".
/// </para>
/// <para>
/// Three rules, the same ones <see cref="UpdateChecker"/> lives by:
/// </para>
/// <list type="number">
///   <item>It must never break the dashboard. Offline, rate-limited, GitHub down, JSON that does not
///     parse — each ends as the last good answer with a plain-words note, never as a 500.</item>
///   <item>It must not hammer GitHub. Unauthenticated callers get 60 requests an hour per IP, and one
///     refresh costs one request plus one per repository, so the answer is cached for thirty
///     minutes and shared by everyone who opens the page.</item>
///   <item>Public means public. Without a token GitHub only lists public repositories, and that is
///     the default. Private ones appear only when <c>Owner:GithubToken</c> is configured, and the
///     snapshot says which of the two the reader is looking at — a short list must not be mistaken
///     for a complete one.</item>
/// </list>
/// </remarks>
public sealed class GithubPrograms(IHttpClientFactory httpFactory, IConfiguration config)
{
    /// <summary>The account <see cref="UpdateChecker"/> already reads releases from.</summary>
    public const string DefaultOwner = "essquireo0o";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GithubProgramsSnapshot? _cached;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    public async Task<GithubProgramsSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
                return _cached;

            var fresh = await FetchAsync(ct);

            // A failed refresh keeps showing the numbers from the last good one, with the reason
            // attached. Yesterday's download counts are more use to the owner than an empty table.
            if (fresh.Error is not null && _cached is { Programs.Count: > 0 })
                fresh = _cached with { Error = fresh.Error };

            _cached = fresh;
            // Checked-at moves on a failure too, so a rate-limited GitHub is not retried on every
            // page load — retrying is exactly what keeps the limit from clearing.
            _checkedAt = DateTimeOffset.UtcNow;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GithubProgramsSnapshot> FetchAsync(CancellationToken ct)
    {
        var owner = config["Owner:GithubUser"] is { Length: > 0 } configured ? configured : DefaultOwner;
        var token = config["Owner:GithubToken"];
        var withPrivate = !string.IsNullOrWhiteSpace(token);

        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            // GitHub rejects requests with no User-Agent outright.
            client.DefaultRequestHeaders.Add("User-Agent", $"ING-Listing-Engine/{UpdateChecker.CurrentVersion}");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            if (withPrivate)
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token!.Trim()}");

            // /user/repos is the only listing that includes private repositories, and it answers for
            // whoever the token belongs to; /users/{owner}/repos is the public view of a named account.
            var listUrl = withPrivate
                ? "https://api.github.com/user/repos?affiliation=owner&per_page=100&sort=pushed"
                : $"https://api.github.com/users/{Uri.EscapeDataString(owner)}/repos?type=owner&per_page=100&sort=pushed";

            var res = await client.GetAsync(listUrl, ct);
            if (!res.IsSuccessStatusCode)
                return Failed(owner, withPrivate, Explain((int)res.StatusCode));

            var repos = ParseRepos(await res.Content.ReadAsStringAsync(ct));
            var programs = new List<GithubProgram>(repos.Count);

            foreach (var repo in repos)
            {
                // One repository's releases failing to load costs that row its release columns, not
                // the whole table.
                var releases = ReleaseSummary.None;
                try
                {
                    var rel = await client.GetAsync(
                        $"https://api.github.com/repos/{repo.FullName}/releases?per_page=100", ct);
                    if (rel.IsSuccessStatusCode)
                        releases = ParseReleases(await rel.Content.ReadAsStringAsync(ct));
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                programs.Add(repo with
                {
                    Releases = releases.Count,
                    Downloads = releases.Downloads,
                    LatestTag = releases.LatestTag,
                    LatestAt = releases.LatestAt,
                });
            }

            // Most-downloaded first; among the never-released, the most recently worked on.
            var ordered = programs
                .OrderByDescending(p => p.Downloads)
                .ThenByDescending(p => p.PushedAt ?? DateTimeOffset.MinValue)
                .ToList();

            return new GithubProgramsSnapshot(owner, DateTimeOffset.UtcNow, ordered, null, withPrivate);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failed(owner, withPrivate, $"GitHub could not be reached ({ex.GetType().Name}).");
        }
    }

    private static GithubProgramsSnapshot Failed(string owner, bool withPrivate, string why) =>
        new(owner, null, [], why, withPrivate);

    private static string Explain(int status) => status switch
    {
        403 or 429 => "GitHub is rate-limiting this server right now. The numbers return on their own within the hour.",
        401        => "GitHub refused the configured token. Public programs are shown again once it is removed or replaced.",
        404        => "GitHub does not know that account name.",
        _          => $"GitHub answered with an error ({status}).",
    };

    /// <summary>
    /// Reads GitHub's repository list. Forks and archived repositories are left out: a fork is
    /// somebody else's program and an archived one is no longer one of the owner's.
    /// </summary>
    public static IReadOnlyList<GithubProgram> ParseRepos(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var list = new List<GithubProgram>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "fork") || Bool(r, "archived")) continue;

            var name = Str(r, "name");
            var fullName = Str(r, "full_name");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullName)) continue;

            list.Add(new GithubProgram(
                Name: name,
                FullName: fullName,
                Description: Str(r, "description")?.Trim(),
                Url: Str(r, "html_url") ?? $"https://github.com/{fullName}",
                Language: Str(r, "language"),
                Stars: Int(r, "stargazers_count"),
                Forks: Int(r, "forks_count"),
                OpenIssues: Int(r, "open_issues_count"),
                PushedAt: Date(r, "pushed_at"),
                Private: Bool(r, "private")));
        }
        return list;
    }

    /// <summary>
    /// Totals one repository's releases. Downloads are summed over every file of every published
    /// release — GitHub counts per file, and a program that has shipped ten versions has its
    /// downloads spread across all ten. Drafts are skipped: nobody outside can download them.
    /// </summary>
    public static ReleaseSummary ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return ReleaseSummary.None;

        var count = 0;
        long downloads = 0;
        string? latestTag = null;
        DateTimeOffset? latestAt = null;

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (Bool(rel, "draft")) continue;
            count++;

            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                    if (asset.TryGetProperty("download_count", out var dc) && dc.TryGetInt64(out var n))
                        downloads += n;

            // Newest by publish date rather than by position: the API's order is by creation, and a
            // release edited or published late would otherwise be reported as the latest.
            var at = Date(rel, "published_at");
            if (at is not null && (latestAt is null || at > latestAt))
            {
                latestAt = at;
                latestTag = Str(rel, "tag_name");
            }
        }

        return new ReleaseSummary(count, downloads, latestTag, latestAt);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
}

/// <param name="Name">The repository's short name.</param>
/// <param name="FullName">owner/name, as the API addresses it.</param>
/// <param name="Url">Its page on github.com.</param>
/// <param name="Releases">Published releases; zero means it has never been packaged for download.</param>
/// <param name="Downloads">Release-file downloads, all versions together. The only usage signal GitHub has.</param>
/// <param name="LatestTag">The newest published release's tag, when there is one.</param>
public sealed record GithubProgram(
    string Name, string FullName, string? Description, string Url, string? Language,
    int Stars, int Forks, int OpenIssues, DateTimeOffset? PushedAt, bool Private,
    int Releases = 0, long Downloads = 0, string? LatestTag = null, DateTimeOffset? LatestAt = null);

public sealed record ReleaseSummary(int Count, long Downloads, string? LatestTag, DateTimeOffset? LatestAt)
{
    public static readonly ReleaseSummary None = new(0, 0, null, null);
}

/// <param name="FetchedAt">When these numbers were read from GitHub; null when they never have been.</param>
/// <param name="Error">Why the list is stale or empty, in words the owner can read; null when it is current.</param>
/// <param name="IncludesPrivate">False when only public repositories were asked for — the list is then not all of them.</param>
public sealed record GithubProgramsSnapshot(
    string Owner, DateTimeOffset? FetchedAt, IReadOnlyList<GithubProgram> Programs, string? Error, bool IncludesPrivate);
