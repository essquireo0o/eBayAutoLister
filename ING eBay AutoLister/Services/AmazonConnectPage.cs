namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The page that connects this app to an Amazon Selling Partner account.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the Amazon phases was built and tested offline — the product-type chooser,
/// the draft-to-attribute filler, the submission payloads, the asynchronous result reading. None of
/// it had ever spoken to Amazon, because five values were missing and there was nowhere to put
/// them. This is that place.
/// </para>
/// <para>
/// It is a self-contained page rather than a section of the main app for one reason: the wwwroot
/// files were claimed by another session the day this was written, and a settings form is not worth
/// a merge race. It is served on the desktop port beside <c>/owner</c>, which it is modelled on.
/// </para>
/// <para>
/// <b>The two secrets are never rendered back.</b> A saved client secret and refresh token come
/// back as the word "saved" and post as empty, which <see cref="CredentialsStore.Save"/> reads as
/// "keep what is stored". Typing over them replaces them; leaving them alone cannot wipe them.
/// </para>
/// </remarks>
public static class AmazonConnectPage
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/amazon", (CredentialsStore store, AmazonOptions options) =>
            Results.Content(Html(store.GetPublicFields(), options), "text/html; charset=utf-8"));
    }

    private static string Html(PublicFields saved, AmazonOptions live)
    {
        // What the RUNNING process is using, which is not the same question as what is on disk:
        // AmazonOptions is built once at startup, so a value saved here reaches Amazon on the next
        // start. Saying so is the whole reason both are shown.
        var running = live.CanCall
            ? "This process is holding a complete Amazon configuration."
            : live.CallProblem?.Reason ?? "This process has no Amazon configuration.";
        var stale = live.ClientId != saved.AmazonClientId
                 || live.MarketplaceId != saved.AmazonMarketplaceId
                 || live.SellerId != saved.AmazonSellerId
                 || live.Sandbox != saved.AmazonSandbox;

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Connect Amazon — ING Listing Engine</title>
            <style>
              :root{color-scheme:dark}
              *{box-sizing:border-box;margin:0;padding:0}
              body{background:#0b0f10;color:#f7fbfb;font:16px/1.6 -apple-system,BlinkMacSystemFont,system-ui,sans-serif;
                   padding:34px 22px 60px;max-width:760px;margin:auto}
              h1{font-size:26px;letter-spacing:-.01em;margin-bottom:4px}
              h2{font-size:17px;margin:30px 0 10px}
              .sub{color:#9fb1b4;margin-bottom:22px}
              .card{background:#121819;border:1px solid #24312f;border-radius:14px;padding:18px;margin-bottom:16px}
              .state{font-weight:700;margin-bottom:4px}
              .warn{border-color:#6b4f16;background:#1a1408}
              label{display:block;margin:14px 0 4px;font-weight:600;font-size:14px}
              .hint{color:#9fb1b4;font-size:13px;font-weight:400}
              input[type=text]{width:100%;background:#0b0f10;border:1px solid #2b3a3d;border-radius:9px;
                               color:#f7fbfb;padding:12px;font:15px ui-monospace,SFMono-Regular,Menlo,monospace}
              .row{display:flex;align-items:flex-start;gap:10px;margin-top:16px}
              .row input{margin-top:5px}
              button{background:linear-gradient(145deg,#f0c453,#b67d12);color:#151006;font-weight:800;border:0;
                     border-radius:12px;padding:15px 22px;font-size:16px;cursor:pointer;margin-top:22px}
              button.ghost{background:#182021;color:#f7fbfb;border:1px solid #2b3a3d;margin-left:10px}
              code{background:#0b0f10;border:1px solid #24312f;padding:1px 6px;border-radius:6px;font-size:13px}
              ol{margin:8px 0 0 20px}li{margin-bottom:6px}
              #said{margin-top:18px;font-weight:600}
              .ok{color:#7fd8a0}.bad{color:#ff9c8a}
            </style></head><body>
              <h1>Connect Amazon</h1>
              <p class="sub">Everything else is built. These are the values it has never had.</p>

              <div class="card{{(stale ? " warn" : "")}}">
                <div class="state">Running now: {{System.Net.WebUtility.HtmlEncode(running)}}</div>
                <div class="hint">Environment: {{(live.Sandbox ? "Sandbox" : "PRODUCTION")}} · {{AmazonEndpoints.ApiHost(live.Region, live.Sandbox ? AmazonEnvironment.Sandbox : AmazonEnvironment.Production)}}
                  {{(stale ? "<br><b>Saved values differ from the ones this process started with — restart the app to use them.</b>" : "")}}
                </div>
              </div>

              <div class="card">
                <b>Where these come from</b>
                <ol>
                  <li>Seller Central &rsaquo; <b>Apps &amp; Services</b> &rsaquo; <b>Develop Apps</b>.</li>
                  <li>Your app &rsaquo; <b>LWA credentials</b> &rsaquo; <b>View</b>: the client ID and the client
                      secret. The secret is hidden until you click through — a collapsed disclosure is why the
                      value recorded in 2026-08 was a sentence rather than a key.</li>
                  <li>Same row &rsaquo; <b>Authorize</b> to grant your own app access to your own account. That
                      hands back the <b>refresh token</b>. This app never sees your Seller Central password.</li>
                  <li>Marketplace ID is <code>ATVPDKIKX0DER</code> for amazon.com. The seller ID is the
                      Merchant Token under Settings &rsaquo; Account Info.</li>
                </ol>
              </div>

              <form id="f">
                <label>Client ID <span class="hint">LWA client identifier, starts with amzn1.application-oa2-client.</span></label>
                <input type="text" id="AmazonClientId" value="{{System.Net.WebUtility.HtmlEncode(saved.AmazonClientId)}}" autocomplete="off" spellcheck="false">

                <label>Client secret <span class="hint">{{(saved.HasAmazonClientSecret ? "Saved. Leave blank to keep it." : "Not set — this is the value that has been missing.")}}</span></label>
                <input type="text" id="AmazonClientSecret" value="" placeholder="{{(saved.HasAmazonClientSecret ? "(saved)" : "amzn1.oa2-cs.v1...")}}" autocomplete="off" spellcheck="false">

                <label>Refresh token <span class="hint">{{(saved.HasAmazonRefreshToken ? "Saved. Leave blank to keep it." : "From Authorize, above. Starts with Atzr|.")}}</span></label>
                <input type="text" id="AmazonRefreshToken" value="" placeholder="{{(saved.HasAmazonRefreshToken ? "(saved)" : "Atzr|...")}}" autocomplete="off" spellcheck="false">

                <label>Marketplace ID</label>
                <input type="text" id="AmazonMarketplaceId" value="{{System.Net.WebUtility.HtmlEncode(saved.AmazonMarketplaceId)}}" placeholder="ATVPDKIKX0DER" autocomplete="off" spellcheck="false">

                <label>Seller ID <span class="hint">Merchant token.</span></label>
                <input type="text" id="AmazonSellerId" value="{{System.Net.WebUtility.HtmlEncode(saved.AmazonSellerId)}}" autocomplete="off" spellcheck="false">

                <div class="row">
                  <input type="checkbox" id="AmazonSandbox" {{(saved.AmazonSandbox ? "checked" : "")}}>
                  <label for="AmazonSandbox" style="margin:0">Use the sandbox
                    <span class="hint">Leave this on to rehearse. Be aware the sandbox replays a fixed
                      LUGGAGE dataset whatever you search for, so it can only prove the plumbing — never
                      that a real product type is right.</span></label>
                </div>

                <div class="row">
                  <input type="checkbox" id="AmazonProductionConsent" {{(saved.AmazonProductionConsentAt.Length > 0 ? "checked" : "")}}>
                  <label for="AmazonProductionConsent" style="margin:0">Submissions may create real listings on my Amazon account
                    <span class="hint">Required before anything is sent to production, and recorded with the date.
                      {{(saved.AmazonProductionConsentAt.Length > 0 ? "Agreed " + System.Net.WebUtility.HtmlEncode(saved.AmazonProductionConsentAt) + "." : "")}}</span></label>
                </div>

                <button type="submit">Save</button>
                <button type="button" class="ghost" id="check">Check connection</button>
                <div id="said"></div>
              </form>

              <script>
                const said = document.getElementById('said');
                const ids = ['AmazonClientId','AmazonClientSecret','AmazonRefreshToken','AmazonMarketplaceId','AmazonSellerId'];

                async function csrf() {
                  try {
                    const r = await fetch('/api/auth/csrf');
                    if (r.ok) return (await r.json()).token || '';
                  } catch (e) { /* desktop build has no CSRF; posting without the header is correct there */ }
                  return '';
                }

                document.getElementById('f').addEventListener('submit', async (e) => {
                  e.preventDefault();
                  said.textContent = 'Saving...'; said.className = '';
                  const body = {};
                  // A blank secret is "keep what is stored", never "erase it" — see CredentialsStore.Save.
                  for (const id of ids) { const v = document.getElementById(id).value.trim(); if (v) body[id] = v; }
                  body.AmazonSandbox = document.getElementById('AmazonSandbox').checked;
                  body.AmazonProductionConsent = document.getElementById('AmazonProductionConsent').checked;
                  try {
                    const token = await csrf();
                    const headers = { 'Content-Type': 'application/json' };
                    if (token) headers['X-CSRF-Token'] = token;
                    const res = await fetch('/api/setup/save', { method: 'POST', headers, body: JSON.stringify(body) });
                    if (!res.ok) throw new Error('the app refused it (' + res.status + ')');
                    said.textContent = 'Saved. Restart the app, then use Check connection — Amazon settings are read once at startup.';
                    said.className = 'ok';
                  } catch (err) {
                    said.textContent = 'Not saved - ' + err.message;
                    said.className = 'bad';
                  }
                });

                document.getElementById('check').addEventListener('click', async () => {
                  said.textContent = 'Asking Amazon for a token...'; said.className = '';
                  try {
                    const res = await fetch('/api/amazon/status');
                    const d = await res.json();
                    // tokenObtainable is the only field that means Amazon answered. configured means
                    // the five values are present, which is not the same thing at all.
                    said.textContent = d.tokenObtainable === true
                      ? 'Amazon issued a token. ' + (d.sandbox ? 'Sandbox.' : 'PRODUCTION.')
                      : (d.message || 'No token.') + (d.nextAction ? ' ' + d.nextAction : '');
                    said.className = d.tokenObtainable === true ? 'ok' : 'bad';
                  } catch (err) {
                    said.textContent = 'Could not read the status - ' + err.message;
                    said.className = 'bad';
                  }
                });
              </script>
            </body></html>
            """;
    }
}
