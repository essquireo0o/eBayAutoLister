using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The five values Amazon needs, and the one agreement production needs.
/// </summary>
/// <remarks>
/// Every other Amazon phase was built and tested without ever reaching Amazon, because there was
/// nowhere in the app to put a client secret, a refresh token, a marketplace or a seller id. They
/// live in the same store as eBay's now, under the same keep-if-blank rule, and they reach
/// <see cref="AmazonOptions"/> through the same <c>Credentials:</c> configuration section.
/// </remarks>
public class AmazonConnectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-amazon-connect", Guid.NewGuid().ToString("N"));

    private CredentialsStore New()
    {
        Directory.CreateDirectory(_root);
        return new CredentialsStore(Path.Combine(_root, "credentials.json"));
    }

    // ── The values ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_five_values_survive_being_saved()
    {
        var store = New();

        store.Save(new CredentialsPatch
        {
            AmazonClientId      = "amzn1.application-oa2-client.abc",
            AmazonClientSecret  = "amzn1.oa2-cs.v1.secret",
            AmazonRefreshToken  = "Atzr|IwEBI-refresh",
            AmazonMarketplaceId = "ATVPDKIKX0DER",
            AmazonSellerId      = "A2EXAMPLE",
            AmazonSandbox       = false,
        });

        var fields = store.GetPublicFields();
        Assert.Equal("amzn1.application-oa2-client.abc", fields.AmazonClientId);
        Assert.Equal("ATVPDKIKX0DER", fields.AmazonMarketplaceId);
        Assert.Equal("A2EXAMPLE", fields.AmazonSellerId);
        Assert.False(fields.AmazonSandbox);
        Assert.True(fields.HasAmazonClientSecret);
        Assert.True(fields.HasAmazonRefreshToken);
    }

    [Fact]
    public void A_blank_secret_keeps_the_stored_one_rather_than_erasing_it()
    {
        var store = New();
        store.Save(new CredentialsPatch { AmazonClientSecret = "amzn1.oa2-cs.v1.secret", AmazonRefreshToken = "Atzr|keep" });

        // What the page posts when the seller edits the marketplace and leaves the secrets alone:
        // they are rendered as "(saved)" and come back empty. Erasing on empty would disconnect an
        // account every time somebody corrected a typo somewhere else on the form.
        store.Save(new CredentialsPatch { AmazonMarketplaceId = "ATVPDKIKX0DER", AmazonClientSecret = "", AmazonRefreshToken = "" });

        var fields = store.GetPublicFields();
        Assert.True(fields.HasAmazonClientSecret);
        Assert.True(fields.HasAmazonRefreshToken);
        Assert.Equal("ATVPDKIKX0DER", fields.AmazonMarketplaceId);
    }

    [Fact]
    public void The_secrets_are_never_handed_back_to_the_page()
    {
        var store = New();
        store.Save(new CredentialsPatch { AmazonClientSecret = "amzn1.oa2-cs.v1.secret", AmazonRefreshToken = "Atzr|IwEBI-refresh" });

        // Asserted against the TYPE, not against one response: a property added later would ship
        // the secret to every caller of /api/setup/fields without anybody meaning to.
        var leaks = typeof(PublicFields).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => p.Name.Contains("Secret", StringComparison.Ordinal)
                     || p.Name.Contains("RefreshToken", StringComparison.Ordinal)
                     || p.Name.Contains("Token", StringComparison.Ordinal) && !p.Name.Contains("ExpiresAt", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        Assert.True(leaks.Count == 0, "PublicFields would hand a secret to the page: " + string.Join(", ", leaks));
    }

    // ── The agreement ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Production_is_refused_until_somebody_agrees_to_it_in_this_app()
    {
        // The sandbox flag is configuration. Configuration gets copied between machines, restored
        // from backups and set by people who are not the seller — so on its own it is not consent,
        // and a real listing is not something to create on the strength of a copied setting.
        var withoutConsent = new AmazonOptions
        {
            ClientId = "id", ClientSecret = "secret", RefreshToken = "refresh",
            MarketplaceId = "ATVPDKIKX0DER", SellerId = "A2EXAMPLE", Sandbox = false,
        };

        var refusal = AmazonSubmitGuard.Check(withoutConsent);
        Assert.NotNull(refusal);
        Assert.Equal("production_refused", refusal!.Code);
    }

    [Fact]
    public void Production_is_allowed_once_it_has_been_agreed_to()
    {
        var consented = new AmazonOptions
        {
            ClientId = "id", ClientSecret = "secret", RefreshToken = "refresh",
            MarketplaceId = "ATVPDKIKX0DER", SellerId = "A2EXAMPLE", Sandbox = false,
            ProductionConsentAt = "2026-08-24 14:00:00Z",
        };

        Assert.Null(AmazonSubmitGuard.Check(consented));
    }

    [Fact]
    public void The_sandbox_never_needed_agreeing_to_because_nothing_there_is_real()
    {
        var sandbox = new AmazonOptions
        {
            ClientId = "id", ClientSecret = "secret", RefreshToken = "refresh",
            MarketplaceId = "ATVPDKIKX0DER", SellerId = "A2EXAMPLE", Sandbox = true,
        };

        Assert.Null(AmazonSubmitGuard.Check(sandbox));
    }

    [Fact]
    public void The_agreement_is_kept_with_its_date_and_can_be_withdrawn()
    {
        var store = New();

        store.Save(new CredentialsPatch { AmazonProductionConsent = true });
        var agreed = store.GetPublicFields().AmazonProductionConsentAt;
        Assert.NotEqual("", agreed);
        // A date, so "when did anybody agree to this" has an answer the day a listing nobody
        // remembers approving turns up.
        Assert.True(DateTime.TryParse(agreed, out _), $"consent was recorded as \"{agreed}\", which is not a date");

        store.Save(new CredentialsPatch { AmazonProductionConsent = false });
        Assert.Equal("", store.GetPublicFields().AmazonProductionConsentAt);
    }

    [Fact]
    public void Saving_something_else_leaves_the_agreement_alone()
    {
        var store = New();
        store.Save(new CredentialsPatch { AmazonProductionConsent = true });

        // The page posts the whole form, but every other form in the app posts only its own fields.
        store.Save(new CredentialsPatch { DefaultPostalCode = "04046" });

        Assert.NotEqual("", store.GetPublicFields().AmazonProductionConsentAt);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
