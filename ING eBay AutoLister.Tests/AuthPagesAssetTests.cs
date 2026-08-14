using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The sign-in and sign-up pages are HTML and JavaScript, and nothing in C# renders them — so
/// nothing in C# notices when the form starts posting to a path the server does not map, or when
/// the page's own idea of the minimum password length drifts away from the server's.
/// </summary>
public class AuthPagesAssetTests
{
    private static readonly string SignIn = ReadAsset("signin.html");
    private static readonly string SignUp = ReadAsset("signup.html");
    private static readonly string Js     = ReadAsset("app.js");

    [Fact]
    public void The_sign_in_form_posts_to_the_endpoint_the_server_maps()
    {
        Assert.Contains($"'{HostedAuth.SignInApi}'", SignIn, StringComparison.Ordinal);
        Assert.Contains($"'{HostedAuth.SignUpApi}'", SignUp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_pages_can_reach_each_other()
    {
        // Someone who lands on the wrong one of these has no other way out — neither page is in
        // the app's navigation, because the app is what they cannot reach yet.
        Assert.Contains($"href=\"{HostedAuth.SignUpPath}\"", SignIn, StringComparison.Ordinal);
        Assert.Contains($"href=\"{HostedAuth.SignInPath}\"", SignUp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_asks_for_a_password_the_server_will_accept()
    {
        // A form that lets a 6-character password through only to have the server refuse it is a
        // round trip and a rejection where a hint would have done.
        Assert.Contains($"minlength=\"{PasswordPolicy.MinimumLength}\"", SignUp, StringComparison.Ordinal);
        Assert.Contains($"At least {PasswordPolicy.MinimumLength} characters.", SignUp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_asks_for_a_name_and_sends_it()
    {
        // The field has to exist, be required, and actually reach the endpoint — a required input
        // whose value is never put in the request body is a form that annoys people for nothing.
        Assert.Contains("id=\"signup-name\"", SignUp, StringComparison.Ordinal);
        Assert.Contains($"maxlength=\"{UserStore.MaximumNameLength}\"", SignUp, StringComparison.Ordinal);
        Assert.Contains("name: name,", SignUp, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_field_does_not_second_guess_what_a_name_looks_like()
    {
        // No pattern attribute on it, and nothing that would refuse a name with a space, an
        // apostrophe or a letter outside a-z in it. The server does not either — see UserStore.
        var field = SignUp[SignUp.IndexOf("id=\"signup-name\"", StringComparison.Ordinal)..];
        field = field[..field.IndexOf("/>", StringComparison.Ordinal)];

        Assert.DoesNotContain("pattern=", field, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_says_who_is_signed_in_by_name()
    {
        // displayName rather than email: the server decides what to call somebody, so an account
        // from before the name existed still gets its address shown instead of a blank.
        Assert.Contains("displayName", Js, StringComparison.Ordinal);
        Assert.Contains("signed-in-as", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_in_page_will_not_bounce_anyone_off_the_product()
    {
        // ?next= comes off the wire. Anything that is not a path on this same site is ignored,
        // or the sign-in form becomes a way to land people on a copy of it somewhere else.
        Assert.Contains("charAt(0) === '/'", SignIn, StringComparison.Ordinal);
        Assert.Contains("charAt(1) !== '/'", SignIn, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_session_lands_the_seller_on_the_sign_in_page_rather_than_on_failed_to_fetch()
    {
        // One wrapper over fetch, because any of this file's calls can be the one that discovers
        // the session expired.
        Assert.Contains($"response.headers.get('{HostedAuth.AuthRequiredHeader}') === 'sign-in'", Js,
                        StringComparison.Ordinal);
        Assert.Contains($"location.replace('{HostedAuth.SignInPath}?next=' + next)", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_desktop_build_is_left_alone_by_all_of_it()
    {
        // The redirect is triggered by a header only the hosted build's own 401s carry, and the
        // sign-out button is only drawn if /api/auth/me answers — which the desktop build does not
        // map. Neither is switched on by a build flag, because the browser cannot see one.
        Assert.Contains($"passThroughFetch('{HostedAuth.MeApi}')", Js, StringComparison.Ordinal);
        Assert.Contains("if (!who.ok) return;", Js, StringComparison.Ordinal);
        Assert.Contains($"passThroughFetch('{HostedAuth.SignOutApi}'", Js, StringComparison.Ordinal);
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
