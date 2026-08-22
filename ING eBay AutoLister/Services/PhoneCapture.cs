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
/// <summary>
/// What the phone said its camera can do, asked of the browser rather than assumed.
/// </summary>
/// <remarks>
/// This is the whole design of the camera controls. A web page gets a different camera on every
/// handset: Android hands over the LED, the exposure and the focus distance; iOS Safari hands over
/// a zoom range and almost nothing else. So the phone reads <c>getCapabilities()</c> once and says
/// what it has, and the desktop draws only the controls that will do something. A dial that does
/// nothing is worse than a missing dial — the seller turns it, the photo does not change, and they
/// stop trusting every other control on the screen.
///
/// Every one of these has a fallback that runs on the phone's own canvas at capture time
/// (see the phone page), so <i>brightness</i> and <i>warmth</i> always do something even when the
/// lens will not take the instruction. The flags say whether the LENS is doing it, which is the
/// difference between a photograph that is correctly exposed and one that has been brightened.
/// </remarks>
public sealed record PhoneCaps(
    bool Torch, int Width, int Height,
    bool Exposure = false, bool Focus = false, bool Macro = false,
    bool WhiteBalance = false, bool Tap = false, bool MultiCamera = false,
    double ZoomMin = 1, double ZoomMax = 1, string? Lenses = null);

/// <summary>What the desktop's camera controls send. Null means "leave that one alone".</summary>
/// <remarks>
/// Null-means-unchanged is what lets six controls share one endpoint without any of them
/// switching another off by omission — nudging the zoom must not turn the flash off.
/// </remarks>
public sealed record PhoneSettingsRequest(
    double? Zoom, bool? Torch,
    string? Flash = null, double? Exposure = null, string? Focus = null,
    string? WhiteBalance = null, string? Lens = null, string? Facing = null,
    bool? Level = null);

public sealed class PhoneCapture(PhotoLibrary photos, ActionLog log) : IAsyncDisposable
{
    /// <summary>Where the phone connects. Deliberately not 9332: that port is the app's alone.</summary>
    public const int Port = 9443;

    // Pairing is a relationship, not a server session. The QR secret lives in the seller's fixed
    // data home so a Chrome tab that was paired yesterday still has the right address after an
    // update restarts the executable today. It is disabled only by the explicit Disconnect
    // endpoint; ordinary shutdown leaves it ready to resume.
    private sealed record Pairing(string Token, bool Enabled);
    private static string PairingPath => Path.Combine(AppPaths.DataHome, "phone-camera-pairing.json");

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

    // The rest of the camera. Strings rather than enums because these cross to a browser as JSON
    // and back, and a value this server does not recognise must reach the phone unharmed rather
    // than being rounded to the nearest thing a C# enum happens to have.
    private string _flash = "off";          // off | on | auto
    private double _exposure;               // -2..+2 stops, 0 = leave it alone
    private string _focus = "auto";         // auto | macro | far
    private string _whiteBalance = "auto";  // auto | daylight | tungsten | cool | shade
    private string _lens = "wide";          // ultra | wide | tele
    private string _facing = "environment"; // environment | user
    private bool _level;                    // the horizon indicator, on the phone's own screen

    // What this phone said its camera can do, on arrival. The torch is the one that really
    // varies — Android hands a web page the LED, iOS does not — and a button that cannot work
    // must not be on the screen. Zoom is never gated: a crop of a twelve-megapixel frame is a
    // zoom on any phone ever made, whether or not its lens will move.
    private bool _canTorch;
    private int _capWidth, _capHeight;
    private bool _canExposure, _canFocus, _canMacro, _canWhiteBalance, _canTap, _canMultiCamera;
    private double _zoomMin = 1, _zoomMax = 1;
    private string _lenses = "";            // e.g. "0.5,1,2" — the lens buttons this phone earns

    // The viewfinder. The phone sends a small frame about once a second so the person at the
    // desk can see what they are about to photograph — without it the shutter is on one device
    // and the picture is on another, which is not a camera, it is a guess. Held in memory only:
    // a preview is not a photograph and has no business in the photo library.
    private byte[]? _preview;
    private DateTimeOffset _previewAt;
    // Which frame this is. A viewfinder reader holds the number of the frame it already drew, so
    // it can be handed the NEXT one the moment it exists instead of asking whether there is one.
    private long _previewSeq;
    private readonly Signal _previewReady = new();

    // ── Why anything here waits on a signal rather than a timer ──────────────────────────────
    //
    // Every wait in this file used to be a sleep: the phone's command poll woke every 150ms to
    // ask whether the shutter had been pressed, the shutter woke every 120ms to ask whether a
    // photograph had arrived, and the viewfinder was two independent one-second timers — one on
    // the phone deciding when to send a frame, one on the desktop deciding when to ask for one.
    // Those numbers are not the cost of the network; they are the cost of asking. Stacked, they
    // put a viewfinder frame on the screen up to two seconds after the lens saw it, which is not
    // a slow camera, it is a camera you cannot aim.
    //
    // A signal costs nothing while nothing is happening and returns in microseconds when it is.
    // The rule for using one correctly: take the waiter BEFORE reading the state you are waiting
    // on. Take it after and a change that lands in between is a change you wait a full timeout to
    // hear about — the bug this shape exists to prevent.
    private sealed class Signal
    {
        private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The task to await. Take this before checking what you are waiting for.</summary>
        public Task Waiter => Volatile.Read(ref _tcs).Task;

