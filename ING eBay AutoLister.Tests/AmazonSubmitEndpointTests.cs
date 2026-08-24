using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

// The pure half is proven next door. What is left is the half that cannot be pure — the request
// this app actually puts on the wire — and this deployment cannot make it against Amazon, because
// the stored LWA client secret is a 203-character note rather than a secret and the exchange comes
// back 401 invalid_client. (Re-measured 2026-08-15; see verification/amazon-phase4-submission.txt.)
//
// So Amazon is stood in for, at the HTTP layer, by a stub that answers with Amazon's own documented
// bodies. That is a weaker claim than a real submission and it is worth being exact about which part
// is weaker: everything from the endpoint down to the bytes of the request is the real code path —
// the real route, the real service, the real token attachment, the real URL and the real payload —
// and only the far end is stood in for. What these cases therefore prove is that the app SENDS the
// right thing and READS the answer correctly. What they cannot prove is that Amazon agrees the
// payload is valid, and no test written on this machine can prove that.
public class AmazonSubmitEndpointTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── (a) An offer on an existing ASIN — the common case, done first ────────

    [Fact]
    public async Task An_offer_on_an_existing_ASIN_is_sent_as_Amazon_documents_it()
    {
        using var server = await Server.StartAsync(Amazon.Accepted);

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        Assert.Equal(AmazonSubmissionState.Submitted, answer.GetProperty("state").GetString());
        Assert.True(answer.GetProperty("awaitingAmazon").GetBoolean());

        var sent = Assert.Single(server.Amazon.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.StartsWith("https://sandbox.sellingpartnerapi-na.amazon.com/listings/2021-08-01/items/",
            sent.Url, StringComparison.Ordinal);

        // The token goes on the request. Without it every one of these is the 403 this deployment
        // gets today, and a stub that did not check would hide that.
        Assert.Equal("Atza|StubAccessToken", sent.AccessToken);

        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("LISTING_OFFER_ONLY", body.GetProperty("requirements").GetString());
        Assert.Equal("B08N5WRWNW", body.GetProperty("attributes")
            .GetProperty("merchant_suggested_asin")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_200_that_says_INVALID_comes_back_as_a_rejection()
    {
        using var server = await Server.StartAsync(Amazon.Invalid);

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        // The HTTP status was 200. Anything that read that as the verdict would be telling the
        // seller their listing went up.
        Assert.Equal(200, answer.GetProperty("call").GetProperty("httpStatus").GetInt32());
        Assert.Equal(AmazonSubmissionState.Rejected, answer.GetProperty("state").GetString());
        Assert.False(answer.GetProperty("awaitingAmazon").GetBoolean());
        Assert.Equal(1, answer.GetProperty("counts").GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task The_answer_never_offers_a_field_that_means_published()
    {
        using var server = await Server.StartAsync(Amazon.Accepted);

        var raw = await server.PostRawAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        // A UI binds to whatever is there. If this response carried a "success" or a "published",
        // somebody would eventually bind to it and be wrong in the seller's favour.
        foreach (var forbidden in new[] { "published", "\"live\"", "success" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("awaitingAmazon", raw, StringComparison.Ordinal);
    }

    // ── The other half of the truth ───────────────────────────────────────────

    [Fact]
    public async Task Asking_what_became_of_a_SKU_surfaces_a_rejection_the_submission_could_not()
    {
        using var server = await Server.StartAsync(Amazon.Accepted, Amazon.FailedAfterProcessing);

        // The submission was accepted with an empty issue list.
        var submitted = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());
        Assert.Equal(AmazonSubmissionState.Submitted, submitted.GetProperty("state").GetString());
        Assert.Equal(0, submitted.GetProperty("counts").GetProperty("issues").GetInt32());

        // Minutes later, the same SKU carries the error that killed it. This is the ONLY place that
        // fact exists — it was never returned to the caller who submitted.
        var state = await server.GetAsync($"{AmazonSubmitEndpoints.StatePath}?sku=ING-TEST-001");

        Assert.True(state.GetProperty("hasErrors").GetBoolean());
        Assert.False(state.GetProperty("amazonSaysBuyable").GetBoolean());
        Assert.Contains("90220", state.GetProperty("headline").GetString()!, StringComparison.Ordinal);

        var issue = state.GetProperty("issues")[0];
        Assert.Equal("ERROR", issue.GetProperty("severity").GetString());
        Assert.Equal("merchant_suggested_asin", issue.GetProperty("attributeNames")[0].GetString());
    }

    [Fact]
    public async Task A_missing_SKU_is_reported_as_a_rejection_leaving_nothing_behind()
    {
        using var server = await Server.StartAsync(Amazon.NotFound);

        var state = await server.GetAsync($"{AmazonSubmitEndpoints.StatePath}?sku=ING-TEST-001");

        Assert.Equal("not_found", state.GetProperty("status").GetString());
        Assert.Contains("rejection", state.GetProperty("headline").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_state_call_asks_Amazon_for_the_issues()
    {
        using var server = await Server.StartAsync(Amazon.Buyable);

        var state = await server.GetAsync($"{AmazonSubmitEndpoints.StatePath}?sku=ING-TEST-001");

        Assert.Contains("includedData=issues%2Csummaries",
            server.Amazon.Requests.Last().Url, StringComparison.Ordinal);

        // Amazon's own words, kept plural.
        var statuses = state.GetProperty("amazonStatuses").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("BUYABLE", statuses);
        Assert.Contains("DISCOVERABLE", statuses);
    }

    // ── What never reaches the wire ───────────────────────────────────────────

    [Fact]
    public async Task An_offer_missing_a_fact_is_refused_before_anything_is_sent()
    {
        using var server = await Server.StartAsync(Amazon.Accepted);

        var offer = Offer();
        offer.Quantity = null;

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, offer);

        Assert.Equal(AmazonSubmissionState.Blocked, answer.GetProperty("state").GetString());
        Assert.Empty(server.Amazon.Requests);

        // "blocked" alone is not actionable. The seller has to be told which fact and why it is not
        // being guessed for them.
        Assert.Contains("out of stock", answer.GetProperty("nextAction").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_production_deployment_cannot_submit_until_the_seller_has_agreed_to_it()
    {
        // Sandbox off is a CONFIGURATION value: it travels in backups, gets copied between
        // machines, and can be set by somebody who is not the person answerable for the listing.
        // On its own it has never been permission to create one.
        using var server = await Server.StartAsync(Amazon.Accepted,
            extra: [("Credentials:AmazonSandbox", "false")]);

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        Assert.Equal(AmazonSubmissionState.NotConfigured, answer.GetProperty("state").GetString());
        Assert.Empty(server.Amazon.Requests);
        Assert.Contains("real listing", answer.GetProperty("headline").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_production_deployment_submits_once_the_seller_has_agreed()
    {
        // The other half, and the reason the refusal above is a gate rather than a wall: a seller
        // who has ticked the box on the Amazon page — which records WHEN they ticked it — is
        // asking for a real listing, and the app stops arguing and sends it.
        using var server = await Server.StartAsync(Amazon.Accepted,
            extra: [("Credentials:AmazonSandbox", "false"),
                    ("Credentials:AmazonProductionConsentAt", "2026-08-24 14:00:00Z")]);

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        Assert.NotEqual(AmazonSubmissionState.NotConfigured, answer.GetProperty("state").GetString());
        Assert.NotEmpty(server.Amazon.Requests);
    }

    [Fact]
    public async Task A_new_product_is_not_sent_when_the_fill_says_it_cannot_go()
    {
        using var server = await Server.StartAsync(Amazon.Accepted);

        // No schema can be read here, so the fill is empty — and an empty fill must not become an
        // empty submission. Asking Amazon to adjudicate something this app already knows the answer
        // to costs a rejection against the seller's account.
        var answer = await server.PostAsync(AmazonSubmitEndpoints.ProductPath, new AmazonProductSubmitRequest
        {
            Sku = "ING-TEST-002",
            Quantity = 1,
            Draft = new AmazonFillRequest { Title = "Bitaxe NerdQaxe++ Bitcoin Solo Miner", Brand = "NerdQaxe" },
        });

        Assert.Equal(AmazonSubmissionState.Blocked, answer.GetProperty("state").GetString());

        // Asking Amazon what the product type requires is a READ, and it happened — that is how the
        // app knows the fill cannot go. What must not happen is the write, and the distinction is
        // the point: this path is allowed to look things up and is not allowed to leave anything
        // behind on the seller's account.
        Assert.DoesNotContain(server.Amazon.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Without_credentials_nothing_is_sent_and_nothing_is_claimed()
    {
        using var server = await Server.StartAsync(Amazon.Accepted, credentials: false);

        var answer = await server.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());

        Assert.Equal(AmazonSubmissionState.NotConfigured, answer.GetProperty("state").GetString());
        Assert.Empty(server.Amazon.Requests);
    }

    [Fact]
    public async Task The_report_carries_the_exchange_and_never_a_credential()
    {
        using var server = await Server.StartAsync(Amazon.Accepted);

        var (contentType, text) = await server.PostTextAsync(AmazonSubmitEndpoints.OfferReportPath, Offer());

        Assert.Equal("text/plain", contentType);
        Assert.Contains("THE EXCHANGE", text, StringComparison.Ordinal);

        // The seller ID identifies the account and a report is a thing people paste into messages.
        Assert.DoesNotContain("A1SELLERTOKEN", text, StringComparison.Ordinal);
        Assert.Contains("{sellerId}", text, StringComparison.Ordinal);

        // The token, the secret and the refresh token never appear anywhere.
        Assert.DoesNotContain("StubAccessToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NotARealSecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NotARealRefreshToken", text, StringComparison.Ordinal);
    }

    // ── The acceptance artefact ───────────────────────────────────────────────

    [Fact]
    public async Task The_submission_report_shows_both_an_acceptance_and_a_rejection()
    {
        var lines = new StringBuilder();

        void Section(string title, string body)
        {
            lines.AppendLine().AppendLine(title).AppendLine(new string('=', title.Length))
                 .AppendLine().AppendLine(body).AppendLine();
        }

        using (var accepted = await Server.StartAsync(Amazon.Accepted))
        {
            var (_, text) = await accepted.PostTextAsync(AmazonSubmitEndpoints.OfferReportPath, Offer());
            Section("(a) OFFER ON AN EXISTING ASIN - Amazon answers ACCEPTED", text);
        }

        using (var invalid = await Server.StartAsync(Amazon.Invalid))
        {
            var (_, text) = await invalid.PostTextAsync(AmazonSubmitEndpoints.OfferReportPath, Offer());
            Section("A REJECTION THAT ARRIVES INSIDE AN HTTP 200", text);
        }

        using (var later = await Server.StartAsync(Amazon.Accepted, Amazon.FailedAfterProcessing))
        {
            await later.PostAsync(AmazonSubmitEndpoints.OfferPath, Offer());
            var (_, text) = await later.GetTextAsync(
                $"{AmazonSubmitEndpoints.StateReportPath}?sku=ING-TEST-001");
            Section("THE ISSUE CHECK - the rejection that the submission response could not carry", text);
        }

        using (var real = await Server.StartAsync(Amazon.RealSandbox403))
        {
            var (_, text) = await real.PostTextAsync(AmazonSubmitEndpoints.OfferReportPath, Offer());
            Section("THE BODY THIS DEPLOYMENT ACTUALLY GETS (measured 2026-08-15)", text);
        }

        output.WriteLine(lines.ToString());

        // The artefact is only worth keeping if it shows the thing the phase is about.
        var all = lines.ToString();
        Assert.Contains("Submitted, awaiting Amazon", all, StringComparison.Ordinal);
        Assert.Contains("Amazon rejected this submission", all, StringComparison.Ordinal);
        Assert.Contains("rejected this listing after processing", all, StringComparison.Ordinal);
    }

    private static AmazonOfferRequest Offer() => new()
    {
        Asin = "B08N5WRWNW",
        Sku = "ING-TEST-001",
        Condition = "new_new",
        ConditionNote = "Sealed retail box.",
        Price = 549.99m,
        Quantity = 3,
    };

    // ── Amazon's own bodies ───────────────────────────────────────────────────

    /// <summary>
    /// The answers Amazon gives, as Amazon shapes them.
    /// </summary>
    /// <remarks>
    /// <see cref="RealSandbox403"/> is not a reconstruction — it is the verbatim body
    /// <c>sandbox.sellingpartnerapi-na.amazon.com</c> returned on 2026-08-15 to the exact PUT this
    /// app builds, sent without a token. It is here so the one response this deployment can actually
    /// obtain from Amazon is put through the same code path as the ones it cannot.
    /// </remarks>
    private static class Amazon
    {
        public static readonly (int Status, string Body) Accepted =
            (200, """{"sku":"ING-TEST-001","status":"ACCEPTED","submissionId":"7b1e4f92","issues":[]}""");

        public static readonly (int Status, string Body) Invalid =
            (200, """
                {"sku":"ING-TEST-001","status":"INVALID","submissionId":"7b1e4f93",
                 "issues":[{"code":"4000001",
                            "message":"The value 'used_god' for 'condition_type' is not one of the accepted values.",
                            "severity":"ERROR","attributeNames":["condition_type"]}]}
                """);

        public static readonly (int Status, string Body) FailedAfterProcessing =
            (200, """
                {"sku":"ING-TEST-001",
                 "summaries":[{"marketplaceId":"ATVPDKIKX0DER","productType":"PRODUCT","status":[]}],
                 "issues":[{"code":"90220",
                            "message":"'merchant_suggested_asin' does not match a product in the Amazon catalog.",
                            "severity":"ERROR","attributeNames":["merchant_suggested_asin"]},
                           {"code":"18027","message":"A main product image is recommended.",
                            "severity":"WARNING","attributeNames":["main_product_image_locator"]}]}
                """);

        public static readonly (int Status, string Body) Buyable =
            (200, """
                {"sku":"ING-TEST-001",
                 "summaries":[{"marketplaceId":"ATVPDKIKX0DER","asin":"B08N5WRWNW","productType":"PRODUCT",
                               "conditionType":"new_new","status":["BUYABLE","DISCOVERABLE"],
                               "itemName":"Echo Dot (4th Gen)"}],
                 "issues":[]}
                """);

        public static readonly (int Status, string Body) NotFound =
            (404, """
                {"errors":[{"code":"NOT_FOUND","message":"The requested listing was not found.","details":""}]}
                """);

        public static readonly (int Status, string Body) RealSandbox403 =
            (403, """
                {
                  "errors": [
                    {
                      "code": "Unauthorized",
                      "message": "Access to requested resource is denied.",
                      "details": "Access token is missing in the request header."
                    }
                  ]
                }
                """);
    }

    // ── A server, and an Amazon that is not Amazon ────────────────────────────

    private sealed record Sent(HttpMethod Method, string Url, string Body, string AccessToken);

    /// <summary>
    /// Stands in for Amazon at the HTTP layer, and records what the app sent.
    /// </summary>
    /// <remarks>
    /// Substituted through <see cref="IHttpClientFactory"/> rather than by pointing the app at a
    /// local URL, so that <see cref="AmazonEndpoints"/> keeps choosing the host. The rule that no
    /// caller can address production while the app reports sandbox is the one invariant on this path
    /// worth more than the test — a test hook that let a host be overridden would be the very hole
    /// that rule exists to close.
    /// </remarks>
    private sealed class StubAmazon : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _spApi;

        public StubAmazon(IEnumerable<(int Status, string Body)> spApi) => _spApi = new(spApi);

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            // Login with Amazon. A real host and a real exchange in production; here, the thing that
            // lets the SP-API request happen at all.
            if (url.StartsWith(AmazonEndpoints.LwaTokenUrl, StringComparison.Ordinal))
                return Json(200, """
                    {"access_token":"Atza|StubAccessToken","token_type":"bearer","expires_in":3600}
                    """);

            var body = request.Content is null
                ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new Sent(request.Method, url, body,
                request.Headers.TryGetValues("x-amz-access-token", out var token)
                    ? token.FirstOrDefault() ?? "" : ""));

            var (status, answer) = _spApi.Count > 0 ? _spApi.Dequeue() : (500, "{}");
            return Json(status, answer);
        }

        private static HttpResponseMessage Json(int status, string body)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("x-amzn-RequestId", "11111111-2222-3333-4444-555555555555");
            return response;
        }
    }

    private sealed class StubFactory(StubAmazon handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Server : IDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly string _root;

        private Server(WebApplication app, HttpClient client, string root, StubAmazon amazon)
        {
            _app = app;
            _client = client;
            _root = root;
            Amazon = amazon;
        }

        public StubAmazon Amazon { get; }

        public static Task<Server> StartAsync(params (int Status, string Body)[] answers) =>
            StartAsync(answers, credentials: true, extra: null);

        public static Task<Server> StartAsync(
            (int Status, string Body) first, (int Status, string Body) second) =>
            StartAsync([first, second], credentials: true, extra: null);

        public static Task<Server> StartAsync(
            (int Status, string Body) only, bool credentials) =>
            StartAsync([only], credentials, extra: null);

        public static Task<Server> StartAsync(
            (int Status, string Body) only, (string Key, string Value)[] extra) =>
            StartAsync([only], credentials: true, extra);

        private static async Task<Server> StartAsync(
            (int Status, string Body)[] answers, bool credentials, (string Key, string Value)[]? extra)
        {
            var root = Path.Combine(Path.GetTempPath(), "amazon-submit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var settings = new Dictionary<string, string?>();
            if (credentials)
            {
                settings["Credentials:AmazonClientId"]      = "amzn1.application-oa2-client.stub";
                settings["Credentials:AmazonClientSecret"]  = "amzn1.oa2-cs.v1.NotARealSecret";
                settings["Credentials:AmazonRefreshToken"]  = "Atzr|NotARealRefreshToken";
                settings["Credentials:AmazonMarketplaceId"] = "ATVPDKIKX0DER";
                settings["Credentials:AmazonSellerId"]      = "A1SELLERTOKEN";
            }
            foreach (var (key, value) in extra ?? []) settings[key] = value;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = "Production",
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(settings);

            var amazon = new StubAmazon(answers);
            builder.Services.AddSingleton<IHttpClientFactory>(new StubFactory(amazon));
            builder.Services.AddSingleton<ActionLog>();
            builder.Services.AddSingleton(AmazonOptions.FromConfiguration(builder.Configuration));
            builder.Services.AddSingleton<AmazonService>();
            builder.Services.AddSingleton(new AmazonSchemaCache(Path.Combine(root, "schemas")));
            builder.Services.AddSingleton<AmazonProductTypeService>();
            builder.Services.AddSingleton<AmazonListingSubmitService>();

            var app = builder.Build();
            AmazonSubmitEndpoints.Map(app);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            return new Server(app, new HttpClient { BaseAddress = new Uri(address) }, root, amazon);
        }

        public async Task<JsonElement> PostAsync<T>(string path, T request) =>
            JsonDocument.Parse(await PostRawAsync(path, request)).RootElement.Clone();

        public async Task<string> PostRawAsync<T>(string path, T request)
        {
            var response = await _client.PostAsJsonAsync(path, request);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<(string ContentType, string Text)> PostTextAsync<T>(string path, T request)
        {
            var response = await _client.PostAsJsonAsync(path, request);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return (response.Content.Headers.ContentType?.MediaType ?? "",
                    await response.Content.ReadAsStringAsync());
        }

        public async Task<JsonElement> GetAsync(string path)
        {
            var response = await _client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        public async Task<(string ContentType, string Text)> GetTextAsync(string path)
        {
            var response = await _client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return (response.Content.Headers.ContentType?.MediaType ?? "",
                    await response.Content.ReadAsStringAsync());
        }

        public void Dispose()
        {
            _client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
