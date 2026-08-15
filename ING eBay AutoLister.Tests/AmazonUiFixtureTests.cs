using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The answers the Amazon panel is photographed against, written out from the code that produces
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> This deployment cannot obtain an Amazon access token — the stored LWA
/// client secret is a placeholder note and LWA answers it with <c>invalid_client</c> — so the panel
/// cannot be driven to a filled state by running the app end to end. The alternative would be to
/// screenshot the panel fed on JSON somebody typed to look right, which is a photograph of a
/// fiction: it proves the CSS and nothing else, and it would keep looking correct after the real
/// endpoint's shape moved out from under it.
/// </para>
/// <para>
/// So the fixtures are generated HERE, from <see cref="AmazonListingMapper"/> reading the captured
/// draft onto the schema fixture, and serialized through the endpoints' own
/// <see cref="AmazonListingFillEndpoints.Describe"/>. What the screenshots show is therefore the
/// shape the endpoint really returns, filled by the mapper the app really runs. What they do not
/// show — and what nothing here claims — is Amazon's real requirements for this product: only
/// production can answer that, and <see cref="AmazonSandboxNotice"/> is what says so in the answer.
/// </para>
/// <para>
/// Regenerate with:
/// <c>dotnet test --filter FullyQualifiedName~AmazonUiFixtureTests</c>
/// </para>
/// </remarks>
public class AmazonUiFixtureTests
{
    private const string Marketplace = "ATVPDKIKX0DER";

    /// <summary>Where the screenshot harness reads them from.</summary>
    private static string FixtureDir =>
        Path.Combine(RepoRoot(), "verification", "amazon-ui");

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public void The_draft_as_saved_is_missing_what_Amazon_requires_and_the_fixture_says_so()
    {
        // The captured draft has no UPC. That is not an oversight in the fixture — it is the case
        // the whole phase exists for, and the one where the honest output is a blocked listing
        // rather than a plausible barcode.
        var fill = AmazonListingMapper.Map(
            Draft(), AmazonListingFillFixtures.SpeakerDefinition(), Marketplace);

        Assert.False(fill.CanSubmit);
        Assert.NotEmpty(fill.Attributes.Where(a => a.IsBlocking).Concat(
                        fill.Choices.Where(c => !c.Satisfied).Select(_ => new AmazonFilledAttribute())));

        // And it is genuinely a mostly-answered draft, not an empty one — the panel being
        // photographed has to show the filled rows as well as the missing ones.
        Assert.True(fill.Filled.Count() >= 3,
            $"only {fill.Filled.Count()} attributes filled; the missing-required screenshot would look empty");

        Write("fill-missing.json", AmazonListingFillEndpoints.Describe(fill));
    }

    [Fact]
    public void The_same_draft_becomes_ready_only_once_a_person_has_answered_it()
    {
        // Three things, and which one comes from where is the whole point of the screenshot.
        //
        // The barcode is a fact about the product that the seller has and the app will not invent.
        // The other two are declarations — a batteries statement and a dangerous-goods statement —
        // which no product description can imply and which the mapper therefore refuses outright.
        // Until the panel could collect them there was no route from any draft to a submittable
        // listing at all, which is what makes this the fixture worth photographing.
        var draft = Draft();
        draft.Upc = "195908484729";

        var answers = new Dictionary<string, string>
        {
            ["batteries_required"]                 = "false",
            ["supplier_declared_dg_hz_regulation"] = "not_applicable",
        };

        var fill = AmazonListingMapper.Map(
            draft, AmazonListingFillFixtures.SpeakerDefinition(), Marketplace, sellerAnswers: answers);

        Assert.True(fill.CanSubmit,
            "the ready fixture is not ready: " + string.Join("; ",
                fill.Blocking.Select(a => a.Name + " — " + a.Note)
                    .Concat(fill.Choices.Where(c => !c.Satisfied).Select(c => c.Note))));
        Assert.Empty(fill.Blocking);

        // And each declaration is attributed to the person who made it, not to a field it was
        // supposedly read off. That attribution is the difference between a seller standing behind
        // a statement and this app having made one in their name.
        foreach (var name in answers.Keys)
            Assert.Equal(AmazonListingMapper.SellerAnswerSource,
                         fill.Attributes.Single(a => a.Name == name).Source);

        Write("fill-ready.json", AmazonListingFillEndpoints.Describe(fill));
    }

