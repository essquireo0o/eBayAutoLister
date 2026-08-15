using System.Net;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Makes an <see cref="HttpClient"/> behave like a browser that has loaded a page from this app:
/// it holds the antiforgery cookie and echoes it back on every unsafe request.
/// </summary>
/// <remarks>
/// <para>
/// The tests that use it are not about CSRF. They are about sign-in, per-user data and the AI
/// quota, and every one of them posts as an ordinary signed-in seller would. Once
/// <see cref="Csrf"/> started refusing tokenless POSTs, all of them started failing with 403 —
/// not because anything they assert had broken, but because a bare <c>HttpClient</c> is not a
/// browser and never had a token to send.
/// </para>
/// <para>
/// So this fills in the one browser behaviour they were relying on without having it: it is the
/// C# translation of <c>csrf.js</c>, doing the same two things in the same order, and it keeps
/// those tests testing what they were written to test.
/// </para>
/// <para>
/// It deliberately does <em>not</em> make CSRF untestable. It only ever supplies a token it was
/// legitimately given by the server, so a test that wants to prove the refusal simply builds a
/// client without this handler — which is what <see cref="CsrfTests"/> does — and gets the same
/// 403 an attacker's page would.
/// </para>
/// </remarks>
public sealed class CsrfClientHandler : DelegatingHandler
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    private readonly CookieContainer _jar;

    public CsrfClientHandler(CookieContainer jar, HttpMessageHandler inner) : base(inner) => _jar = jar;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                 CancellationToken cancellationToken)
    {
        if (SafeMethods.Contains(request.Method.Method) || request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken);

        var token = Token(request.RequestUri);
        if (token is null)
        {
            // One safe request is enough to be issued one — exactly what csrf.js does when a form
            // is the first thing on the page to talk to the server.
            using var prime = new HttpRequestMessage(HttpMethod.Get,
                new Uri(request.RequestUri, Csrf.TokenPath));
            using var response = await base.SendAsync(prime, cancellationToken);

            token = Token(request.RequestUri);
        }

        if (token is not null) request.Headers.TryAddWithoutValidation(Csrf.HeaderName, token);

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>The token the server has issued this jar, or null before it has issued one.</summary>
    private string? Token(Uri uri) => _jar.GetCookies(uri)[Csrf.CookieName]?.Value;
}
