// Attaches the antiforgery token to every request that changes something.
//
// The server (see Csrf.cs) puts a random token in a cookie this origin's JavaScript can read, and
// refuses any POST, PUT, PATCH or DELETE that does not echo it back in X-CSRF-Token. Another site
// can make the browser send our cookies — that is what CSRF is — but the same-origin policy stops
// it reading them, so it cannot produce the header.
//
// This wraps window.fetch once instead of touching the two hundred-odd call sites across app.js,
// editor.html, signin.html and signup.html. It must load before any of them: app.js takes its own
// copy of window.fetch at parse time, and a wrapper installed afterwards would never be reached by
// the calls that copy serves.
//
// The desktop build never sends the cookie and never checks the header, so on that build every
// line below is a header nobody reads. Nothing here is conditional on which build it is.
(() => {
  const SAFE = /^(GET|HEAD|OPTIONS|TRACE)$/i;
  const COOKIE = 'ing_csrf';
  const HEADER = 'X-CSRF-Token';

  const readToken = () =>
    document.cookie.split('; ').find(c => c.startsWith(COOKIE + '='))?.slice(COOKIE.length + 1) || '';

  const passThroughFetch = window.fetch.bind(window);

  // A first visit that goes straight to a POST — a form submitted before anything else on the page
  // has talked to the server — has no cookie yet. One safe request fixes that: any GET makes the
  // server issue a token, and /api/auth/csrf is the cheapest one that exists for the purpose.
  // Awaited rather than fired off, because the POST it is for is about to need the value.
  const ensureToken = async () => {
    let token = readToken();
    if (token) return token;

    try { await passThroughFetch('/api/auth/csrf', { credentials: 'same-origin' }); }
    catch { /* Offline or the server is down. The request below will fail on its own terms. */ }

    return readToken();
  };

  window.fetch = async (input, init) => {
    const method = (init?.method || (input instanceof Request ? input.method : 'GET') || 'GET');
    if (SAFE.test(method)) return passThroughFetch(input, init);

    // Same-origin only. A request to somewhere else must not be handed this site's token, and
    // nothing in this app makes one — but "nothing does today" is not a reason to send a secret
    // to an address the caller chose.
    const url = new URL(input instanceof Request ? input.url : String(input), location.href);
    if (url.origin !== location.origin) return passThroughFetch(input, init);

    const token = await ensureToken();

    // A Request object carries its own headers and ignores an init.headers passed alongside it,
    // so it has to be rebuilt rather than decorated.
    if (input instanceof Request && !init) {
      const request = new Request(input, { headers: new Headers(input.headers) });
      request.headers.set(HEADER, token);
      return passThroughFetch(request);
    }

    const headers = new Headers((init && init.headers) || (input instanceof Request ? input.headers : undefined));
    headers.set(HEADER, token);
    return passThroughFetch(input, { ...init, headers });
  };
})();