        /// <summary>Releases everyone waiting, and arms the next wait.</summary>
        public void Set() =>
            Interlocked.Exchange(ref _tcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                       .TrySetResult();
    }

    // Woken by anything the phone's command poll is sitting there waiting for: the shutter, a
    // recording, a zoom nudge, a change of lens.
    private readonly Signal _commandReady = new();
    // Woken when a photograph lands, so the desktop's Snap returns on the upload rather than on
    // the next tick of a poll.
    private readonly Signal _shotArrived = new();
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
        int CaptureWidth = 0, int CaptureHeight = 0,
        string Flash = "off", double Exposure = 0, string Focus = "auto",
        string WhiteBalance = "auto", string Lens = "wide", string Facing = "environment",
        bool Level = false,
        bool CanExposure = false, bool CanFocus = false, bool CanMacro = false,
        bool CanWhiteBalance = false, bool CanTap = false, bool CanMultiCamera = false,
        double ZoomMin = 1, double ZoomMax = 1, string Lenses = "");

    /// <summary>The last viewfinder frame the phone sent, or null if it has not sent one lately.</summary>
    public byte[]? LatestPreview() =>
        _preview is { } p && DateTimeOffset.UtcNow - _previewAt < TimeSpan.FromSeconds(15) ? p : null;

