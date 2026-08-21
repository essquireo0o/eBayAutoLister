using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The seller's phone, used as the Photo Box camera, with the shutter on the desktop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a second server and not a route on the app.</b> The desktop app has no password —
/// it is one seller on a loopback port nothing outside the machine can reach, and it holds their
/// eBay refresh token. Binding <i>that</i> to the network so a phone could reach it would hand
/// every device on the café wifi a publish button. So the phone talks to this instead: a separate
/// listener that knows how to do exactly two things, serve a camera page and accept a photo, and
/// that only answers a caller who has the token printed in the QR code.
/// </para>
/// <para>
/// <b>Why HTTPS, with a certificate this app invents.</b> A browser will not open a camera on a
/// plain http origin, and there is no certificate authority on earth that will vouch for
/// 192.168.1.50. So the app makes its own, and Safari asks the seller once whether they trust it.
/// That prompt is the honest cost of a camera on a private network; the alternative is no live
/// camera at all.
/// </para>
/// <para>
/// <b>The shutter lives on the desktop.</b> The phone holds its camera open and asks this server,
/// over and over, whether a photo has been asked for. Pressing Snap in the app answers that
/// question yes. The phone takes the frame and posts it back, and it lands in the Photo Library
/// exactly where a Photo Box snap lands — so the AI Listing button downstream cannot tell which
/// camera took the picture, and does not need to.
/// </para>
/// </remarks>
public sealed class PhoneCapture(PhotoLibrary photos, ActionLog log) : IAsyncDisposable
{
    /// <summary>Where the phone connects. Deliberately not 9332: that port is the app's alone.</summary>
    public const int Port = 9443;

    /// <summary>A session that has heard nothing from a phone in this long has been abandoned.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    private string _token = "";
    private DateTimeOffset _lastSeen;
    private TaskCompletionSource<bool>? _shutter;   // completed when the desktop presses Snap
    private readonly List<string> _shots = [];      // photo-library urls, oldest first
    private bool _phoneEverConnected;

    // The viewfinder. The phone sends a small frame about once a second so the person at the
    // desk can see what they are about to photograph — without it the shutter is on one device
    // and the picture is on another, which is not a camera, it is a guess. Held in memory only:
    // a preview is not a photograph and has no business in the photo library.
    private byte[]? _preview;
    private DateTimeOffset _previewAt;

    public sealed record Status(
        bool Running, string? Url, bool PhoneConnected, int ShotCount, string[] Shots, string? QrSvg, string? Detail,
        bool HasPreview = false);

    /// <summary>The last viewfinder frame the phone sent, or null if it has not sent one lately.</summary>
    public byte[]? LatestPreview() =>
        _preview is { } p && DateTimeOffset.UtcNow - _previewAt < TimeSpan.FromSeconds(15) ? p : null;

    public Status Snapshot()
    {
        if (_app is null) return new(false, null, false, 0, [], null, null);
        var url = PublicUrl;
        return new(true, url, _phoneEverConnected && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(20),
                   _shots.Count, [.. _shots], QrCode.ToSvg(url), null, LatestPreview() is not null);
    }

    private string PublicUrl => $"https://{LocalAddress()}:{Port}/p/{_token}";

    /// <summary>
    /// The address a phone on the same wifi can reach this machine at. Picked from the interface
    /// that actually carries traffic rather than the first one Windows lists — a machine with WSL,
    /// Hyper-V and a VPN has half a dozen addresses and only one of them is on the seller's wifi.
    /// </summary>
    public static string LocalAddress()
    {
        try
        {
            // The address the OS would use to reach the internet is the address the phone shares
            // the network with. No packet is sent; connecting a UDP socket only picks a route.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            if (probe.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address.ToString();
        }
        catch { /* no route to the internet; fall back to reading the interfaces */ }

        var candidate = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        return candidate?.ToString() ?? "127.0.0.1";
    }