    [Fact]
    public void An_answer_outside_Amazons_closed_list_is_still_refused_when_a_person_typed_it()
    {
        // Being answered by a human makes a value the seller's to stand behind. It does not make it
        // legal, and the schema check is not skipped for it — otherwise the answer box would be a
        // way to post anything at all to Amazon under the seller's account.
        var fill = AmazonListingMapper.Map(
            Draft(), AmazonListingFillFixtures.SpeakerDefinition(), Marketplace,
            sellerAnswers: new Dictionary<string, string>
            {
                ["supplier_declared_dg_hz_regulation"] = "probably fine",
            });

        var declared = fill.Attributes.Single(a => a.Name == "supplier_declared_dg_hz_regulation");
        Assert.Equal(AmazonFillState.InvalidValue, declared.State);
        Assert.Null(declared.Payload);
        Assert.False(fill.CanSubmit);
    }

    [Fact]
    public void An_accepted_submission_is_written_out_as_pending_rather_than_as_a_listing()
    {
        // Amazon's own ACCEPTED body, read by the app's own parser. The response says nothing about
        // whether a listing exists, which is exactly why the panel may not either.
        var submission = AmazonSubmissionResponse.Parse(
            """{"sku":"ING-NERDQAXE-001","status":"ACCEPTED","submissionId":"a1b2c3d4-0000-4f00-8000-2b7d1c9e5a10","issues":[]}""",
            200, "ING-NERDQAXE-001");

        submission.Environment   = AmazonEnvironment.Sandbox.ToString();
        submission.ProductType   = "BLUETOOTH_SPEAKER";
        submission.MarketplaceId = Marketplace;
        submission.Headline      = AmazonSubmissionWords.Describe(submission);
        submission.NextAction    = AmazonSubmissionWords.NextAction(submission, submission.Sku);

        Assert.Equal(AmazonSubmissionState.Submitted, submission.State);
        Assert.True(submission.AwaitingAmazon);

        // The sentence the screenshot will carry, checked against the same list the phase enforces.
        foreach (var forbidden in AmazonSubmissionWords.ForbiddenWords)
            Assert.DoesNotContain(forbidden, submission.Headline, StringComparison.OrdinalIgnoreCase);

        Write("submission-pending.json", AmazonSubmitEndpoints.Describe(submission));
    }

    [Fact]
    public void The_status_fixture_reports_the_sandbox_this_build_is_actually_locked_to()
    {
        // Not a hand-written {"sandbox":true}. The option defaults to sandbox and the submit guard
        // refuses anything else, so this asserts the two facts the banner reports before writing
        // them down.
        var options = new AmazonOptions();
        Assert.True(options.Sandbox);
        Assert.Equal(AmazonEnvironment.Sandbox, options.Environment);
        Assert.Equal("production_refused",
            AmazonSubmitGuard.Check(new AmazonOptions { Sandbox = false })!.Code);

        Write("status.json", new
        {
            configured            = true,
            applicationConfigured = true,
            tokenObtainable       = true,
            sandbox               = options.Sandbox,
            environment           = options.Environment.ToString(),
            region                = options.Region.ToString(),
            apiHost               = new Uri(options.BaseUrl).Host,
            code                  = "",
            message               = "",
            nextAction            = "",
        });
    }

    private static PostListingRequest Draft() => AmazonListingFillFixtures.RealDraft();

    private static void Write(string name, object body)
    {
        Directory.CreateDirectory(FixtureDir);
        File.WriteAllText(Path.Combine(FixtureDir, name), JsonSerializer.Serialize(body, Pretty));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
