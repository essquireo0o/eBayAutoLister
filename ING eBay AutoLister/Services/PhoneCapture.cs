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
/// <summary>What the phone said its camera can do. Sent once, on arrival.</summary>
public sealed record PhoneCaps(bool Torch, int Width, int Height);

/// <summary>What the desktop's camera controls send. Null means "leave that one alone".</summary>
public sealed record PhoneSettingsRequest(double? Zoom, bool? Torch);

public sealed class PhoneCapture(PhotoLibrary photos, ActionLog log) : IAsyncDisposable
{
    /// <summary>Where the phone connects. Deliberately not 9332: that port is the app's alone.</summary>
    public const int Port = 9443;

    /// <summary>
    /// A session that has heard nothing from a phone in this long has been abandoned, and the
    /// port closes. A phone that is actually there says something every second, so this can only
    /// fire on a session the seller has walked away from — never on one in use.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    private string _token = "";
    private DateTimeOffset _lastSeen;
    private TaskCompletionSource<bool>? _shutter;   // completed when the desktop presses Snap
    private readonly List<string> _shots = [];      // photo-library urls, oldest first
    private bool _phoneEverConnected;

    // What the desktop has asked the camera to do. The phone applies these; it does not decide
    // them. A counter rather than a timestamp because the phone's only question is "has this
    // changed since the values I am already using" — and the poll answers the moment it has, so
    // a nudge on the zoom slider reaches the lens in well under a second.
    private double _zoom = 1.0;
    private bool _torch;
    private int _settingsSeq;

    // What this phone said its camera can do, on arrival. The torch is the one that really
    // varies — Android hands a web page the LED, iOS does not — and a button that cannot work
    // must not be on the screen. Zoom is never gated: a crop of a twelve-megapixel frame is a
    // zoom on any phone ever made, whether or not its lens will move.
    private bool _canTorch;
    private int _capWidth, _capHeight;

    // The viewfinder. The phone sends a small frame about once a second so the person at the
    // desk can see what they are about to photograph — without it the shutter is on one device
    // and the picture is on another, which is not a camera, it is a guess. Held in memory only:
    // a preview is not a photograph and has no business in the photo library.
    private byte[]? _preview;
    private DateTimeOffset _previewAt;
    // Qualified: the desktop build also has WinForms in scope, and its Timer is a different animal.
    private System.Threading.Timer? _idleSweep;

    // ── Video ────────────────────────────────────────────────────────────────────
    // A recording is not a photograph: it never goes into the photo library, whose callers
    // all assume they can render what they are handed, and it is never offered to the AI
    // listing. eBay does not take a video file on a listing the way it takes photos.
    private readonly List<string> _videos = [];
    private string _command = "";              // "shoot" | "record-start" | "record-stop"
    private bool _recording;

    /// <summary>Long enough for a turn around a product; short enough to cross a wifi.</summary>
    public const int MaxVideoSeconds = 60;

    /// <summary>Videos live beside the photos, in their own folder, under the same data home.</summary>
    private static string VideoDir => Path.Combine(AppPaths.DataHome, "photos", "photo-box-video");

    public sealed record Status(
        bool Running, string? Url, bool PhoneConnected, int ShotCount, string[] Shots, string? QrSvg, string? Detail,
        bool HasPreview = false, bool PhoneWasConnected = false,
        bool Recording = false, string[]? Videos = null, int MaxVideoSeconds = 0,
        double Zoom = 1.0, bool Torch = false, bool CanTorch = false,
        int CaptureWidth = 0, int CaptureHeight = 0);

    /// <summary>The last viewfinder frame the phone sent, or null if it has not sent one lately.</summary>
    public byte[]? LatestPreview() =>
        _preview is { } p && DateTimeOffset.UtcNow - _previewAt < TimeSpan.FromSeconds(15) ? p : null;

