using System.Net;
using System.Text;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The owner dashboard's "My programs on GitHub" table. What matters here is that the numbers are
/// the ones GitHub publishes, that a failing GitHub never takes the dashboard down with it, and
/// that the page is not allowed to spend the server's sixty anonymous requests an hour.
/// </summary>
public class GithubProgramsTests
{
    private const string Repos = """
        [
          {"name":"rigalert","full_name":"someone/rigalert","description":" Miner monitor ","html_url":"https://github.com/someone/rigalert",
           "language":"Python","stargazers_count":3,"forks_count":1,"open_issues_count":2,"pushed_at":"2026-06-25T21:45:52Z",
           "private":false,"fork":false,"archived":false},
          {"name":"daily-alerts","full_name":"someone/daily-alerts","description":null,"html_url":"https://github.com/someone/daily-alerts",
           "language":null,"stargazers_count":0,"forks_count":0,"open_issues_count":0,"pushed_at":"2026-09-21T16:04:03Z",
           "private":false,"fork":false,"archived":false},
          {"name":"somebody-elses","full_name":"someone/somebody-elses","fork":true,"archived":false},
          {"name":"retired","full_name":"someone/retired","fork":false,"archived":true}
        ]
        """;

    [Fact]
    public void A_fork_or_an_archived_repository_is_not_one_of_the_owners_programs()
    {
        var programs = GithubPrograms.ParseRepos(Repos);

        Assert.Equal(["rigalert", "daily-alerts"], programs.Select(p => p.Name));

        var rig = programs[0];
        Assert.Equal("Miner monitor", rig.Description);
        Assert.Equal("Python", rig.Language);
        Assert.Equal((3, 1, 2), (rig.Stars, rig.Forks, rig.OpenIssues));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 21, 45, 52, TimeSpan.Zero), rig.PushedAt);
        Assert.False(rig.Private);

        // GitHub sends null for a repository with no description and no detected language.
        Assert.Null(programs[1].Description);
        Assert.Null(programs[1].Language);
    }

    [Fact]
    public void Downloads_are_every_file_of_every_published_release_added_together()
    {
        var summary = GithubPrograms.ParseReleases("""
            [
              {"tag_name":"v1.1.0","draft":false,"published_at":"2026-05-01T00:00:00Z",
               "assets":[{"download_count":5},{"download_count":2}]},
              {"tag_name":"v1.2.0","draft":false,"published_at":"2026-08-01T00:00:00Z",
               "assets":[{"download_count":30}]},
              {"tag_name":"v9.9.9","draft":true,"published_at":null,
               "assets":[{"download_count":1000}]}
            ]
            """);

        // The draft is counted nowhere: nobody outside the account can download it.
        Assert.Equal(2, summary.Count);
        Assert.Equal(37, summary.Downloads);

        // Latest by publish date, not by where it sits in the list.
        Assert.Equal("v1.2.0", summary.LatestTag);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), summary.LatestAt);
    }

    [Fact]
    public void A_program_that_was_never_released_reports_nothing_rather_than_failing()
    {
        Assert.Equal(ReleaseSummary.None, GithubPrograms.ParseReleases("[]"));

        // An error body is an object, not a list — e.g. GitHub's {"message":"Not Found"}.
        Assert.Equal(ReleaseSummary.None, GithubPrograms.ParseReleases("""{"message":"Not Found"}"""));
        Assert.Empty(GithubPrograms.ParseRepos("""{"message":"API rate limit exceeded"}"""));
    }

    [Fact]
    public async Task The_table_is_ordered_by_downloads_and_says_it_is_public_only()
    {
        var github = new StubGithub(url => url switch
        {
            _ when url.Contains("/users/someone/repos") => (200, Repos),
            _ when url.EndsWith("/repos/someone/rigalert/releases?per_page=100") =>
                (200, """[{"tag_name":"RigAlertv1.0.0","published_at":"2026-05-19T19:58:05Z","assets":[{"download_count":41}]}]"""),
            _ => (200, "[]"),
        });

        var snapshot = await Service(github).GetAsync();

        Assert.Null(snapshot.Error);
        Assert.Equal("someone", snapshot.Owner);
        Assert.False(snapshot.IncludesPrivate);
        Assert.NotNull(snapshot.FetchedAt);

        // daily-alerts was pushed more recently, but rigalert is the one people downloaded.
        Assert.Equal(["rigalert", "daily-alerts"], snapshot.Programs.Select(p => p.Name));
        Assert.Equal((1, 41L, "RigAlertv1.0.0"),
            (snapshot.Programs[0].Releases, snapshot.Programs[0].Downloads, snapshot.Programs[0].LatestTag));
        Assert.Equal((0, 0L, (string?)null),
            (snapshot.Programs[1].Releases, snapshot.Programs[1].Downloads, snapshot.Programs[1].LatestTag));

        // No token configured, so none is sent and the public listing is the one asked for.
        Assert.All(github.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task Opening_the_dashboard_again_does_not_ask_GitHub_again()
    {
        var github = new StubGithub(url => url.Contains("/users/") ? (200, Repos) : (200, "[]"));
        var service = Service(github);

        await service.GetAsync();
        var asked = github.Requests.Count;
        await service.GetAsync();
        await service.GetAsync();

        // One listing plus one releases call per program, once — sixty an hour is all an
        // anonymous caller gets, shared by everything on the server's address.
        Assert.Equal(3, asked);
        Assert.Equal(asked, github.Requests.Count);
    }

    [Fact]
    public async Task A_rate_limited_GitHub_is_explained_and_is_not_retried_on_the_next_page_load()
    {
        var github = new StubGithub(_ => (403, """{"message":"API rate limit exceeded"}"""));
        var service = Service(github);

        var snapshot = await service.GetAsync();

        Assert.Empty(snapshot.Programs);
        Assert.Contains("rate-limiting", snapshot.Error);

        await service.GetAsync();
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task GitHub_being_unreachable_is_an_answer_not_an_exception()
    {
        var github = new StubGithub(_ => throw new HttpRequestException("no route to host"));

        var snapshot = await Service(github).GetAsync();

        Assert.Empty(snapshot.Programs);
        Assert.NotNull(snapshot.Error);
        Assert.Null(snapshot.FetchedAt);
    }

    [Fact]
    public async Task One_repositorys_releases_failing_costs_that_row_not_the_table()
    {
        var github = new StubGithub(url =>
            url.Contains("/users/") ? (200, Repos)
            : url.Contains("/rigalert/") ? throw new HttpRequestException("reset")
            : (200, """[{"tag_name":"v1","published_at":"2026-09-01T00:00:00Z","assets":[{"download_count":4}]}]"""));

        var snapshot = await Service(github).GetAsync();

        Assert.Null(snapshot.Error);
        Assert.Equal(2, snapshot.Programs.Count);
        Assert.Equal(4, snapshot.Programs.Single(p => p.Name == "daily-alerts").Downloads);
        Assert.Equal(0, snapshot.Programs.Single(p => p.Name == "rigalert").Downloads);
    }

    [Fact]
    public async Task A_configured_token_asks_for_the_private_ones_too_and_says_so()
    {
        var github = new StubGithub(url => url.Contains("/user/repos") ? (200, Repos) : (200, "[]"));

        var snapshot = await Service(github, token: "example-token").GetAsync();

        Assert.True(snapshot.IncludesPrivate);
        Assert.Contains(github.Requests, r => r.Url.Contains("/user/repos?affiliation=owner"));
        Assert.All(github.Requests, r => Assert.Equal("Bearer example-token", r.Authorization));
    }

    private static GithubPrograms Service(StubGithub github, string? token = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Owner:GithubUser"] = "someone",
            ["Owner:GithubToken"] = token,
        }).Build();
        return new GithubPrograms(new StubFactory(github), config);
    }

    private sealed record Asked(string Url, string? Authorization);

    private sealed class StubGithub(Func<string, (int Status, string Body)> answer) : HttpMessageHandler
    {
        public List<Asked> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new Asked(url, request.Headers.Authorization?.ToString()));

            var (status, body) = answer(url);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(StubGithub handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