    /// <summary>
    /// The first viewfinder frame newer than <paramref name="afterSeq"/>, waited for rather than
    /// polled for. Null when none arrives inside <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// This is what lets the desktop hold one open connection and be written to at the phone's
    /// own frame rate. The timeout exists only so a stream over a phone that has been put in a
    /// pocket can notice and say so; it is not a polling interval, and nothing is lost by making
    /// it long.
    /// </remarks>
    public async Task<(byte[] Frame, long Seq)?> NextPreviewAsync(
        long afterSeq, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!ct.IsCancellationRequested)
        {
            // Before the read, always — see Signal.
            var waiter = _previewReady.Waiter;

            var seq = Interlocked.Read(ref _previewSeq);
            if (seq != afterSeq && LatestPreview() is { } frame) return (frame, seq);

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return null;

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var slept = Task.Delay(left, wake.Token);
            await Task.WhenAny(waiter, slept).ConfigureAwait(false);
            wake.Cancel();   // stops the timer the moment the signal won the race
        }
        return null;
    }

    public Status Snapshot()
    {
        if (_app is null) return new(false, null, false, 0, [], null, null);
        var url = PublicUrl;
        return new(true, url, _phoneEverConnected && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(20),
                   _shots.Count, [.. _shots], QrCode.ToSvg(url), null, LatestPreview() is not null,
                   _phoneEverConnected, _recording, [.. _videos], MaxVideoSeconds,
                   _zoom, _torch, _canTorch, _capWidth, _capHeight,
                   _flash, _exposure, _focus, _whiteBalance, _lens, _facing, _level,
                   _canExposure, _canFocus, _canMacro, _canWhiteBalance, _canTap, _canMultiCamera,
                   _zoomMin, _zoomMax, _lenses);
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
        var saved = ReadPairing();
        var token = saved is not null && ValidToken(saved.Token)
            ? saved.Token.ToLowerInvariant()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        WritePairing(new(token, true));
        return await StartListenerAsync(token, ct);
    }

    /// <summary>
    /// Restores a previously paired phone after an app restart. This never creates a pairing:
    /// sellers who have never opened the phone camera do not acquire a network listener merely
    /// by launching the desktop app.
    /// </summary>
    public async Task<Status?> ResumeIfEnabledAsync(CancellationToken ct)
    {
        var saved = ReadPairing();
        if (saved is null || !saved.Enabled || !ValidToken(saved.Token)) return null;

        // An updater can start the replacement process at the instant the previous listener is
        // releasing 9443. A few short retries turn that race into a brief reconnect on the phone.
        Status status = new(false, null, false, 0, [], null, null);
        for (var attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            status = await StartListenerAsync(saved.Token.ToLowerInvariant(), ct);
            if (status.Running) return status;
            await Task.Delay(400, ct);
        }
        return status;
    }

    private async Task<Status> StartListenerAsync(string token, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_app is not null) return Snapshot();

            _token = token;
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

    /// <summary>Forgets automatic resume only when the seller explicitly disconnects the phone.</summary>
    public async Task DisableAsync()
    {
        var saved = ReadPairing();
        var token = ValidToken(_token) ? _token : saved?.Token;
        if (token is not null && ValidToken(token)) WritePairing(new(token.ToLowerInvariant(), false));
        await StopAsync();
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

    private Pairing? ReadPairing()
    {
        try
        {
            if (!File.Exists(PairingPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<Pairing>(
                File.ReadAllText(PairingPath),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Phone pairing could not be read", ex.Message);
            return null;
        }
    }

    private void WritePairing(Pairing pairing)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataHome);
            var json = System.Text.Json.JsonSerializer.Serialize(pairing,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var temporary = PairingPath + ".new";
            File.WriteAllText(temporary, json);
            File.Move(temporary, PairingPath, true);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Phone pairing could not be saved", ex.Message);
        }
    }

    private static bool ValidToken(string? token) =>
        token is { Length: >= 16 and <= 64 } && token.All(Uri.IsHexDigit);

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
        _commandReady.Set();   // the phone is holding a poll open; this is what it was waiting for

        // The phone's poll completes the shutter task; the upload that follows adds to _shots.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var start = DateTimeOffset.UtcNow;
            while (_shots.Count == before)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Taken before the count is re-read at the top of the loop, so an upload that
                // lands mid-iteration is not one this wait sleeps through. See Signal.
                var landed = _shotArrived.Waiter;
                if (_shots.Count != before) break;

                var left = TimeSpan.FromSeconds(28) - (DateTimeOffset.UtcNow - start);
                if (left <= TimeSpan.Zero) break;

                using var wake = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                await Task.WhenAny(landed, Task.Delay(left, wake.Token)).ConfigureAwait(false);
                wake.Cancel();
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
        _commandReady.Set();
        _recording = true;
        return null;
    }

    /// <summary>Tells the phone to stop. The file arrives on its own a moment later.</summary>
    public void StopRecording()
    {
        if (!_recording) return;
        _command = "record-stop";
        _commandReady.Set();
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
    public Status Apply(double? zoom, bool? torch) => Apply(new PhoneSettingsRequest(zoom, torch));

    /// <summary>
    /// The whole camera, one control at a time. Anything null is left where it was.
    /// </summary>
    /// <remarks>
    /// Nothing is gated on capability here except the torch, which is a physical lamp that either
    /// exists or does not. Brightness and warmth are accepted from any phone because the phone page
    /// falls back to doing them on the captured frame — refusing them here would turn a control
    /// that works into one that silently does not.
    /// </remarks>
    public Status Apply(PhoneSettingsRequest req)
    {
        if (req.Zoom is { } z) _zoom = Math.Clamp(z, 1.0, 8.0);
        if (req.Torch is { } t) _torch = t && _canTorch;

        // "on" with no lamp is a promise this phone cannot keep, so it is not accepted.
        if (Pick(req.Flash, "off", "on", "auto") is { } fl)
            _flash = (fl is "on" or "auto") && !_canTorch ? "off" : fl;

        // Two stops either way. Past that a phone is not exposing, it is destroying a photograph.
        if (req.Exposure is { } ev) _exposure = Math.Clamp(ev, -2.0, 2.0);

        if (Pick(req.Focus, "auto", "macro", "far") is { } fo) _focus = fo;
        if (Pick(req.WhiteBalance, "auto", "daylight", "tungsten", "cool", "shade") is { } wb) _whiteBalance = wb;
        if (Pick(req.Lens, "ultra", "wide", "tele") is { } ln) _lens = ln;
        if (Pick(req.Facing, "environment", "user") is { } fc) _facing = fc;
        if (req.Level is { } lv) _level = lv;

        _settingsSeq++;
        // A zoom nudge, a lens change or a torch has to reach the lens now. The poll is already
        // open and waiting; this is what ends its wait.
        _commandReady.Set();
        return Snapshot();

        // A value the desktop invented is dropped rather than stored: the phone would not know
        // what to do with it, and a camera stuck in a mode nobody can name is unrecoverable
        // without rescanning the code.
        static string? Pick(string? value, params string[] allowed) =>
            value is not null && Array.IndexOf(allowed, value) >= 0 ? value : null;
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
        // Every answer this endpoint gives carries the whole camera, not just the thing that
        // changed: the phone applies what it is told and keeps no opinion of its own, so a page
        // that was asleep through three changes catches up on the first reply it gets.
        object Payload(bool shoot, string command) => new
        {
            shoot,
            command,
            maxVideoSeconds = MaxVideoSeconds,
            seq = _settingsSeq,
            zoom = _zoom,
            torch = _torch,
            flash = _flash,
            exposure = _exposure,
            focus = _focus,
            whiteBalance = _whiteBalance,
            lens = _lens,
            facing = _facing,
            level = _level
        };

        web.MapGet("/p/{token}/poll", async (string token, int? seq, CancellationToken ct) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            // Held open until there is something to say, then answered at once. It used to wake
            // ten times a second to ask itself the same three questions, which put up to 150ms
            // between a press of Snap and the phone hearing about it — on top of the round trip.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
            while (!ct.IsCancellationRequested)
            {
                // Before the reads below, always — a command that lands between the read and the
                // wait must not be one this poll sleeps through. See Signal.
                var waiter = _commandReady.Waiter;

                if (_shutter is { Task.IsCompleted: false } s)
                {
                    s.TrySetResult(true);
                    // `shoot` is still sent for a page that predates recording, so an old
                    // tab left open keeps taking photographs instead of going quiet.
                    return Results.Ok(Payload(true, "shoot"));
                }
                if (_command.Length > 0)
                {
                    var cmd = _command;
                    _command = "";
                    return Results.Ok(Payload(false, cmd));
                }
                // A zoom nudge must reach the lens now, not when this long-poll happens to time
                // out. The phone tells us which settings it is already using.
                if (seq is { } known && known != _settingsSeq)
                    return Results.Ok(Payload(false, ""));

                var left = deadline - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero) break;

                // The timeout is not a polling interval — it exists so the phone re-announces
                // itself now and then, which is how _lastSeen knows the page is still open.
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(waiter, Task.Delay(left, wake.Token)).ConfigureAwait(false);
                wake.Cancel();
            }
            return Results.Ok(Payload(false, ""));
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

        // What this phone's camera can do, sent once when it starts. The desktop draws its
        // controls from this and nothing else, so a phone that says little gets a small panel
        // rather than a full one where half the dials are decoration.
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
                    _canExposure = caps.Exposure;
                    _canFocus = caps.Focus;
                    _canMacro = caps.Macro;
                    _canWhiteBalance = caps.WhiteBalance;
                    _canTap = caps.Tap;
                    _canMultiCamera = caps.MultiCamera;
                    _zoomMin = caps.ZoomMin;
                    _zoomMax = caps.ZoomMax;
                    _lenses = caps.Lenses ?? "";
                    // A phone with no lamp cannot be left holding a flash mode it will never fire.
                    if (!_canTorch) { _flash = "off"; _torch = false; }
                    _settingsSeq++;   // the desktop's panel redraws off this
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
            if (ms.Length > 500)
            {
                _preview = ms.ToArray();
                _previewAt = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref _previewSeq);
                // Straight out to whoever is watching. This is the whole viewfinder path now:
                // lens -> phone POST -> this line -> the desktop's open stream, with no timer
                // anywhere between them.
                _previewReady.Set();
            }
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
            _shotArrived.Set();
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
          /* The camera controls the desktop is driving, shown here too. The person holding the
             phone is often the person who can see that the shot is too dark. */
          .rig{width:100%;max-width:520px;margin-top:14px;display:flex;flex-direction:column;gap:10px}
          .rigrow{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
          .riglab{font-size:12px;color:#9fb9bd;min-width:64px;text-transform:uppercase;letter-spacing:.04em}
          .pill{padding:9px 14px;font-size:14px;border-radius:999px;background:#123c42;color:#cfe3e6;
                border:1px solid #1d5760;margin:0}
          .pill.on{background:#c79a36;color:#241703;border-color:#c79a36;font-weight:700}
          .pill[disabled]{opacity:.35}
          .lensrow{display:flex;gap:8px;justify-content:center;width:100%;max-width:520px;margin-top:12px}
          .lens{width:56px;height:56px;border-radius:50%;background:rgba(9,40,45,.85);color:#cfe3e6;
                border:1px solid #1d5760;font-size:14px;font-weight:700;margin:0;padding:0}
          .lens.on{background:#c79a36;color:#241703;border-color:#c79a36}
          /* The horizon. Two lines: one fixed, one that rolls with the phone. They meet and turn
             gold when the phone is square to the item, which is the only moment that matters. */
          .level{position:absolute;left:50%;top:50%;width:120px;height:2px;margin-left:-60px;
                 background:rgba(255,255,255,.55);pointer-events:none;transform-origin:center}
          .level.flat{background:#c79a36;height:3px}
          .levelref{position:absolute;left:50%;top:50%;width:44px;height:2px;margin-left:-22px;
                    background:rgba(255,255,255,.25);pointer-events:none}
          .stage{position:relative;width:100%;max-width:520px}
          .tapdot{position:absolute;width:64px;height:64px;margin:-32px 0 0 -32px;border:2px solid #c79a36;
                  border-radius:10px;pointer-events:none;opacity:0;transition:opacity .3s}
          .tapdot.show{opacity:1}
          .hint{font-size:12px;color:#6f8b90;text-align:center;margin-top:4px}
        </style></head>
        <body>
          <h1>ING Photo Box</h1>
          <div class="sub">Point this phone at the item and leave this page open.<br>Press <b>Snap</b> in the app on your computer.</div>
          <div class="stage">
            <video id="v" playsinline muted autoplay></video>
            <div class="levelref" id="levelref" style="display:none"></div>
            <div class="level" id="level" style="display:none"></div>
            <div class="tapdot" id="tapdot"></div>
          </div>
          <div class="lensrow" id="lensrow" style="display:none"></div>
          <div class="hint" id="taphint" style="display:none">Tap the picture to focus there</div>
          <div class="rig" id="rig" style="display:none">
            <div class="rigrow" id="flashrow">
              <span class="riglab">Flash</span>
              <button class="pill" type="button" data-flash="off">Off</button>
              <button class="pill" type="button" data-flash="auto">Auto</button>
              <button class="pill" type="button" data-flash="on">On</button>
            </div>
            <div class="rigrow" id="levelrow">
              <span class="riglab">Level</span>
              <button class="pill" type="button" id="levelbtn">Off</button>
            </div>
          </div>
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
            await sendCaps();
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

        // ══ The rest of the camera ═══════════════════════════════════════════════
        // Everything a phone camera app puts on its screen, as far as a web page is allowed to
        // reach it — and, where it is not allowed, done to the captured frame instead so the
        // control still does what it says.
        //
        // WHAT A BROWSER ACTUALLY GIVES YOU, because this is the whole reason the code is
        // shaped like this: Android Chrome exposes torch, exposureCompensation, focusMode,
        // focusDistance, whiteBalanceMode, colorTemperature and pointsOfInterest through
        // applyConstraints. iOS Safari exposes a zoom range and, on current versions, very
        // little else — no torch, no ImageCapture, and no depth map, so no true portrait
        // blur at the lens. Rather than hide the controls an iPhone cannot drive, the two
        // that can be honestly done in software — brightness and warmth — are applied to the
        // pixels at capture, and the app says which of the two happened. Portrait is done
        // after the fact on the desktop, where there is a real segmentation model.
        let flashMode = 'off', exposureEv = 0, focusMode = 'auto';
        let wbMode = 'auto', lensMode = 'wide', facing = 'environment', levelOn = false;
        // Set when the LENS took the instruction; when it did not, shoot() does it in pixels.
        let lensExposure = false, lensWhiteBalance = false;

        function caps_() {
          const t = track();
          return (t && t.getCapabilities) ? (t.getCapabilities() || {}) : {};
        }
        const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
        const wait = ms => new Promise(r => setTimeout(r, ms));

        // ── What this phone can do, told to the desktop ──────────────────────────
        // Sent once on arrival and again whenever the camera is re-opened, because switching to
        // the front camera changes every answer.
        async function sendCaps() {
          try {
            const t = track(), c = caps_();
            const set = (t && t.getSettings) ? (t.getSettings() || {}) : {};
            const zmin = (c.zoom && typeof c.zoom.min === 'number') ? c.zoom.min : 1;
            const zmax = (c.zoom && typeof c.zoom.max === 'number') ? c.zoom.max : 1;

            // The lens buttons a real camera app shows: 0.5 / 1 / 2. Offered only where the zoom
            // range proves the phone has something to switch to — a single-lens phone gets one
            // button, which is no button at all, so it gets none.
            const lenses = [];
            if (zmin <= 0.7) lenses.push('0.5');
            lenses.push('1');
            if (zmax >= 1.9) lenses.push('2');

            let cameras = 0;
            try {
              const devs = await navigator.mediaDevices.enumerateDevices();
              cameras = devs.filter(d => d.kind === 'videoinput').length;
            } catch (e) { /* label-less device lists are still countable, but not always present */ }

            await fetch('/p/' + TOKEN + '/caps', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                torch: !!c.torch,
                width: set.width || v.videoWidth || 0,
                height: set.height || v.videoHeight || 0,
                exposure: !!c.exposureCompensation,
                focus: Array.isArray(c.focusMode) && c.focusMode.length > 0,
                macro: !!(c.focusDistance && typeof c.focusDistance.min === 'number'),
                whiteBalance: !!(c.colorTemperature || (Array.isArray(c.whiteBalanceMode) && c.whiteBalanceMode.length)),
                tap: !!c.pointsOfInterest,
                multiCamera: cameras > 1,
                zoomMin: zmin, zoomMax: zmax,
                lenses: lenses.join(',')
              })
            });
            paintRig(!!c.torch);
          } catch (e) { /* a camera that cannot describe itself still takes photographs */ }
        }

        // ── Flash ────────────────────────────────────────────────────────────────
        // A flash is not a torch. The lamp comes on just before the frame and goes off straight
        // after, which is what a camera does and what a seller means by "use the flash". The
        // pause in between is not padding: the sensor is metering, and a frame grabbed the
        // instant the LED lights is a photograph of a white blur.
        //
        // On a phone with no lamp — every iPhone, to a web page — the mode cannot be set: the
        // desktop refuses it, and the panel there says why rather than offering a dead switch.
        async function fireFlash() {
          if (flashMode === 'off' || !caps_().torch) return false;
          if (flashMode === 'auto' && !sceneIsDark()) return false;
          await applyTorch(true);
          await wait(340);
          return true;
        }

        // Is this dark enough to need the lamp? Measured off the frame that is already on screen,
        // at 32 pixels wide, which costs nothing and is plenty for "how bright is this".
        const lum = document.createElement('canvas');
        function sceneIsDark() {
          try {
            if (!v.videoWidth) return false;
            lum.width = 32; lum.height = 24;
            const g = lum.getContext('2d', { willReadFrequently: true });
            g.drawImage(v, 0, 0, 32, 24);
            const d = g.getImageData(0, 0, 32, 24).data;
            let sum = 0;
            for (let i = 0; i < d.length; i += 4) sum += 0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2];
            return (sum / (d.length / 4)) < 72;
          } catch (e) { return false; }
        }

        // ── Brightness ───────────────────────────────────────────────────────────
        // Asked of the lens first, because exposing correctly and brightening afterwards are not
        // the same thing: the lens gathers more light, the canvas only stretches what it caught,
        // and stretching a dark frame stretches its noise with it. The fallback is still worth
        // having — a listing photo two stops too dark does not sell either.
        async function applyExposure(ev) {
          exposureEv = clamp(Number(ev) || 0, -2, 2);
          lensExposure = false;
          const t = track(), c = caps_();
          if (!t || !c.exposureCompensation) return;
          const lo = c.exposureCompensation.min, hi = c.exposureCompensation.max;
          if (typeof lo !== 'number' || typeof hi !== 'number' || hi <= lo) return;
          try {
            // exposureCompensation is in stops on every implementation that has it, so our
            // -2..+2 is already the right unit — it only has to be brought inside this lens's range.
            await t.applyConstraints({ advanced: [{ exposureMode: 'continuous',
                                                    exposureCompensation: clamp(exposureEv, lo, hi) }] });
            lensExposure = true;
          } catch (e) { /* the canvas will do it at capture */ }
        }

        // ── Focus ────────────────────────────────────────────────────────────────
        // Macro is the one that earns its place in a photo box: the whole job is a hallmark, a
        // serial number or a scratch photographed from four inches away, and a lens left on
        // continuous autofocus hunts past all three.
        async function applyFocus(mode) {
          focusMode = mode || 'auto';
          const t = track(), c = caps_();
          if (!t || !Array.isArray(c.focusMode)) return;
          const has = m => c.focusMode.indexOf(m) >= 0;
          try {
            if (focusMode === 'auto') {
              if (has('continuous')) await t.applyConstraints({ advanced: [{ focusMode: 'continuous' }] });
              return;
            }
            const fd = c.focusDistance;
            if (has('manual') && fd && typeof fd.min === 'number') {
              const near = focusMode === 'macro';
              await t.applyConstraints({ advanced: [{ focusMode: 'manual',
                                                      focusDistance: near ? fd.min : fd.max }] });
            } else if (has('single-shot')) {
              await t.applyConstraints({ advanced: [{ focusMode: 'single-shot' }] });
            }
          } catch (e) { /* the lens stays where it was, which is a working camera */ }
        }

        // Tap the picture, focus there — the gesture every phone camera has trained everyone to
        // make. The square is drawn wherever they touched even when the lens will not oblige,
        // because a tap that draws nothing reads as a broken screen.
        async function focusAt(nx, ny) {
          const dot = document.getElementById('tapdot');
          const box = v.getBoundingClientRect();
          dot.style.left = (nx * box.width) + 'px';
          dot.style.top = (ny * box.height) + 'px';
          dot.classList.add('show');
          setTimeout(() => dot.classList.remove('show'), 900);
          const t = track(), c = caps_();
          if (!t || !c.pointsOfInterest) return;
          try {
            const adv = { pointsOfInterest: [{ x: clamp(nx, 0, 1), y: clamp(ny, 0, 1) }] };
            if (Array.isArray(c.focusMode) && c.focusMode.indexOf('single-shot') >= 0) adv.focusMode = 'single-shot';
            await t.applyConstraints({ advanced: [adv] });
          } catch (e) { /* the square still told them the tap landed */ }
        }

        // ── White balance ────────────────────────────────────────────────────────
        // The one setting a seller can see the effect of without being told what it is. A desk
        // lamp is orange, a window is blue, and a product shot under either is the wrong colour —
        // which on eBay is a return, because the buyer ordered the thing in the photograph.
        const KELVIN = { daylight: 5600, tungsten: 2900, cool: 7200, shade: 8500 };
        async function applyWhiteBalance(mode) {
          wbMode = mode || 'auto';
          lensWhiteBalance = false;
          const t = track(), c = caps_();
          if (!t) return;
          try {
            if (wbMode === 'auto') {
              if (Array.isArray(c.whiteBalanceMode) && c.whiteBalanceMode.indexOf('continuous') >= 0) {
                await t.applyConstraints({ advanced: [{ whiteBalanceMode: 'continuous' }] });
                lensWhiteBalance = true;
              }
              return;
            }
            const k = KELVIN[wbMode];
            if (!k || !c.colorTemperature) return;
            const target = clamp(k, c.colorTemperature.min, c.colorTemperature.max);
            await t.applyConstraints({ advanced: [{ whiteBalanceMode: 'manual', colorTemperature: target }] });
            lensWhiteBalance = true;
          } catch (e) { /* the canvas will do it at capture */ }
        }

        // The software version: a colour temperature is a red/blue see-saw, so the fallback is
        // one multiply per channel. Deliberately gentle — this is correcting a lamp, not
        // applying a filter, and a product photograph that has obviously been tinted is worse
        // than one that is slightly warm.
        function wbGains() {
          if (wbMode === 'auto' || lensWhiteBalance) return null;
          const g = { daylight: [1.00, 1.00, 1.00], tungsten: [1.14, 1.01, 0.86],
                      cool: [0.92, 0.99, 1.12], shade: [1.08, 1.00, 0.93] }[wbMode];
          return g || null;
        }

        // ── Lens ─────────────────────────────────────────────────────────────────
        // 0.5 / 1 / 2, the way a camera app labels them, mapped onto whatever range this phone
        // reports. On iOS these are real lenses behind one virtual device, which is why the
        // ultra-wide lives at a zoom value below 1 rather than on a camera of its own.
        async function applyLens(which) {
          lensMode = which || 'wide';
          const t = track(), c = caps_();
          if (!t || !c.zoom || typeof c.zoom.min !== 'number') return;
          const lo = c.zoom.min, hi = c.zoom.max;
          const target = which === 'ultra' ? lo : which === 'tele' ? clamp(2, lo, hi) : clamp(1, lo, hi);
          try { await t.applyConstraints({ advanced: [{ zoom: target }] }); cropZoom = 1; }
          catch (e) { /* the lens stays where it was */ }
          paintLenses();
        }

        // ── Front camera ─────────────────────────────────────────────────────────
        // Not a photo-box lens, and that is exactly why it is here: the seller checking that the
        // page is alive, or shooting themselves holding the item for scale, should not have to
        // rescan a QR code to do it.
        async function applyFacing(which) {
          if (which === facing) return;
          facing = which;
          try {
            const old = v.srcObject;
            const stream = await navigator.mediaDevices.getUserMedia({
              video: { facingMode: { ideal: facing }, width: { ideal: 4032 }, height: { ideal: 3024 } },
              audio: false
            });
            if (old && old.getTracks) old.getTracks().forEach(x => x.stop());
            v.srcObject = stream;
            await v.play();
            // A different camera answers every capability question differently, so everything is
            // asked again and every setting re-applied to the new lens.
            await sendCaps();
            await applyExposure(exposureEv);
            await applyFocus(focusMode);
            await applyWhiteBalance(wbMode);
          } catch (e) { say('That camera would not open.', 'bad'); }
        }

        // ── The level ────────────────────────────────────────────────────────────
        // A phone leaning five degrees makes a product look like it is sliding off the desk, and
        // it is the one flaw a seller never notices while holding the thing. iOS will not give a
        // page the motion sensors without a tap, so the tap is asked for here rather than
        // pretended away.
        let levelReady = false;
        async function applyLevel(on) {
          levelOn = !!on;
          const bar = document.getElementById('level'), ref = document.getElementById('levelref');
          const btn = document.getElementById('levelbtn');
          bar.style.display = ref.style.display = levelOn ? '' : 'none';
          if (btn) { btn.textContent = levelOn ? 'On' : 'Off'; btn.classList.toggle('on', levelOn); }
          if (!levelOn || levelReady) return;
          try {
            const DOE = window.DeviceOrientationEvent;
            if (DOE && typeof DOE.requestPermission === 'function') {
              const granted = await DOE.requestPermission();
              if (granted !== 'granted') { say('Tap the Level button on this phone to allow it.', 'busy'); return; }
            }
            window.addEventListener('deviceorientation', e => {
              if (!levelOn) return;
              const roll = Number(e.gamma) || 0;
              bar.style.transform = 'rotate(' + (-roll) + 'deg)';
              bar.classList.toggle('flat', Math.abs(roll) < 2);
            });
            levelReady = true;
          } catch (e) { /* no sensors: the bar simply stays level, and lies about nothing */ }
        }

        // ── The controls on this phone's own screen ──────────────────────────────
        function paintRig(hasTorch) {
          const rig = document.getElementById('rig');
          if (rig) rig.style.display = '';
          const flashRow = document.getElementById('flashrow');
          if (flashRow) flashRow.style.display = hasTorch ? '' : 'none';
          document.querySelectorAll('[data-flash]').forEach(b =>
            b.classList.toggle('on', b.dataset.flash === flashMode));
          const hint = document.getElementById('taphint');
          if (hint) hint.style.display = caps_().pointsOfInterest ? '' : 'none';
          paintLenses();
        }

        function paintLenses() {
          const row = document.getElementById('lensrow');
          const c = caps_();
          if (!row) return;
          const zmin = (c.zoom && typeof c.zoom.min === 'number') ? c.zoom.min : 1;
          const zmax = (c.zoom && typeof c.zoom.max === 'number') ? c.zoom.max : 1;
          const opts = [];
          if (zmin <= 0.7) opts.push(['ultra', '.5\u00d7']);
          opts.push(['wide', '1\u00d7']);
          if (zmax >= 1.9) opts.push(['tele', '2\u00d7']);
          if (opts.length < 2) { row.style.display = 'none'; return; }
          row.style.display = 'flex';
          row.innerHTML = opts.map(o =>
            '<button type="button" class="lens' + (o[0] === lensMode ? ' on' : '') +
            '" data-lens="' + o[0] + '">' + o[1] + '</button>').join('');
        }

        // The centred window the current zoom is looking at, in source pixels.
        function window_() {
          const w = v.videoWidth / cropZoom, h = v.videoHeight / cropZoom;
          return { sx: (v.videoWidth - w) / 2, sy: (v.videoHeight - h) / 2, sw: w, sh: h };
        }

        async function shoot() {
          // The lamp, if this phone has one and the mode asked for it. Turned off in the finally
          // below whatever happens next, because a torch left burning is a flat battery and a
          // seller who thinks the app broke their phone.
          const lit = await fireFlash();
          try {
          const q = window_();
          cv.width = Math.round(q.sw); cv.height = Math.round(q.sh);
          const g = cv.getContext('2d');
          g.drawImage(v, q.sx, q.sy, q.sw, q.sh, 0, 0, cv.width, cv.height);
          developFrame(g, cv.width, cv.height);
          fl.style.opacity = '0.85'; setTimeout(() => fl.style.opacity = '0', 120);
          const blob = await new Promise(r => cv.toBlob(r, 'image/jpeg', 0.95));
          say('Sending…', 'busy');
          const res = await fetch('/p/' + TOKEN + '/photo', { method: 'POST', body: blob });
          if (res.ok) { taken++; c.textContent = taken + (taken === 1 ? ' photo sent' : ' photos sent'); say('Ready — press Snap on your computer.', 'ok'); }
          else say('The computer would not take that photo.', 'bad');
          } finally { if (lit) await applyTorch(false); }
        }

        // ── What the lens would not do, done to the pixels ───────────────────────
        // Only ever the settings the lens refused: when the camera took the instruction this
        // touches nothing, because a frame that is already correctly exposed and correctly
        // balanced must not be corrected twice. That is the whole reason lensExposure and
        // lensWhiteBalance exist.
        //
        // Skipped entirely at the default settings, so an untouched camera produces an untouched
        // photograph — this never runs on a photo nobody asked it to change.
        function developFrame(g, w, h) {
          const gain = (!lensExposure && Math.abs(exposureEv) > 0.01) ? Math.pow(2, exposureEv) : 1;
          const wb = wbGains();
          if (gain === 1 && !wb) return;
          try {
            const img = g.getImageData(0, 0, w, h);
            const d = img.data;
            const rg = (wb ? wb[0] : 1) * gain, gg = (wb ? wb[1] : 1) * gain, bg = (wb ? wb[2] : 1) * gain;
            // A 256-entry table per channel rather than three multiplies per pixel: a 12-megapixel
            // frame is 48 million channel values and the difference is a second of the seller's time.
            const tr = new Uint8Array(256), tg = new Uint8Array(256), tb = new Uint8Array(256);
            for (let i = 0; i < 256; i++) {
              tr[i] = clamp(Math.round(i * rg), 0, 255);
              tg[i] = clamp(Math.round(i * gg), 0, 255);
              tb[i] = clamp(Math.round(i * bg), 0, 255);
            }
            for (let i = 0; i < d.length; i += 4) { d[i] = tr[d[i]]; d[i + 1] = tg[d[i + 1]]; d[i + 2] = tb[d[i + 2]]; }
            g.putImageData(img, 0, 0);
          } catch (e) { /* a frame that will not develop is still a photograph */ }
        }

        // The desktop's viewfinder — the picture the person at the computer aims by. The real
        // frame, at full sensor resolution, is taken by shoot() and never goes through here.
        //
        // WHY THIS IS NOT ON A TIMER. It used to sleep a flat second between frames, and the
        // desktop asked for a frame on its own unsynchronised second, so a picture reached the
        // screen up to two seconds after the lens saw it and moved once a second when it got
        // there. Nobody can aim a camera through that: you move the phone, wait, and discover
        // where it was pointing a moment ago.
        //
        // So the loop is paced by the link itself. Send a frame, wait for the send to finish,
        // send the next one. On a phone and a desk on the same wifi that settles at 15-25 frames
        // a second; on a slow link it settles wherever the link can carry, which is the right
        // answer at both ends and needs no number chosen in advance. The only fixed figure is a
        // floor between frames, so a fast link cannot spend the phone's battery encoding frames
        // faster than a screen can show them.
        //
        // requestAnimationFrame is deliberately not used: it stops in a backgrounded tab, and a
        // phone propped up as a camera is very often a phone the person is not looking at.
        const MIN_FRAME_MS = 40;              // 25fps ceiling — past this is heat, not smoothness
        const pv = document.createElement('canvas');
        const pctx = pv.getContext('2d', { alpha: false, desynchronized: true });
        async function preview() {
          let lastFrame = -1;
          while (running) {
            const began = performance.now();
            try {
              // A frame the camera has not redrawn yet is the frame already on the desktop's
              // screen. Encoding and sending it again costs a JPEG and a request to say nothing.
              const stamp = (v.currentTime * 1000) | 0;
              if (v.videoWidth && stamp !== lastFrame) {
                lastFrame = stamp;
                const q = window_();
                const w = 640, h = Math.round(q.sh * (640 / q.sw));
                if (pv.width !== w || pv.height !== h) { pv.width = w; pv.height = h; }
                pctx.drawImage(v, q.sx, q.sy, q.sw, q.sh, 0, 0, w, h);
                const b = await new Promise(r => pv.toBlob(r, 'image/jpeg', 0.5));
                // Awaited, so the next frame is not encoded until this one is off the phone:
                // frames queued behind a slow link are frames that arrive already wrong.
                if (b) await fetch('/p/' + TOKEN + '/preview', { method: 'POST', body: b });
              }
            } catch (e) { /* a dropped preview frame is not worth a message */ }
            const spent = performance.now() - began;
            if (spent < MIN_FRAME_MS) await new Promise(r => setTimeout(r, MIN_FRAME_MS - spent));
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
          let reconnecting = false;
          while (running) {
            try {
              const r = await fetch('/p/' + TOKEN + '/poll?seq=' + settingsSeq);
              if (!r.ok) throw new Error('camera listener is restarting');
              if (reconnecting) {
                // A new server has no record of this phone's lenses yet. Re-introduce the camera
                // before applying its settings, then carry on without a new QR scan or camera tap.
                settingsSeq = -1;
                await sendCaps();
                reconnecting = false;
                say('Reconnected — ready for the next photo.', 'ok');
              }
              const j = await r.json();
              if (j.maxVideoSeconds) maxSecs = j.maxVideoSeconds;
              if (typeof j.seq === 'number' && j.seq !== settingsSeq) {
                settingsSeq = j.seq;
                if (typeof j.zoom === 'number') await applyZoom(j.zoom);
                // The old torch switch still works: it is a lamp held on, which is a different
                // thing from a flash and is genuinely useful for lining a shot up in a dim room.
                if (flashMode === 'off') await applyTorch(!!j.torch);
                if (typeof j.flash === 'string') flashMode = j.flash;
                if (typeof j.exposure === 'number' && j.exposure !== exposureEv) await applyExposure(j.exposure);
                if (typeof j.focus === 'string' && j.focus !== focusMode) await applyFocus(j.focus);
                if (typeof j.whiteBalance === 'string' && j.whiteBalance !== wbMode) await applyWhiteBalance(j.whiteBalance);
                if (typeof j.lens === 'string' && j.lens !== lensMode) await applyLens(j.lens);
                if (typeof j.facing === 'string' && j.facing !== facing) await applyFacing(j.facing);
                if (typeof j.level === 'boolean' && j.level !== levelOn) await applyLevel(j.level);
                paintRig(!!caps_().torch);
              }
              if (j.shoot || j.command === 'shoot') await shoot();
              else if (j.command === 'record-start') startRecording();
              else if (j.command === 'record-stop') stopRecording();
            } catch (e) {
              say('Lost the connection to your computer — retrying…', 'bad');
              reconnecting = true;
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

        // Tap the picture to focus there. Ignored while pinching, which is two fingers and a
        // different intention entirely.
        v.addEventListener('click', e => {
          if (!running) return;
          const box = v.getBoundingClientRect();
          focusAt((e.clientX - box.left) / box.width, (e.clientY - box.top) / box.height);
        });

        document.addEventListener('click', e => {
          const flashBtn = e.target.closest ? e.target.closest('[data-flash]') : null;
          if (flashBtn) { flashMode = flashBtn.dataset.flash; paintRig(!!caps_().torch); return; }
          const lensBtn = e.target.closest ? e.target.closest('[data-lens]') : null;
          if (lensBtn) { applyLens(lensBtn.dataset.lens); return; }
        });
        document.getElementById('levelbtn').addEventListener('click', () => applyLevel(!levelOn));

        startBtn.addEventListener('click', begin);
        // Safari needs a tap before it will hand over a camera, so the button is the real entry
        // point; this only helps the browsers that do not.
        begin();
        </script>
        </body></html>
        """;
}