    public Status Snapshot()
    {
        if (_app is null) return new(false, null, false, 0, [], null, null);
        var url = PublicUrl;
        return new(true, url, _phoneEverConnected && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(20),
                   _shots.Count, [.. _shots], QrCode.ToSvg(url), null, LatestPreview() is not null,
                   _phoneEverConnected, _recording, [.. _videos], MaxVideoSeconds,
                   _zoom, _torch, _canTorch, _capWidth, _capHeight);
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

            _idleSweep?.Dispose();
            _idleSweep = new System.Threading.Timer(_ =>
            {
                if (_app is null || DateTimeOffset.UtcNow - _lastSeen <= IdleTimeout) return;
                log.Add("Info", "Phone camera closed", $"no phone for {IdleTimeout.TotalMinutes:0} minutes");
                _ = StopAsync();
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

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
            _idleSweep?.Dispose();
            _idleSweep = null;
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

    /// <summary>Tells the phone to start recording. Returns why not, or null.</summary>
    public string? StartRecording()
    {
        if (_app is null) return "The phone camera isn't running.";
        if (DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(25))
            return "No phone is holding the camera page open.";
        if (_recording) return null;
        _command = "record-start";
        _recording = true;
        return null;
    }

    /// <summary>Tells the phone to stop. The file arrives on its own a moment later.</summary>
    public void StopRecording()
    {
        if (!_recording) return;
        _command = "record-stop";
        _recording = false;
    }

    /// <summary>
    /// Point the camera differently. Only the values sent change, so the zoom slider cannot
    /// switch the light off by omission.
    /// </summary>
    /// <remarks>
    /// Clamped to 1–8×: past that a phone is inventing pixels, and a viewfinder that offered 30×
    /// would be promising something no phone in a photo box can deliver.
    /// </remarks>
    public Status Apply(double? zoom, bool? torch)
    {
        if (zoom is { } z) _zoom = Math.Clamp(z, 1.0, 8.0);
        if (torch is { } t) _torch = t && _canTorch;
        _settingsSeq++;
        return Snapshot();
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
        web.MapGet("/p/{token}/poll", async (string token, int? seq, CancellationToken ct) =>
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
                    // `shoot` is still sent for a page that predates recording, so an old
                    // tab left open keeps taking photographs instead of going quiet.
                    return Results.Ok(new { shoot = true, command = "shoot", maxVideoSeconds = MaxVideoSeconds,
                                            seq = _settingsSeq, zoom = _zoom, torch = _torch });
                }
                if (_command.Length > 0)
                {
                    var cmd = _command;
                    _command = "";
                    return Results.Ok(new { shoot = false, command = cmd, maxVideoSeconds = MaxVideoSeconds,
                                            seq = _settingsSeq, zoom = _zoom, torch = _torch });
                }
                // A zoom nudge must reach the lens now, not when this long-poll happens to time
                // out. The phone tells us which settings it is already using.
                if (seq is { } known && known != _settingsSeq)
                    return Results.Ok(new { shoot = false, command = "", maxVideoSeconds = MaxVideoSeconds,
                                            seq = _settingsSeq, zoom = _zoom, torch = _torch });
                await Task.Delay(150, ct);
                waited += 150;
            }
            return Results.Ok(new { shoot = false, command = "", maxVideoSeconds = MaxVideoSeconds,
                                    seq = _settingsSeq, zoom = _zoom, torch = _torch });
        });

        // A finished recording. Saved beside the photos but never IN the photo library:
        // its callers all assume they can render what they are handed.
        web.MapPost("/p/{token}/video", async (string token, HttpRequest req) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _recording = false;
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < 5000) return Results.BadRequest(new { error = "empty recording" });

            var ext = (req.ContentType ?? "").Contains("webm", StringComparison.OrdinalIgnoreCase) ? "webm" : "mp4";
            Directory.CreateDirectory(VideoDir);
            var name = $"{Guid.NewGuid():N}.{ext}";
            await File.WriteAllBytesAsync(Path.Combine(VideoDir, name), bytes);
            var url = $"/photo-box-video/{name}";
            _videos.Add(url);
            log.Add("Info", "Phone camera video", $"{url} ({bytes.Length / 1048576.0:0.0} MB)");
            return Results.Ok(new { url });
        });

        // What this phone's camera can do, sent once when it starts. Only the torch really
        // varies; everything else the desktop offers works on any phone.
        web.MapPost("/p/{token}/caps", async (string token, HttpRequest req) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            try
            {
                var caps = await System.Text.Json.JsonSerializer.DeserializeAsync<PhoneCaps>(
                    req.Body, new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web));
                if (caps is not null)
                {
                    _canTorch = caps.Torch;
                    _capWidth = caps.Width;
                    _capHeight = caps.Height;
                }
            }
            catch { /* a phone that cannot describe itself still takes photographs */ }
            return Results.Ok(new { ok = true });
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
            var url = await photos.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, bytes, "jpg");
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
          .awake{margin-top:6px;font-size:12px;color:#6f8b90}
          .awake.warn{color:#f0d79a}
          .zoomwrap{display:flex;align-items:center;gap:10px;width:100%;max-width:520px;margin-top:12px}
          .zoomwrap input[type=range]{flex:1}
          .zlab{font-size:12px;color:#9fb9bd}
          .zval{font-size:13px;color:#f0d79a;min-width:56px;text-align:right}
          .flash{position:fixed;inset:0;background:#fff;opacity:0;pointer-events:none;transition:opacity .18s}
          .zoomrow{display:flex;align-items:center;gap:10px;width:100%;max-width:520px;margin-top:14px}
          .zoomrow input{flex:1;accent-color:#c79a36}
          .mini{margin:0;padding:6px 14px;font-size:18px;background:#123c42;color:#e8eef0}
          .zoom{margin-top:6px;font-size:13px;color:#9fb9bd;min-height:18px}
        </style></head>
        <body>
          <h1>ING Photo Box</h1>
          <div class="sub">Point this phone at the item and leave this page open.<br>Press <b>Snap</b> in the app on your computer.</div>
          <video id="v" playsinline muted autoplay></video>
          <div class="zoomwrap" id="zoomwrap" style="display:none">
            <span class="zlab">zoom</span>
            <input type="range" id="zoom" min="0.5" max="3" step="0.1" value="0.5">
            <span class="zval" id="zoomval">widest</span>
          </div>
          <div class="status busy" id="s">Starting the camera…</div>
          <div class="count" id="c"></div>
          <div class="awake" id="awake"></div>
          <div class="zoomrow">
            <button class="mini" id="zout" type="button" aria-label="Zoom out">−</button>
            <input id="zr" type="range" min="1" max="8" step="0.1" value="1" aria-label="Zoom">
            <button class="mini" id="zin" type="button" aria-label="Zoom in">+</button>
          </div>
          <div class="zoom" id="z"></div>
          <button id="shot" type="button">📸 Take the photo</button>
          <button id="start">Allow camera</button>
          <canvas id="cv" style="display:none"></canvas>
          <div class="flash" id="fl"></div>
        <script>
        const TOKEN = {{"\"" + token + "\""}};
        const v = document.getElementById('v'), cv = document.getElementById('cv');
        const s = document.getElementById('s'), c = document.getElementById('c');
        const startBtn = document.getElementById('start'), fl = document.getElementById('fl');
        const awakeEl = document.getElementById('awake');
        let taken = 0, running = false, wakeLock = null;
        let settingsSeq = -1;              // which settings this page has already applied
        const zEl = document.getElementById('z');

        // Pinch on the picture, the way a camera app works. The desk's slider and this end up in
        // the same place: whoever moved last wins, and the viewfinder shows it either way.
        let pinchFrom = 0, pinchZoom = 1;
        const spread = t => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
        document.addEventListener('touchstart', e => {
          if (e.touches.length === 2) { pinchFrom = spread(e.touches); pinchZoom = zoom; }
        }, { passive: true });
        document.addEventListener('touchmove', e => {
          if (e.touches.length === 2 && pinchFrom > 0) {
            e.preventDefault();
            applyZoom(pinchZoom * (spread(e.touches) / pinchFrom));
          }
        }, { passive: false });
        document.addEventListener('touchend', () => { pinchFrom = 0; }, { passive: true });

        function say(t, cls) { s.textContent = t; s.className = 'status ' + (cls || ''); }

        // ── Keeping the phone awake ────────────────────────────────────────────────
        // An iPhone locks its screen after thirty seconds, and a locked screen suspends
        // this page: the camera stops, the shutter on the computer finds nobody home, and
        // the seller is left pressing Snap at a dead link. The Screen Wake Lock API is the
        // supported way to say "I am a camera, stay on" — iOS has had it since 16.4. It is
        // dropped every time the page is backgrounded, so it has to be taken again on the
        // way back rather than once at the start.
        async function keepAwake() {
          if (!('wakeLock' in navigator)) {
            awakeEl.textContent = 'This phone cannot hold its own screen awake — set Settings → Display & Brightness → Auto-Lock to Never while you photograph.';
            awakeEl.className = 'awake warn';
            return;
          }
          try {
            wakeLock = await navigator.wakeLock.request('screen');
            awakeEl.textContent = 'Screen held awake while this page is open.';
            awakeEl.className = 'awake';
            wakeLock.addEventListener('release', () => { wakeLock = null; });
          } catch (e) {
            awakeEl.textContent = 'Could not hold the screen awake (' + e.name + '). Set Auto-Lock to Never while you photograph.';
            awakeEl.className = 'awake warn';
          }
        }

        // Coming back from a lock, an app switch or an incoming call: retake the wake lock,
        // and restart the camera if the browser stopped it while we were away. Without this
        // the page looks fine on return and quietly never sends another frame.
        document.addEventListener('visibilitychange', async () => {
          if (document.visibilityState !== 'visible' || !running) return;
          await keepAwake();
          try {
            const live = v.srcObject && v.srcObject.getVideoTracks().some(t => t.readyState === 'live');
            if (!live) { running = false; await begin(); return; }
            if (v.paused) await v.play();
          } catch (e) { say('Tap Allow camera to start again.', 'bad'); startBtn.style.display = ''; }
        });

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
            await goWidest(stream);
            startBtn.style.display = 'none';
            running = true;

            // What this phone can do, so the desk only offers controls that exist here. Sent once;
            // a failure is silent because a camera that cannot describe itself still takes photos.
            try {
              const t = track();
              const caps = (t && t.getCapabilities) ? (t.getCapabilities() || {}) : {};
              const set = (t && t.getSettings) ? (t.getSettings() || {}) : {};
              fetch('/p/' + TOKEN + '/caps', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ torch: !!caps.torch,
                                       width: set.width || v.videoWidth || 0,
                                       height: set.height || v.videoHeight || 0 })
              });
            } catch (e) { /* nothing here is worth interrupting the camera for */ }
            await keepAwake();
            say('Ready — press Snap on your computer.', 'ok');
            poll();
            preview();
          } catch (e) {
            say('The camera was not allowed: ' + e.message, 'bad');
            startBtn.style.display = '';
          }
        }

        // ── The widest lens the phone has ─────────────────────────────────────────
        // A photo box is a small space and the item fills it, so the ultra-wide is the lens
        // that sees the whole product with its accessories from a short distance. iOS hands
        // over the standard wide unless asked otherwise, and exposes the ultra-wide as zoom
        // values BELOW 1 on its virtual multi-camera device — so the minimum of the zoom
        // range is the widest lens, and that is what this asks for. Everything is guarded:
        // getCapabilities does not exist in every browser, and a camera that cannot zoom is
        // still a camera.
        let zoomTrack = null, zoomCaps = null;
        async function goWidest(stream) {
          try {
            const track = stream.getVideoTracks()[0];
            if (!track || typeof track.getCapabilities !== 'function') return;
            const caps = track.getCapabilities();
            if (!caps || !caps.zoom || typeof caps.zoom.min !== 'number') return;
            zoomTrack = track; zoomCaps = caps;
            await track.applyConstraints({ advanced: [{ zoom: caps.zoom.min }] });
            buildZoomControl(caps.zoom.min);
          } catch (e) { /* the lens stays wherever the phone opened it */ }
        }

        function buildZoomControl(value) {
          const wrap = document.getElementById('zoomwrap');
          if (!wrap || !zoomCaps) return;
          wrap.style.display = 'flex';
          const slider = document.getElementById('zoom');
          const label = document.getElementById('zoomval');
          slider.min = zoomCaps.zoom.min;
          slider.max = zoomCaps.zoom.max;
          slider.step = zoomCaps.zoom.step || 0.1;
          slider.value = value;
          const show = v => label.textContent = (Number(v) <= zoomCaps.zoom.min + 1e-6)
            ? 'widest' : Number(v).toFixed(1) + '×';
          show(value);
          slider.oninput = async () => {
            show(slider.value);
            try { await zoomTrack.applyConstraints({ advanced: [{ zoom: Number(slider.value) }] }); }
            catch (e) { /* a refused zoom leaves the picture where it was */ }
          };
        }

        // shoot() draws from the same <video> element the constraints above are applied to,
        // so the captured frame is whatever lens is selected right now — the zoom is real,
        // not a crop applied afterwards.
        // ── Zoom ──────────────────────────────────────────────────────────────────
        // Two ways, and the phone decides which it has. A lens that will move is asked to move
        // (real optical/sensor zoom, no pixels lost). A lens that will not — every iPhone, to a
        // web page — is left alone and the frame is cropped instead: at 12 megapixels a 4x centre
        // crop is still comfortably larger than anything eBay shows. Either way the viewfinder
        // and the saved photograph are cropped by the same number, so what the desk sees is what
        // the library gets.
        let zoom = 1, cropZoom = 1;

        function track() { return v.srcObject && v.srcObject.getVideoTracks ? v.srcObject.getVideoTracks()[0] : null; }

        async function applyZoom(z) {
          zoom = Math.max(1, Math.min(8, Number(z) || 1));
          const t = track();
          const caps = (t && t.getCapabilities) ? (t.getCapabilities() || {}) : {};
          if (caps.zoom && caps.zoom.max > (caps.zoom.min || 1)) {
            // Our 1–8 onto whatever range this lens actually has.
            const lo = caps.zoom.min || 1, hi = caps.zoom.max;
            const target = lo + (hi - lo) * ((zoom - 1) / 7);
            try { await t.applyConstraints({ advanced: [{ zoom: target }] }); cropZoom = 1; zoomNote(zoom, true); return; }
            catch (e) { /* the lens refused; the crop below still zooms */ }
          }
          cropZoom = zoom;
          zoomNote(zoom, false);
        }

        function zoomNote(z, optical) {
          zEl.textContent = z > 1.01 ? (z.toFixed(1) + '× ' + (optical ? 'lens' : 'crop')) : '';
        }

        async function applyTorch(on) {
          const t = track();
          const caps = (t && t.getCapabilities) ? (t.getCapabilities() || {}) : {};
          if (!caps.torch) return;
          try { await t.applyConstraints({ advanced: [{ torch: !!on }] }); } catch (e) { /* nothing to do */ }
        }

        // The centred window the current zoom is looking at, in source pixels.
        function window_() {
          const w = v.videoWidth / cropZoom, h = v.videoHeight / cropZoom;
          return { sx: (v.videoWidth - w) / 2, sy: (v.videoHeight - h) / 2, sw: w, sh: h };
        }

        async function shoot() {
          const q = window_();
          cv.width = Math.round(q.sw); cv.height = Math.round(q.sh);
          cv.getContext('2d').drawImage(v, q.sx, q.sy, q.sw, q.sh, 0, 0, cv.width, cv.height);
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
                const q = window_();
                const w = 640, h = Math.round(q.sh * (640 / q.sw));
                pv.width = w; pv.height = h;
                pv.getContext('2d').drawImage(v, q.sx, q.sy, q.sw, q.sh, 0, 0, w, h);
                const b = await new Promise(r => pv.toBlob(r, 'image/jpeg', 0.55));
                if (b) await fetch('/p/' + TOKEN + '/preview', { method: 'POST', body: b });
              }
            } catch (e) { /* a dropped preview frame is not worth a message */ }
            await new Promise(r => setTimeout(r, 1000));
          }
        }

        // ── Recording ─────────────────────────────────────────────────────────────
        // Started and stopped from the desktop, like the shutter. Safari records mp4 and
        // most everything else records webm, so the type is chosen by asking rather than
        // assuming. A recording stops itself at the cap whatever the desktop does, because
        // the file crosses a wifi and nobody wants to discover the limit afterwards.
        let recorder = null, chunks = [], recTimer = null, recStarted = 0, maxSecs = 60;

        function pickMime() {
          const wanted = ['video/mp4', 'video/webm;codecs=vp9', 'video/webm'];
          for (const m of wanted) {
            if (window.MediaRecorder && MediaRecorder.isTypeSupported(m)) return m;
          }
          return '';
        }

        function startRecording() {
          if (recorder || !v.srcObject) return;
          const mime = pickMime();
          try {
            recorder = mime ? new MediaRecorder(v.srcObject, { mimeType: mime })
                            : new MediaRecorder(v.srcObject);
          } catch (e) { say('This phone cannot record video: ' + e.message, 'bad'); return; }
          chunks = [];
          recorder.ondataavailable = e => { if (e.data && e.data.size) chunks.push(e.data); };
          recorder.onstop = async () => {
            clearInterval(recTimer); recTimer = null;
            const type = recorder.mimeType || mime || 'video/mp4';
            recorder = null;
            const blob = new Blob(chunks, { type });
            chunks = [];
            say('Sending the recording…', 'busy');
            try {
              const res = await fetch('/p/' + TOKEN + '/video', {
                method: 'POST', headers: { 'Content-Type': type }, body: blob });
              say(res.ok ? 'Recording sent. Ready.' : 'The computer would not take that recording.',
                  res.ok ? 'ok' : 'bad');
            } catch (e) { say('The recording could not be sent.', 'bad'); }
          };
          recorder.start(1000);
          recStarted = Date.now();
          recTimer = setInterval(() => {
            const secs = Math.floor((Date.now() - recStarted) / 1000);
            say('● Recording ' + secs + 's of ' + maxSecs + 's', 'busy');
            if (secs >= maxSecs) stopRecording();
          }, 250);
          say('● Recording 0s of ' + maxSecs + 's', 'busy');
        }

        function stopRecording() {
          if (!recorder) return;
          try { recorder.stop(); } catch (e) { recorder = null; }
        }

        async function poll() {
          while (running) {
            try {
              const r = await fetch('/p/' + TOKEN + '/poll?seq=' + settingsSeq);
              if (!r.ok) { say('This link has expired. Scan the code again.', 'bad'); return; }
              const j = await r.json();
              if (j.maxVideoSeconds) maxSecs = j.maxVideoSeconds;
              if (typeof j.seq === 'number' && j.seq !== settingsSeq) {
                settingsSeq = j.seq;
                if (typeof j.zoom === 'number') await applyZoom(j.zoom);
                await applyTorch(!!j.torch);
              }
              if (j.shoot || j.command === 'shoot') await shoot();
              else if (j.command === 'record-start') startRecording();
              else if (j.command === 'record-stop') stopRecording();
            } catch (e) {
              say('Lost the connection to your computer — retrying…', 'bad');
              await new Promise(r => setTimeout(r, 1500));
            }
          }
        }

        // The shutter, on the phone as well as on the desk: sometimes the person holding the
        // camera is the person who can see the shot.
        document.getElementById('shot').addEventListener('click', () => { if (running) shoot(); });
        document.getElementById('zr').addEventListener('input', e => applyZoom(e.target.value));
        document.getElementById('zin').addEventListener('click', () => { applyZoom(zoom + 0.5); document.getElementById('zr').value = zoom; });
        document.getElementById('zout').addEventListener('click', () => { applyZoom(zoom - 0.5); document.getElementById('zr').value = zoom; });

        startBtn.addEventListener('click', begin);
        // Safari needs a tap before it will hand over a camera, so the button is the real entry
        // point; this only helps the browsers that do not.
        begin();
        </script>
        </body></html>
        """;
}