    public async Task<Status> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_app is not null) return Snapshot();

            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            _shots.Clear();
            _phoneEverConnected = false;
            _lastSeen = DateTimeOffset.UtcNow;

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenAnyIP(Port, o => o.UseHttps(Certificate()));
                k.Limits.MaxRequestBodySize = 25 * 1024 * 1024;   // a phone photo, with room to spare
            });

            var web = builder.Build();
            MapRoutes(web);
            await web.StartAsync(ct);
            _app = web;

            log.Add("Info", "Phone camera ready", PublicUrl);
            return Snapshot();
        }
        catch (Exception ex)
        {
            _app = null;
            log.Add("Warning", "Phone camera could not start", ex.Message);
            return new(false, null, false, 0, [], null,
                $"The phone camera could not start: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null) return;
            // Anything still waiting on the shutter is told no, so a phone hung up on mid-poll
            // does not sit there holding a request that will never be answered.
            _shutter?.TrySetResult(false);
            _shutter = null;
            await _app.StopAsync(TimeSpan.FromSeconds(2));
            await _app.DisposeAsync();
            _app = null;
        }
        catch { /* a listener that will not stop cleanly is still a listener the app is done with */ }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Presses the shutter and waits for the frame. Returns the photo-library url, or a sentence
    /// saying why not — a phone that is not holding the page open is the usual answer.
    /// </summary>
    public async Task<(string? Url, string? Error)> ShootAsync(CancellationToken ct)
    {
        if (_app is null) return (null, "The phone camera isn't running.");
        if (DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(25))
            return (null, "No phone is holding the camera page open. Scan the code again and leave that page on screen.");

        var before = _shots.Count;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutter = pending;

        // The phone's poll completes the shutter task; the upload that follows adds to _shots.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var start = DateTimeOffset.UtcNow;
            while (_shots.Count == before)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(120, deadline.Token);
                if (DateTimeOffset.UtcNow - start > TimeSpan.FromSeconds(28)) break;
            }
        }
        catch (OperationCanceledException)
        {
            return (null, "The phone didn't send a photo in time. Is the camera page still on screen?");
        }
        finally { _shutter = null; }

        return _shots.Count > before
            ? (_shots[^1], null)
            : (null, "The phone didn't send a photo. Is the camera page still open and the camera allowed?");
    }

    private void MapRoutes(WebApplication web)
    {
        // Everything is under /p/{token}. A caller without it gets a flat 404 and learns nothing:
        // this server is on the seller's network, and its whole security model is that one secret.
        bool Ok(string token) => _token.Length > 0 && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token.PadRight(64)[..64]),
            System.Text.Encoding.ASCII.GetBytes(_token.PadRight(64)[..64]));

        web.MapGet("/p/{token}", (string token) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            return Results.Content(PageHtml(token), "text/html; charset=utf-8");
        });

        // The phone asks this over and over. It answers "shoot" the moment the desktop presses
        // Snap, or "wait" after a few seconds so the request never looks hung to a phone browser.
        web.MapGet("/p/{token}/poll", async (string token, CancellationToken ct) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            var waited = 0;
            while (waited < 8000)
            {
                if (_shutter is { Task.IsCompleted: false } s)
                {
                    s.TrySetResult(true);
                    return Results.Ok(new { shoot = true });
                }
                await Task.Delay(150, ct);
                waited += 150;
            }
            return Results.Ok(new { shoot = false });
        });

        // Viewfinder frames. Small, frequent, and never written to disk.
        web.MapPost("/p/{token}/preview", async (string token, HttpRequest req) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length > 500) { _preview = ms.ToArray(); _previewAt = DateTimeOffset.UtcNow; }
            return Results.Ok();
        });

        web.MapPost("/p/{token}/photo", async (string token, HttpRequest req) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < 2000) return Results.BadRequest(new { error = "empty frame" });
            var url = await photos.SavePhotoAsync(PhotoBoxCamera.LibraryFolder, bytes, "jpg");
            _shots.Add(url);
            log.Add("Info", "Phone camera photo", url);
            return Results.Ok(new { url });
        });
    }

    /// <summary>
    /// A self-signed certificate for this machine's address, kept between runs so a phone that
    /// has already trusted it does not have to be asked again every time the app restarts.
    /// </summary>
    private static X509Certificate2 Certificate()
    {
        var path = Path.Combine(AppPaths.DataHome, "phone-camera.pfx");
        const string pass = "ing-photobox";   // guards a file already inside the user's profile
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, pass);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(7)) return existing;
            }
            catch { /* unreadable or expired: make a new one below */ }
        }

        var ip = LocalAddress();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=ING Photo Box ({ip})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ip));
        san.AddDnsName("photobox.local");
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = cert.Export(X509ContentType.Pfx, pass);
        try { File.WriteAllBytes(path, pfx); } catch { /* not being able to cache it is not fatal */ }
        return X509CertificateLoader.LoadPkcs12(pfx, pass);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static string PageHtml(string token) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <title>ING Photo Box — phone camera</title>
        <style>
          *{box-sizing:border-box;margin:0;padding:0}
          body{background:#08272b;color:#e8eef0;font:16px/1.5 -apple-system,system-ui,sans-serif;
               min-height:100vh;display:flex;flex-direction:column;align-items:center;padding:16px}
          h1{font-size:17px;color:#f0d79a;margin-bottom:2px}
          .sub{font-size:13px;color:#9fb9bd;margin-bottom:14px;text-align:center}
          video{width:100%;max-width:520px;border-radius:12px;background:#000;aspect-ratio:3/4;object-fit:cover}
          .status{margin-top:14px;font-size:15px;text-align:center;min-height:24px}
          .ok{color:#7ddba0}.busy{color:#f0d79a}.bad{color:#ff9c8a}
          button{margin-top:16px;padding:14px 22px;font-size:16px;font-weight:600;border:0;border-radius:10px;
                 background:#c79a36;color:#241703}
          .count{margin-top:12px;font-size:14px;color:#9fb9bd}
          .flash{position:fixed;inset:0;background:#fff;opacity:0;pointer-events:none;transition:opacity .18s}
        </style></head>
        <body>
          <h1>ING Photo Box</h1>
          <div class="sub">Point this phone at the item and leave this page open.<br>Press <b>Snap</b> in the app on your computer.</div>
          <video id="v" playsinline muted autoplay></video>
          <div class="status busy" id="s">Starting the camera…</div>
          <div class="count" id="c"></div>
          <button id="start">Allow camera</button>
          <canvas id="cv" style="display:none"></canvas>
          <div class="flash" id="fl"></div>
        <script>
        const TOKEN = {{"\"" + token + "\""}};
        const v = document.getElementById('v'), cv = document.getElementById('cv');
        const s = document.getElementById('s'), c = document.getElementById('c');
        const startBtn = document.getElementById('start'), fl = document.getElementById('fl');
        let taken = 0, running = false;

        function say(t, cls) { s.textContent = t; s.className = 'status ' + (cls || ''); }

        async function begin() {
          try {
            // The back camera at the best resolution it will give us — this is the whole point
            // of using the phone instead of the little board camera.
            const stream = await navigator.mediaDevices.getUserMedia({
              video: { facingMode: { ideal: 'environment' }, width: { ideal: 4032 }, height: { ideal: 3024 } },
              audio: false
            });
            v.srcObject = stream;
            await v.play();
            startBtn.style.display = 'none';
            running = true;
            say('Ready — press Snap on your computer.', 'ok');
            poll();
            preview();
          } catch (e) {
            say('The camera was not allowed: ' + e.message, 'bad');
            startBtn.style.display = '';
          }
        }

        async function shoot() {
          cv.width = v.videoWidth; cv.height = v.videoHeight;
          cv.getContext('2d').drawImage(v, 0, 0);
          fl.style.opacity = '0.85'; setTimeout(() => fl.style.opacity = '0', 120);
          const blob = await new Promise(r => cv.toBlob(r, 'image/jpeg', 0.92));
          say('Sending…', 'busy');
          const res = await fetch('/p/' + TOKEN + '/photo', { method: 'POST', body: blob });
          if (res.ok) { taken++; c.textContent = taken + (taken === 1 ? ' photo sent' : ' photos sent'); say('Ready — press Snap on your computer.', 'ok'); }
          else say('The computer would not take that photo.', 'bad');
        }

        // The desktop's viewfinder. Small and cheap on purpose — this runs once a second all the
        // time the page is open, and it is only there so the person at the computer can see what
        // they are about to photograph. The real frame, at full sensor resolution, is taken by
        // shoot() and never goes through here.
        const pv = document.createElement('canvas');
        async function preview() {
          while (running) {
            try {
              if (v.videoWidth) {
                const w = 640, h = Math.round(v.videoHeight * (640 / v.videoWidth));
                pv.width = w; pv.height = h;
                pv.getContext('2d').drawImage(v, 0, 0, w, h);
                const b = await new Promise(r => pv.toBlob(r, 'image/jpeg', 0.55));
                if (b) await fetch('/p/' + TOKEN + '/preview', { method: 'POST', body: b });
              }
            } catch (e) { /* a dropped preview frame is not worth a message */ }
            await new Promise(r => setTimeout(r, 1000));
          }
        }

        async function poll() {
          while (running) {
            try {
              const r = await fetch('/p/' + TOKEN + '/poll');
              if (!r.ok) { say('This link has expired. Scan the code again.', 'bad'); return; }
              const j = await r.json();
              if (j.shoot) await shoot();
            } catch (e) {
              say('Lost the connection to your computer — retrying…', 'bad');
              await new Promise(r => setTimeout(r, 1500));
            }
          }
        }

        startBtn.addEventListener('click', begin);
        // Safari needs a tap before it will hand over a camera, so the button is the real entry
        // point; this only helps the browsers that do not.
        begin();
        </script>
        </body></html>
        """;
}
