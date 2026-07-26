using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The source picker's rules. Adding OfferUp later should mean writing one ILocalSupplySource and
// registering it — these cases are what guarantee the rest of the pipeline doesn't need to know.
public class LocalSupplySourcesTests
{
    private sealed class StubSource(string id, bool available, bool requiresConnection = true) : ILocalSupplySource
    {
        public string Id => id;
        public string Label => id;
        public bool RequiresConnection => requiresConnection;
        public bool IsAvailable => available;
        public string AvailabilityNote => available ? "ready" : "connect first";

        public Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default) =>
            Task.FromResult(new LocalSupplySearchResult { SourceId = id, Status = available ? "ok" : "not_connected" });
    }

    private static LocalSupplySources Registry() => new([
        new StubSource("craigslist", available: true, requiresConnection: false),
        new StubSource("facebook", available: false),
    ]);

    // A seller who has connected nothing still gets a full search — that's the point of leading
    // with a public source.
    [Fact]
    public void Resolve_NoPreference_UsesWhateverCanAnswerRightNow()
    {
        var picked = Registry().Resolve(null);

        Assert.Equal(["craigslist"], picked.Select(s => s.Id));
    }

    // An explicitly requested site is searched even when it can't answer: its not_connected status
    // is what puts the connect prompt on screen, which beats dropping the site the seller asked for.
    [Fact]
    public void Resolve_ExplicitlyNamedSource_IsSearchedEvenWhileDisconnected()
    {
        var picked = Registry().Resolve("facebook");

        Assert.Equal(["facebook"], picked.Select(s => s.Id));
    }

    [Fact]
    public void Resolve_ParsesACommaListAndTolerablesSpacing()
    {
        var picked = Registry().Resolve(" facebook , craigslist ");

        Assert.Equal(2, picked.Count);
    }

    // A typo in a stored preference must not turn into an empty search with no explanation.
    [Fact]
    public void Resolve_OnlyUnknownNames_FallsBackToWhatsAvailable()
    {
        var picked = Registry().Resolve("offerup");

        Assert.Equal(["craigslist"], picked.Select(s => s.Id));
    }

    [Fact]
    public void Describe_ListsDisconnectedSourcesWithTheirReason()
    {
        var described = Registry().Describe();

        var facebook = described.Single(s => s.Id == "facebook");
        Assert.False(facebook.Available);
        Assert.True(facebook.RequiresConnection);
        Assert.Equal("connect first", facebook.Note);
        // Hiding it would just make the feature look absent.
        Assert.Equal(2, described.Count);
    }

    [Fact]
    public void ById_IsCaseInsensitive() =>
        Assert.Equal("craigslist", Registry().ById("CraigsList")?.Id);
}
