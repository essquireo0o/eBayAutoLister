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
    double ZoomMin = 1, double ZoomMax = 1, string? Lenses = null, bool ZoomOptical = false);

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

public sealed class PhoneCapture(PhotoLibrary photos, ActionLog log, ClaudeService claude) : IAsyncDisposable
{
    /// <summary>Where the phone connects. Deliberately not 9332: that port is the app's alone.</summary>
    public const int Port = 9443;

    /// <summary>
    /// Plain HTTP, and deliberately so: the only thing on it is what a phone needs BEFORE it can
    /// open the HTTPS page at all — the certificate authority to trust. See the trust routes.
    /// </summary>
    /// <remarks>
    /// A PREFERENCE, not a promise. On the machine this was written on, 9444 was already held by
    /// an antivirus service on 0.0.0.0 — and Kestrel's ListenAnyIP does not fail when that
    /// happens. It binds the IPv6 half, reports itself started, and every IPv4 request goes to the
    /// other process instead, which accepts the connection and never answers. From the outside
    /// that is indistinguishable from the app being broken, and no error is logged anywhere. So
    /// the port is probed for real before Kestrel is told about it, and the one that answers is
    /// the one the QR code points at.
    /// </remarks>
    public const int TrustPortPreferred = 9444;

    /// <summary>The port the trust page is actually on, once the server has started.</summary>
    public static int TrustPort { get; private set; } = TrustPortPreferred;

    /// <summary>
    /// The first port at or after the preferred one that this machine will really give us on IPv4.
    /// </summary>
    private static int PickTrustPort()
    {
        for (var port = TrustPortPreferred; port < TrustPortPreferred + 12; port++)
        {
            try
            {
                // IPv4 explicitly: the failure this exists to catch is a port that is free on IPv6
                // and taken on IPv4, which is the half a phone actually uses.
                var probe = new TcpListener(IPAddress.Any, port);
                probe.Server.ExclusiveAddressUse = true;
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { /* somebody else has it — try the next one */ }
        }
        return TrustPortPreferred;   // nothing free; start anyway and let the error be visible
    }

    // Pairing is a relationship, not a server session. The QR secret lives in the seller's fixed
    // data home so a Chrome tab that was paired yesterday still has the right address after an
    // update restarts the executable today. It is disabled only by the explicit Disconnect
    // endpoint; ordinary shutdown leaves it ready to resume.
    private sealed record Pairing(string Token, bool Enabled);
    private static string PairingPath => Path.Combine(AppPaths.DataHome, "phone-camera-pairing.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    /// <summary>The last request that reached either phone listener, whatever it asked for.</summary>
    private volatile string _lastContact = "";

    /// <summary>When each device was last written to the log, so one phone cannot flood it.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _contactSeen = new();

    private string _token = "";
    private DateTimeOffset _lastSeen;
    private TaskCompletionSource<bool>? _shutter;   // completed when the desktop presses Snap
    private readonly List<string> _shots = [];      // photo-library urls, oldest first

    /// <summary>When a photograph last arrived from a phone that has no live channel. See PhoneSending.</summary>
    private DateTimeOffset _phoneSending = DateTimeOffset.MinValue;
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
    private bool _zoomOptical;              // the lens moved, rather than the frame being cropped
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
        double ZoomMin = 1, double ZoomMax = 1, string Lenses = "",
        // The one-time setup a phone needs before it can open the camera page at all. Carried on
        // every status so the screen can offer it without a second round trip — and so it is
        // right there when Safari refuses, which is the moment the seller needs it.
        string TrustUrl = "", string TrustQrSvg = "",
        // Whether the zoom on screen is the lens moving or the frame being cropped. The desk
        // shows it because the two are genuinely different pictures — one keeps every pixel the
        // sensor has — and because a chip that only ever showed the number the desk asked for is
        // how a zoom that did nothing went unnoticed.
        bool ZoomOptical = false,
        // The last request that reached this machine's phone listeners, or "" if none ever has.
        // See the middleware in MapRoutes for why an unanswered question needed its own field.
        string LastContact = "",
        // A phone on the NO-CERTIFICATE page can never be "connected": that flag means a live lens
        // and a working desktop shutter, and a file input has neither. It is still very much here —
        // photographs are arriving — and reporting that as "No camera yet" is why the feature read
        // as dead while it was working. Two states, because there genuinely are two.
        bool PhoneSending = false);

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
        // One QR works in Safari whether this phone has trusted the local authority yet or not.
        // The HTTP bootstrap contains no pairing token: it probes the HTTPS certificate, passes a
        // trusted phone straight through, and shows the one-time Apple setup when Safari refuses.
        var url = LaunchUrl;
        return new(true, url, _phoneEverConnected && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(20),
                   _shots.Count, [.. _shots], QrCode.ToSvg(url), null, LatestPreview() is not null,
                   _phoneEverConnected, _recording, [.. _videos], MaxVideoSeconds,
                   _zoom, _torch, _canTorch, _capWidth, _capHeight,
                   _flash, _exposure, _focus, _whiteBalance, _lens, _facing, _level,
                   _canExposure, _canFocus, _canMacro, _canWhiteBalance, _canTap, _canMultiCamera,
                   _zoomMin, _zoomMax, _lenses,
                   TrustUrl(), QrCode.ToSvg(TrustUrl()), _zoomOptical, _lastContact,
                   PhoneSending: DateTimeOffset.UtcNow - _phoneSending < TimeSpan.FromMinutes(3));
    }

    private string PublicUrl => $"https://{LocalAddress()}:{Port}/p/{_token}";
    // The primary QR must always take a photograph. Safari can open its native camera from this
    // plain-HTTP file-input page with no profile or certificate; the optional /trust route remains
    // available from that page for sellers who specifically want the desktop live viewfinder.
    private string LaunchUrl => $"http://{LocalAddress()}:{TrustPort}/c/{_token}";

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
                // ── The chicken and the egg ──────────────────────────────────────────────────
                // The camera page is HTTPS because a browser gives no camera to an insecure page.
                // Its certificate is local, so the phone does not trust it, so the phone has to be
                // given the authority to trust — and it cannot fetch that over the connection it
                // does not trust yet. Safari makes that absolute: with a certificate it will not
                // accept there is no "visit this website anyway", so the seller is simply stuck,
                // and telling them to install Chrome is not an answer.
                //
                // So the trust arrives over plain HTTP on the next port, carrying only three
                // things: a page of instructions, an Apple configuration profile, and the
                // authority's public certificate. Nothing here is secret — a public key is
                // public — and there is no token, because a phone that cannot reach this cannot
                // reach anything else on this machine either.
                TrustPort = PickTrustPort();
                k.ListenAnyIP(TrustPort);
                k.Limits.MaxRequestBodySize = 25 * 1024 * 1024;   // a phone photo, with room to spare
            });

            var web = builder.Build();
            // Preview frames use one persistent binary connection. Without this middleware the
            // WebSocket route below is merely an HTTP endpoint and Safari receives a 400 before
            // the first encoded frame leaves the phone.
            web.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            });
            MapRoutes(web);
            await web.StartAsync(ct);
            _app = web;

            log.Add("Info", "Phone camera ready", PublicUrl);
            log.Add("Info", "Phone camera trust page", TrustUrl()
                + (TrustPort == TrustPortPreferred ? "" : $" (port {TrustPortPreferred} was taken)"));
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
        // ── Who actually reached this machine ────────────────────────────────────────────────
        //
        // Every other line this class logs describes something that had already gone right: a
        // photo saved, a pairing read, a camera started. So when the owner says "using my phone
        // does not work at all", there is nothing whatsoever to look at — and the two causes need
        // opposite fixes. A request arriving and no camera appearing is a certificate or a page
        // problem, on this side, fixable here. No request arriving at all is the phone never
        // reaching the machine: wrong network, asleep, cellular, a captive portal — none of which
        // any amount of work on this code will touch.
        //
        // A whole day went into guessing between those two. One line per device per minute ends
        // it, and it costs a dictionary lookup on a listener that serves a handful of requests.
        web.Use(async (ctx, next) =>
        {
            var ip   = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : "/";
            var now  = DateTimeOffset.Now;

            _lastContact = $"{ip} asked for {path} on port {ctx.Connection.LocalPort} at {now:HH:mm:ss}";

            // First sight of a device, then at most once a minute after that. A phone polling the
            // viewfinder would otherwise write a log line every second and bury everything else.
            if (!_contactSeen.TryGetValue(ip, out var last) || now - last > TimeSpan.FromMinutes(1))
            {
                _contactSeen[ip] = now;
                log.Add("Info", "A phone reached this computer",
                    $"{ip} asked for {path} on port {ctx.Connection.LocalPort}");
            }

            await next();
        });

        // The public surface is deliberately tiny: one blank pixel proves Safari accepts this
        // machine's certificate, and / redirects to the paired camera only after that HTTPS
        // connection already succeeded. The pairing token never crosses the HTTP bootstrap.
        web.MapGet("/ready.gif", () => Results.File(
            Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="), "image/gif"));
        web.MapGet("/", (HttpContext ctx) => ctx.Connection.LocalPort == Port
            ? Results.Redirect(PublicUrl)
            : Results.Redirect("/start"));

        // Every camera control and byte of imagery remains under /p/{token}. A caller without it
        // gets a flat 404 and learns nothing.
        bool Ok(string token) => _token.Length > 0 && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token.PadRight(64)[..64]),
            System.Text.Encoding.ASCII.GetBytes(_token.PadRight(64)[..64]));

        void StorePreview(byte[] frame)
        {
            if (frame.Length <= 500) return;
            _preview = frame;
            _previewAt = DateTimeOffset.UtcNow;
            _lastSeen = DateTimeOffset.UtcNow;
            _phoneEverConnected = true;
            Interlocked.Increment(ref _previewSeq);
            _previewReady.Set();
        }

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
                    _zoomOptical = caps.ZoomOptical;
                    _lenses = caps.Lenses ?? "";
                    // A phone with no lamp cannot be left holding a flash mode it will never fire.
                    if (!_canTorch) { _flash = "off"; _torch = false; }
                    _settingsSeq++;   // the desktop's panel redraws off this
                }
            }
            catch { /* a phone that cannot describe itself still takes photographs */ }
            return Results.Ok(new { ok = true });
        });

        // Viewfinder frames. Small JPEGs, frequent, and never written to disk. The WebSocket is
        // the normal path: one TLS handshake and one connection for the whole camera session.
        // The phone drops a preview when that connection already has a frame buffered, so a slow
        // wifi link lowers frame rate instead of showing the seller where the camera used to be.
        web.Map("/p/{token}/preview-stream", async context =>
        {
            var token = Convert.ToString(context.Request.RouteValues["token"]) ?? "";
            if (!Ok(token)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[512 * 1024];
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open &&
                       !context.RequestAborted.IsCancellationRequested)
                {
                    var count = 0;
                    System.Net.WebSockets.ValueWebSocketReceiveResult part;
                    do
                    {
                        if (count == buffer.Length)
                        {
                            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig,
                                "preview frame too large", context.RequestAborted);
                            return;
                        }
                        part = await socket.ReceiveAsync(buffer.AsMemory(count), context.RequestAborted);
                        if (part.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return;
                        count += part.Count;
                    }
                    while (!part.EndOfMessage);

                    if (part.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary && count > 500)
                        StorePreview(buffer.AsSpan(0, count).ToArray());
                }
            }
            catch (OperationCanceledException) { /* phone page closed */ }
            catch (System.Net.WebSockets.WebSocketException) { /* wifi changed or phone locked */ }
        });

        // Compatibility fallback for a browser or network that refuses WebSockets.
        web.MapPost("/p/{token}/preview", async (string token, HttpRequest req) =>
        {
            if (!Ok(token)) return Results.NotFound();
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            StorePreview(ms.ToArray());
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
            // Deliberately NOT _phoneEverConnected: that enables the desktop Snap button, which
            // works by setting a command and waiting on /poll — a channel this path does not have.
            // Lighting it up here would make Snap time out on "the phone didn't send a photo",
            // which is a button that lies instead of a panel that under-reports.
            _phoneSending = DateTimeOffset.UtcNow;
            var url = await photos.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, bytes, "jpg");
            _shots.Add(url);
            _shotArrived.Set();
            log.Add("Info", "Phone camera photo", url);
            return Results.Ok(new { url });
        });

        // ── Trust, over plain HTTP ───────────────────────────────────────────────────────────
        //
        // Three routes, no token, nothing secret. They exist because the HTTPS page cannot serve
        // the thing that makes the HTTPS page openable. See the TrustPort comment in StartAsync.
        //
        // WHY A CONFIGURATION PROFILE AND NOT JUST THE .CER. Safari on iOS will download a bare
        // certificate, but what it does with it depends on the version and it is easy to end up
        // with a file in Downloads and no trust. A .mobileconfig is the path iOS is built for:
        // tapping it puts "Profile Downloaded" in Settings, and installing it puts the authority
        // in the certificate store under this app's name, where the seller can also find it later
        // to remove. It still needs the second step — Settings, General, About, Certificate Trust
        // Settings — because iOS does not let any downloaded root become fully trusted without a
        // person saying so, and no installer on the computer can say it for them.

        // The primary QR lands here. A trusted Safari is forwarded automatically; an untrusted one
        // stays on the same useful page and gets the installer instead of a dead certificate error.
        web.MapGet("/start", () => Results.Content(TrustPageHtml(autoStart: true), "text/html; charset=utf-8"));
        web.MapGet("/trust", () => Results.Content(TrustPageHtml(autoStart: false), "text/html; charset=utf-8"));

        // The way out of the certificate entirely. A file input is not a secure-context
        // feature, so this page opens the iPhone camera from plain HTTP with nothing to
        // install. Linked from the setup page above, for the phone that will not be set up.
        // The listing, written from the phone, on the photograph just taken.
        //
        // The seller is standing over the item with it in their hand — which is the moment they can
        // answer what the AI cannot see, and the moment they are least able to walk to a computer.
        // So the phone gets the same call the desktop makes: title, category, condition, item
        // specifics and a starting price, from the shot that was sent seconds ago.
        //
        // The photograph is NOT re-uploaded. It is already in the library, saved by the handler
        // above, and re-sending two megabytes over a phone's uplink to analyse what the computer is
        // already holding would be the slowest possible way to ask.
        web.MapPost("/c/{token}/listing", async (string token, PhoneListingAsk ask, CancellationToken ct) =>
        {
            if (!Ok(token)) return Results.NotFound();
            _lastSeen = DateTimeOffset.UtcNow;

            var url = (ask?.Url ?? "").Trim();
            if (url.Length == 0) url = _shots.Count > 0 ? _shots[^1] : "";
            if (url.Length == 0)
                return Results.BadRequest(new { error = "Take a photo first — there is nothing to write a listing from." });

            var bytes = photos.ReadPhoto(url);
            if (bytes is null)
                return Results.BadRequest(new { error = "That photo is no longer in the library." });

            try
            {
                var listing = await claude.AnalyzeImageAsync(Convert.ToBase64String(bytes), "image/jpeg", ct);
                log.Add("Info", "Phone wrote a listing", $"{url} — {listing.Title}");

                // Only what a phone screen can hold and a seller can check standing up. The whole
                // draft is on the computer; this is the part worth reading before walking back to it.
                return Results.Ok(new
                {
                    ok = true,
                    title      = listing.Title,
                    category   = listing.Category,
                    condition  = ConditionWords(listing.Condition),
                    price      = listing.Price,
                    brand      = listing.Brand,
                    photo      = url,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // The photograph is already saved and is not lost with the listing that failed.
                log.Add("Warning", "Phone listing failed", $"{url} — {ex.Message}");
                return Results.Json(new { error = "The AI could not write this one. The photo is saved on your computer." },
                                    statusCode: 502);
            }
        });

        web.MapGet("/c/{token}", (string token, HttpContext ctx) =>
        {
            if (!Ok(token)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Content(CameraFreePageHtml(), "text/html; charset=utf-8");
        });

        // The name matters: iOS decides what to do with this from the extension and the type.
        web.MapGet("/trust.mobileconfig", () =>
            Results.File(MobileConfig(), "application/x-apple-aspen-config", "ing-photo-box.mobileconfig"));

        // For everything that is not an iPhone — Android, a second computer, a browser being
        // configured by hand. DER, because that is what Windows and Android both expect.
        web.MapGet("/ing-photo-box-ca.cer", () =>
        {
            using var ca = Authority();
            return Results.File(ca.Export(X509ContentType.Cert), "application/x-x509-ca-cert", "ing-photo-box-ca.cer");
        });

        // Anything else on this port is somebody's browser guessing. Send them to the one page.
        web.MapFallback(ctx =>
        {
            if (ctx.Connection.LocalPort != TrustPort) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
            ctx.Response.Redirect("/trust");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A self-signed certificate for this machine's address, kept between runs so a phone that
    /// has already trusted it does not have to be asked again every time the app restarts.
    /// </summary>

    /// <summary>Which photograph to write the listing from. Empty means the one just sent.</summary>
    public sealed class PhoneListingAsk
    {
        public string? Url { get; set; }
    }

    /// <summary>eBay's condition codes are for eBay. A person holding the item reads words.</summary>
    private static string ConditionWords(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "NEW" or "NEW_OTHER" or "NEW_WITH_TAGS"      => "New",
        "NEW_WITH_DEFECTS"                            => "New with defects",
        "MANUFACTURER_REFURBISHED" or "CERTIFIED_REFURBISHED" => "Refurbished",
        "SELLER_REFURBISHED"                          => "Refurbished by seller",
        "USED_EXCELLENT"                              => "Used — excellent",
        "USED_VERY_GOOD"                              => "Used — very good",
        "USED_GOOD"                                   => "Used — good",
        "USED_ACCEPTABLE"                             => "Used — acceptable",
        "FOR_PARTS_OR_NOT_WORKING"                    => "For parts or not working",
        _                                             => "Used",
    };

    /// <summary>The address a phone types to be handed the authority, before it trusts anything.</summary>
    public static string TrustUrl() => $"http://{LocalAddress()}:{TrustPort}/trust";

    /// <summary>
    /// An Apple configuration profile carrying this machine's camera authority as a root.
    /// </summary>
    /// <remarks>
    /// The UUIDs are derived from the authority's own thumbprint rather than made fresh each
    /// request, so installing it twice replaces the profile instead of stacking a second copy in
    /// the seller's Settings — and so a phone that already has it recognises it.
    /// </remarks>
    /// <remarks>Public so it can be tested: it only ever READS the authority, never writes one.</remarks>
    public static byte[] MobileConfig()
    {
        using var ca = Authority();
        var der = Convert.ToBase64String(ca.Export(X509ContentType.Cert));

        // Two stable, different UUIDs from one thumbprint: the profile and the payload inside it
        // must not share an identifier.
        var seed = Convert.FromHexString(ca.Thumbprint);
        var profileId = new Guid(System.Security.Cryptography.MD5.HashData(seed)).ToString().ToUpperInvariant();
        var payloadId = new Guid(System.Security.Cryptography.MD5.HashData([.. seed, 1])).ToString().ToUpperInvariant();

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>PayloadType</key><string>com.apple.security.root</string>
                  <key>PayloadVersion</key><integer>1</integer>
                  <key>PayloadIdentifier</key><string>com.ingmining.photobox.ca</string>
                  <key>PayloadUUID</key><string>{payloadId}</string>
                  <key>PayloadDisplayName</key><string>ING Photo Box camera certificate</string>
                  <key>PayloadDescription</key><string>Lets this iPhone open the camera page on your own computer without a warning.</string>
                  <key>PayloadCertificateFileName</key><string>ing-photo-box-ca.cer</string>
                  <key>PayloadContent</key>
                  <data>{der}</data>
                </dict>
              </array>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadVersion</key><integer>1</integer>
              <key>PayloadIdentifier</key><string>com.ingmining.photobox</string>
              <key>PayloadUUID</key><string>{profileId}</string>
              <key>PayloadDisplayName</key><string>ING Photo Box camera</string>
              <key>PayloadOrganization</key><string>ING Mining LLC</string>
              <key>PayloadDescription</key><string>Trusts the camera page served by ING Listing Engine on your own computer. It grants nothing else, and it works only on your own network.</string>
              <key>PayloadRemovalDisallowed</key><false/>
            </dict>
            </plist>
            """;

        return System.Text.Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// The camera that needs no certificate at all.
    /// </summary>
    /// <remarks>
    /// Safari will not give a page the live camera (<c>getUserMedia</c>) without a secure context,
    /// which is the entire reason the certificate, the authority and the three Settings taps exist.
    /// A FILE INPUT has no such requirement: <c>capture="environment"</c> opens the iPhone's own
    /// camera from a plain-HTTP page and hands the bytes back, with no trust decision, no profile,
    /// and nothing for the owner to install.
    /// <para>
    /// What it costs is the live viewfinder and the desktop shutter — the phone takes the photo,
    /// the computer does not fire it. What it buys is a Safari that works on the first tap, on any
    /// iPhone, including one that will never be set up. So it is offered beside the trusted route
    /// rather than instead of it.
    /// </para>
    /// <para>
    /// The bytes are re-encoded to JPEG in a canvas before they are sent. Not compression for its
    /// own sake: an iPhone hands back HEIC often enough, and every reader downstream — the AI
    /// enhance pass, the eBay upload — expects a JPEG. Decoding it on the phone, where the codec is
    /// native, is cheaper and far more reliable than teaching the desktop to convert.
    /// </para>
    /// </remarks>
    private string CameraFreePageHtml() => $$"""
        <!DOCTYPE html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <title>ING Photo Box — phone camera</title>
        <style>
          :root{color-scheme:dark}
          *{box-sizing:border-box;margin:0;padding:0}
          body{background:#050707;color:#f7fbfb;font:16px/1.55 -apple-system,BlinkMacSystemFont,system-ui,sans-serif;
               padding:calc(env(safe-area-inset-top) + 22px) 20px 40px;max-width:640px;margin:auto}
          h1{font-size:23px;line-height:1.25;letter-spacing:-.01em;margin-bottom:6px}
          .sub{color:#9fb1b4;margin-bottom:22px}
          .btn{display:block;text-align:center;background:linear-gradient(145deg,#f0c453,#b67d12);color:#151006;
               font-weight:800;padding:20px;border-radius:14px;text-decoration:none;margin:0 0 12px;
               font-size:18px;border:0;width:100%;font-family:inherit;cursor:pointer}
          .go{display:block;text-align:center;border:1px solid #2b3a3d;color:#f7fbfb;padding:15px;border-radius:14px;
              text-decoration:none;margin-top:10px;background:#111719;font:inherit;width:100%;cursor:pointer}
          .count{margin:22px 0 10px;font-weight:700}
          .row{display:flex;align-items:center;gap:12px;padding:10px 0;border-top:1px solid #1d2729;font-size:14px}
          .row img{width:46px;height:46px;object-fit:cover;border-radius:8px;background:#111719}
          .row span{color:#9fb1b4}
          .ok{color:#7fd8a0}.bad{color:#ff9c8a}
          .listing{border:1px solid #2b3a3d;border-radius:14px;background:#0d1213;padding:16px;margin:4px 0 10px}
          .listing h2{font-size:17px;line-height:1.35;margin-bottom:10px}
          .listing dl{display:grid;grid-template-columns:auto 1fr;gap:6px 14px;font-size:14px}
          .listing dt{color:#9fb1b4}
          .listing dd{color:#f7fbfb;font-weight:600}
          .listing .after{margin-top:12px;color:#9fb1b4;font-size:13px;line-height:1.45}
          .why{margin-top:30px;padding-top:18px;border-top:1px solid #1d2729;color:#9fb1b4;font-size:14px}
          .why a{color:#f0c453}
        </style></head><body>
          <h1>Take photos with this phone</h1>
          <p class="sub">No certificate, no setup. Tap, shoot, and the photo lands on your computer.</p>

          <button class="btn" id="shoot" type="button">Take a photo</button>
          <button class="go" id="pick" type="button">Choose photos already taken</button>
          <input id="cam" type="file" accept="image/*" capture="environment" hidden>
          <input id="lib" type="file" accept="image/*" multiple hidden>

          <div class="count" id="count">Nothing sent yet.</div>

          <button class="btn" id="write" type="button" hidden>Write the listing with AI</button>
          <div id="listing" class="listing" hidden></div>

          <div id="shots"></div>

          <p class="why">Keep this page open and shoot as many as you like — each one appears on the
             computer as it arrives. For the live viewfinder and a shutter button on the computer,
             the one-time certificate setup is <a href="/trust">on this page</a>.</p>

          <script>
            const TOKEN = {{System.Text.Json.JsonSerializer.Serialize(_token)}};
            const shots = document.getElementById('shots');
            const count = document.getElementById('count');
            const cam = document.getElementById('cam');
            const lib = document.getElementById('lib');
            let sent = 0, failed = 0;

            document.getElementById('shoot').addEventListener('click', () => cam.click());
            document.getElementById('pick').addEventListener('click', () => lib.click());
            cam.addEventListener('change', () => take(cam));
            lib.addEventListener('change', () => take(lib));

            function tally() {
              count.textContent = failed
                ? sent + ' sent, ' + failed + ' failed'
                : (sent === 1 ? '1 photo sent to your computer' : sent + ' photos sent to your computer');
            }

            function take(input) {
              const files = [...input.files];
              input.value = '';                    // so the same shot can be retaken immediately
              files.forEach(send);
            }

            // An iPhone photograph is often HEIC and always large. Both are settled here, on the
            // device that already has the codec, rather than on the computer that does not.
            async function toJpeg(file) {
              const cap = 3000;
              let width, height, draw;
              try {
                const bitmap = await createImageBitmap(file);
                width = bitmap.width; height = bitmap.height;
                draw = (c, w, h) => c.drawImage(bitmap, 0, 0, w, h);
              } catch (e) {
                // Older Safari cannot make a bitmap from a HEIC File, but it can always show one.
                const url = URL.createObjectURL(file);
                try {
                  const img = await new Promise((ok, no) => {
                    const i = new Image();
                    i.onload = () => ok(i);
                    i.onerror = () => no(new Error('this phone could not read the photo'));
                    i.src = url;
                  });
                  width = img.naturalWidth; height = img.naturalHeight;
                  draw = (c, w, h) => c.drawImage(img, 0, 0, w, h);
                } finally { setTimeout(() => URL.revokeObjectURL(url), 10000); }
              }
              const scale = Math.min(1, cap / Math.max(width, height));
              const canvas = document.createElement('canvas');
              canvas.width = Math.round(width * scale);
              canvas.height = Math.round(height * scale);
              draw(canvas.getContext('2d'), canvas.width, canvas.height);
              const blob = await new Promise(r => canvas.toBlob(r, 'image/jpeg', 0.92));
              if (!blob) throw new Error('this phone could not encode the photo');
              return { blob: blob, thumb: canvas.toDataURL('image/jpeg', 0.4) };
            }

            const write = document.getElementById('write');
            const listing = document.getElementById('listing');
            let lastPhoto = '';

            write.addEventListener('click', async () => {
              write.disabled = true;
              const was = write.textContent;
              // The wait is real — this is the same full listing call the computer makes, measured at
              // 64 seconds on a real photo. Saying so beats a button that looks stuck.
              write.textContent = 'Reading the photo... about a minute';
              listing.hidden = true;
              try {
                const res = await fetch('/c/' + TOKEN + '/listing', {
                  method: 'POST', headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ url: lastPhoto }),
                });
                const d = await res.json();
                if (!res.ok || !d.ok) throw new Error(d.error || 'the AI did not answer');
                listing.innerHTML =
                  '<h2>' + esc(d.title || 'Untitled') + '</h2><dl>'
                  + row('Condition', d.condition)
                  + row('Price', d.price > 0 ? '$' + Number(d.price).toFixed(2) : '')
                  + row('Category', d.category)
                  + row('Brand', d.brand)
                  + '</dl><p class="after">Saved as a draft on your computer. Open AI Listing there to '
                  + 'check the price against real sold comps and publish it.</p>';
                listing.hidden = false;
              } catch (e) {
                listing.innerHTML = '<p class="after">Could not write it - ' + esc(e.message)
                  + '. Your photo is saved on the computer either way.</p>';
                listing.hidden = false;
              } finally {
                write.disabled = false;
                write.textContent = was;
              }
            });

            function esc(t) {
              return String(t == null ? '' : t).replace(/[&<>"]/g, c =>
                ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
            }
            function row(label, value) {
              return value ? '<dt>' + label + '</dt><dd>' + esc(value) + '</dd>' : '';
            }

            async function send(file) {
              const row = document.createElement('div');
              row.className = 'row';
              const pic = document.createElement('img');
              const label = document.createElement('span');
              label.textContent = 'Preparing...';
              row.append(pic, label);
              shots.prepend(row);
              try {
                const shot = await toJpeg(file);
                pic.src = shot.thumb;
                label.textContent = 'Sending...';
                const res = await fetch('/p/' + TOKEN + '/photo', { method: 'POST', body: shot.blob });
                if (!res.ok) throw new Error('the computer refused it (' + res.status + ')');
                const saved = await res.json().catch(() => ({}));
                if (saved.url) lastPhoto = saved.url;
                sent++;
                label.textContent = 'On your computer';
                label.className = 'ok';
                write.hidden = false;
              } catch (e) {
                failed++;
                label.textContent = 'Failed - ' + e.message;
                label.className = 'bad';
              }
              tally();
            }
          </script>
        </body></html>
        """;

    /// <summary>
    /// The page a phone lands on before it can open the camera. Written for somebody standing at a
    /// bench holding a phone: what to tap, in order, and what each step is for.
    /// </summary>
    private string TrustPageHtml(bool autoStart)
    {
        var camera = $"https://{LocalAddress()}:{Port}/";
        var ready = $"https://{LocalAddress()}:{Port}/ready.gif";
        var startScript = autoStart ? "tryCamera();" : "showSetup();";
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
            <title>Trust the ING Photo Box camera</title>
            <style>
              :root{color-scheme:dark}
              *{box-sizing:border-box;margin:0;padding:0}
              body{background:#050707;color:#f7fbfb;font:16px/1.55 -apple-system,BlinkMacSystemFont,system-ui,sans-serif;
                   padding:calc(env(safe-area-inset-top) + 22px) 20px 40px;max-width:640px;margin:auto}
              h1{font-size:23px;line-height:1.25;letter-spacing:-.01em;margin-bottom:6px}
              .sub{color:#9fb1b4;margin-bottom:22px}
              .check{padding:16px;border:1px solid #2b3a3d;border-radius:14px;background:#0d1213;margin-bottom:18px}
              .check b{display:block;margin-bottom:3px}.check span{color:#9fb1b4;font-size:14px}
              .hidden{display:none}
              ol{list-style:none;counter-reset:s}
              li{counter-increment:s;position:relative;padding:0 0 22px 46px;border-left:2px solid #1d2729;margin-left:14px}
              li:last-child{border-left-color:transparent}
              li::before{content:counter(s);position:absolute;left:-15px;top:-2px;width:28px;height:28px;border-radius:50%;
                         display:grid;place-items:center;background:linear-gradient(145deg,#f0c453,#b67d12);
                         color:#151006;font-weight:900;font-size:13px}
              b{color:#fff}
              .btn{display:block;text-align:center;background:linear-gradient(145deg,#f0c453,#b67d12);color:#151006;
                   font-weight:800;padding:16px;border-radius:14px;text-decoration:none;margin:6px 0 4px}
              .go{display:block;text-align:center;border:1px solid #2b3a3d;color:#f7fbfb;padding:15px;border-radius:14px;
                  text-decoration:none;margin-top:10px}
              button.go{width:100%;background:#111719;font:inherit;cursor:pointer}
              .why{margin-top:30px;padding-top:18px;border-top:1px solid #1d2729;color:#9fb1b4;font-size:14px}
              code{background:#111719;padding:2px 6px;border-radius:6px;font-size:13px}
            </style></head><body>
              <h1>ING Photo Box for Safari</h1>
              <p class="sub">One QR checks the secure camera connection first. If this iPhone is already
                 set up, the camera opens automatically.</p>
              <div class="check" id="check"><b id="check-title">Checking the secure camera…</b>
                <span id="check-note">Keep this page open for a moment.</span></div>
              <a class="go" href="/c/{{_token}}" style="margin-bottom:4px">Skip all this — take photos with this phone &rsaquo;</a>
              <p class="sub" style="font-size:14px;margin-bottom:22px">Uses the iPhone's own camera. No certificate,
                 nothing to install, works right now. You tap the shutter instead of the computer.</p>
              <section id="setup" class="hidden">
              <h2>One-time setup for this iPhone</h2>
              <p class="sub">Safari requires HTTPS before it will allow the camera. Apple does not let a
                 downloaded local certificate become trusted silently, so these two Settings approvals
                 are the only manual part.</p>
              <ol>
                <li><b>Download the profile.</b>
                    <a class="btn" href="/trust.mobileconfig">Download the certificate</a>
                    Safari will say <b>Profile Downloaded</b>. Install it within eight minutes.</li>
                <li><b>Install it.</b> Open <b>Settings</b> — <b>Profile Downloaded</b> is near the top.
                    Tap it, then <b>Install</b>, and enter your passcode.</li>
                <li><b>Turn trust on.</b> Still in Settings: <b>General</b> &rsaquo; <b>About</b> &rsaquo;
                    <b>Certificate Trust Settings</b>, and switch on
                    <b>ING Photo Box camera authority</b>.
                    <br>iPhones make you do this last step by hand for any certificate you install —
                    no app on your computer can do it for you.</li>
                <li><b>Open the camera.</b>
                    <button class="go" id="retry" type="button">Check again and open camera &rsaquo;</button>
                    Return to this Safari page after Settings. It checks again automatically, so the
                    same QR keeps working after app updates and restarts.</li>
              </ol>
              <p class="why"><b>If iOS blocks profile installation:</b> try again at a familiar location.
                 Stolen Device Protection can delay security changes away from familiar locations.</p>
              <p class="why"><b>What you are trusting.</b> One certificate, made by the copy of ING
                 Listing Engine running on your own computer, naming only that computer's address on
                 your own network (<code>{{LocalAddress()}}</code>). It is not a password, it grants
                 nothing on the internet, and you can remove it any time under
                 Settings &rsaquo; General &rsaquo; VPN &amp; Device Management.</p>
              </section>
              <script>
                const CAMERA = {{System.Text.Json.JsonSerializer.Serialize(camera)}};
                const READY = {{System.Text.Json.JsonSerializer.Serialize(ready)}};
                const setup = document.getElementById('setup');
                const title = document.getElementById('check-title');
                const note = document.getElementById('check-note');
                let attempt = 0;
                function showSetup() {
                  setup.classList.remove('hidden');
                  title.textContent = 'Safari needs the one-time certificate setup';
                  note.textContent = 'Nothing is wrong with the camera. Complete the steps below, then return here.';
                }
                function tryCamera() {
                  const mine = ++attempt;
                  setup.classList.add('hidden');
                  title.textContent = 'Checking the secure camera…';
                  note.textContent = 'Already trusted phones open automatically.';
                  const image = new Image();
                  const timer = setTimeout(() => { if (mine === attempt) showSetup(); }, 2800);
                  image.onload = () => {
                    if (mine !== attempt) return;
                    clearTimeout(timer);
                    title.textContent = 'Secure connection ready';
                    note.textContent = 'Opening the camera…';
                    location.replace(CAMERA);
                  };
                  image.onerror = () => { if (mine === attempt) { clearTimeout(timer); showSetup(); } };
                  image.src = READY + '?t=' + Date.now();
                }
                document.getElementById('retry').addEventListener('click', tryCamera);
                document.addEventListener('visibilitychange', () => {
                  if (!document.hidden && !setup.classList.contains('hidden')) setTimeout(tryCamera, 350);
                });
                window.addEventListener('focus', () => {
                  if (!setup.classList.contains('hidden')) setTimeout(tryCamera, 350);
                });
                {{startScript}}
              </script>
            </body></html>
            """;
    }

    // ── Why there is an authority here and not just a certificate ───────────────────────────
    //
    // The phone has to trust this server, and the only device whose opinion counts is the phone.
    // A certificate installed by the MSI lands in Windows' store, which an iPhone never consults —
    // so "install it in the installer so it doesn't ask" cannot work, and the trust has to be
    // carried to the phone once, by hand. See /trust on the plain-HTTP listener.
    //
    // Once per phone, though, not once per certificate. So this issues from a small LOCAL
    // AUTHORITY that is made once and kept: the phone trusts the authority, and every server
    // certificate signed by it afterwards is trusted too. That matters because the server
    // certificate is pinned to this machine's LAN address, and a laptop that moves between a
    // house and a workshop gets a new address — with a bare self-signed certificate, every one
    // of those was a new certificate and another trip through Settings.
    //
    // Apple's rules for the SERVER certificate (iOS 13+) are why the leaf looks the way it does:
    // a SAN (the common name is ignored), the serverAuth EKU, RSA 2048 or better, SHA-256 or
    // better, and no more than 825 days of validity. Miss any of them and Safari does not offer
    // "visit this website anyway" — it simply refuses, which is exactly the dead end the owner
    // hit. The authority is not a server certificate and is not bound by the 825 days, so it is
    // given ten years: re-trusting it every two years would be the same chore on a longer fuse.
    private const string KeyPass = "ing-photobox";   // guards files already inside the user's profile

    private static string CaPath   => Path.Combine(AppPaths.DataHome, "phone-camera-ca.pfx");
    private static string LeafPath => Path.Combine(AppPaths.DataHome, "phone-camera.pfx");

    /// <summary>The local authority, made once and kept. This is what a phone is asked to trust.</summary>
    public static X509Certificate2 Authority()
    {
        if (File.Exists(CaPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(CaPath, KeyPass,
                    X509KeyStorageFlags.Exportable);
                // Six months of headroom: a phone that trusted this must not be sent back to
                // Settings the week it expires.
                if (existing.NotAfter > DateTime.UtcNow.AddDays(180)) return existing;
            }
            catch { /* unreadable: make a new one below */ }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=ING Photo Box camera authority, O=ING Mining LLC", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = ca.Export(X509ContentType.Pfx, KeyPass);
        try { File.WriteAllBytes(CaPath, pfx); } catch { /* not being able to keep it is not fatal */ }
        return X509CertificateLoader.LoadPkcs12(pfx, KeyPass, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Every IPv4 address this machine answers on, so moving network does not re-issue.</summary>
    private static List<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                  && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                         .Select(a => a.Address)
                         .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
            {
                if (!found.Any(a => a.Equals(address))) found.Add(address);
            }
        }
        catch { /* one unreadable adapter must not cost the certificate */ }

        if (IPAddress.TryParse(LocalAddress(), out var primary) && !found.Any(a => a.Equals(primary)))
            found.Insert(0, primary);
        if (found.Count == 0) found.Add(IPAddress.Loopback);
        return found;
    }

    private static X509Certificate2 Certificate()
    {
        var wanted = LocalAddresses();
        using var ca = Authority();

        if (File.Exists(LeafPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(LeafPath, KeyPass);
                // Three ways a cached certificate stops being the right one to serve.
                //
                // It is running out. It no longer covers the address the phone will actually type
                // — a certificate for last week's IP fails with a name mismatch, and a name
                // mismatch is one of the errors Safari will not let anyone past. Or it was not
                // issued by the authority the phone has been asked to trust, which is every
                // certificate written before this machine had an authority at all: serving one of
                // those to a phone that has dutifully installed the profile would be the same
                // warning again, for a reason the seller could not possibly work out.
                if (existing.NotAfter > DateTime.UtcNow.AddDays(7)
                    && CoversAll(existing, wanted)
                    && string.Equals(existing.Issuer, ca.Subject, StringComparison.Ordinal))
                    return existing;
            }
            catch { /* unreadable or expired: make a new one below */ }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=ING Photo Box ({wanted[0]})", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var address in wanted) san.AddIpAddress(address);
        san.AddDnsName("photobox.local");
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());

        // 397 days: comfortably inside Apple's 825-day ceiling and inside the 398 the public CAs
        // settled on, so nothing downstream has to think about it.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter  = notBefore.AddDays(397);
        if (notAfter > ca.NotAfter) notAfter = new DateTimeOffset(ca.NotAfter).AddDays(-1);

        var serial = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;   // a positive serial; some stacks reject the negative reading

        using var issued = req.Create(ca, notBefore, notAfter, serial);
        using var leaf = issued.CopyWithPrivateKey(rsa);

        var pfx = leaf.Export(X509ContentType.Pfx, KeyPass);
        try { File.WriteAllBytes(LeafPath, pfx); } catch { /* not being able to cache it is not fatal */ }
        return X509CertificateLoader.LoadPkcs12(pfx, KeyPass);
    }

    /// <summary>Whether this certificate already names every address the phone might be given.</summary>
    private static bool CoversAll(X509Certificate2 cert, List<IPAddress> addresses)
    {
        try
        {
            var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            if (san is null) return false;
            var named = san.EnumerateIPAddresses().ToList();
            return addresses.All(a => named.Any(n => n.Equals(a)));
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static string PageHtml(string token) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <title>ING Photo Box — phone camera</title>
        <style>
          :root{color-scheme:dark;--gold:#d9a62f;--ink:#f7fbfb;--muted:#9fb1b4;--panel:#111719}
          *{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
          html,body{width:100%;height:100%;overflow:hidden;background:#050707}
          body{color:var(--ink);font:15px/1.4 -apple-system,BlinkMacSystemFont,"SF Pro Display",system-ui,sans-serif;
               overscroll-behavior:none;touch-action:manipulation}
          button{font:inherit}
          .camera{position:relative;width:100%;height:100dvh;max-width:680px;margin:auto;overflow:hidden;
                  background:#050707;display:grid;grid-template-rows:minmax(0,1fr) auto}
          .stage{position:relative;min-height:0;overflow:hidden;background:#000}
          video{display:block;width:100%;height:100%;background:#000;object-fit:cover}
          .camera-top{position:absolute;z-index:5;inset:0 0 auto;display:flex;align-items:center;justify-content:space-between;
                      gap:12px;padding:calc(env(safe-area-inset-top) + 12px) 16px 22px;
                      background:linear-gradient(180deg,rgba(0,0,0,.72),transparent)}
          .brand{display:flex;align-items:center;gap:9px;font-weight:800;letter-spacing:-.01em}
          .brandmark{display:grid;place-items:center;width:30px;height:30px;border-radius:9px;
                     background:linear-gradient(145deg,#f0c453,#b67d12);color:#151006;font-size:11px;font-weight:900}
          .live{display:flex;align-items:center;gap:6px;color:#dce7e8;font-size:12px;font-weight:700}
          .live::before{content:"";width:7px;height:7px;border-radius:50%;background:#54dd88;box-shadow:0 0 0 4px rgba(84,221,136,.13)}
          .stage-actions{display:flex;gap:9px}
          .iconbtn{display:grid;place-items:center;width:40px;height:40px;border:1px solid rgba(255,255,255,.22);
                   border-radius:50%;background:rgba(7,14,16,.58);backdrop-filter:blur(14px);color:#fff;font-size:18px}
          .iconbtn.on{background:var(--gold);border-color:var(--gold);color:#181004}
          .lensrow{position:absolute;z-index:5;left:50%;bottom:16px;transform:translateX(-50%);display:flex;gap:8px;
                   justify-content:center;padding:5px;border-radius:999px;background:rgba(5,10,11,.58);backdrop-filter:blur(14px)}
          .lens{width:44px;height:36px;border:0;border-radius:999px;background:transparent;color:#fff;
                font-size:13px;font-weight:800;padding:0}
          .lens.on{background:var(--gold);color:#201504}
          .status-float{position:absolute;z-index:4;left:50%;bottom:66px;transform:translateX(-50%);max-width:90%;
                        padding:7px 12px;border-radius:999px;background:rgba(4,10,11,.62);backdrop-filter:blur(12px);
                        color:#e9f1f1;font-size:12px;font-weight:650;text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
          .status-float.ok{color:#7de3a4}.status-float.busy{color:#f3d888}.status-float.bad{color:#ff9e91}
          .dock{position:relative;z-index:6;padding:12px 18px calc(env(safe-area-inset-bottom) + 14px);background:#0b1012;
                border-top:1px solid rgba(255,255,255,.08)}
          .shutterrow{display:grid;grid-template-columns:64px 1fr 64px;align-items:center;min-height:84px}
          .thumb,.controls-toggle{justify-self:center;width:48px;height:48px;border:1px solid rgba(255,255,255,.16);
                                  border-radius:13px;background:#172023;color:#dce7e8;overflow:hidden}
          .thumb{padding:0;position:relative}.thumb img{width:100%;height:100%;object-fit:cover}.thumb span{font-size:19px}
          .controls-toggle{font-size:21px}
          .shutter{justify-self:center;width:74px;height:74px;padding:5px;border:3px solid #fff;border-radius:50%;background:transparent}
          .shutter span{display:block;width:100%;height:100%;border-radius:50%;background:#fff;transition:transform .08s ease,background .15s ease}
          .shutter:active span{transform:scale(.88);background:var(--gold)}
          .capture-meta{display:flex;align-items:center;justify-content:center;gap:9px;min-height:20px;color:#829497;font-size:11px}
          .capture-meta .count{color:#c8d4d5}.awake{max-width:70%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
          .awake.warn{color:#e9c66e}
          .controls-sheet{position:absolute;z-index:8;left:10px;right:10px;bottom:calc(100% - 8px);max-height:min(54dvh,430px);
                          overflow:auto;padding:18px;border:1px solid rgba(255,255,255,.12);border-radius:20px;
                          background:rgba(16,23,25,.97);box-shadow:0 -18px 50px rgba(0,0,0,.38);
                          transform:translateY(18px);opacity:0;pointer-events:none;transition:.2s ease}
          .controls-sheet.open{transform:none;opacity:1;pointer-events:auto}
          .sheet-head{display:flex;align-items:center;justify-content:space-between;margin-bottom:14px}
          .sheet-head h2{font-size:17px}.sheet-head button{border:0;background:transparent;color:#aebfc1;font-size:24px;padding:2px 6px}
          .rig{display:flex;flex-direction:column;gap:13px}
          .rigrow{display:flex;align-items:center;gap:7px;flex-wrap:wrap}
          .riglab{font-size:10px;color:#819396;min-width:68px;text-transform:uppercase;letter-spacing:.1em;font-weight:800}
          .pill{padding:8px 12px;font-size:12px;border-radius:999px;background:#182326;color:#d5e1e2;border:1px solid #29383b}
          .pill.on{background:var(--gold);color:#1d1303;border-color:var(--gold);font-weight:800}
          .pill[disabled]{opacity:.35}
          .zoomrow{display:grid;grid-template-columns:38px minmax(0,1fr) 38px;align-items:center;gap:9px;margin-top:14px}
          .zoomrow input{width:100%;accent-color:var(--gold)}
          .mini{width:38px;height:38px;border:1px solid #2a3a3d;border-radius:50%;background:#182326;color:#e8f1f1;font-size:20px}
          .zoom{text-align:center;margin-top:5px;font-size:11px;color:#92a5a8;min-height:16px}
          #start{position:absolute;z-index:12;left:50%;top:50%;transform:translate(-50%,-50%);padding:14px 22px;
                 border:0;border-radius:999px;background:var(--gold);color:#201504;font-weight:850;box-shadow:0 12px 30px rgba(0,0,0,.38)}
          .review{position:fixed;z-index:20;inset:0;display:grid;grid-template-rows:auto minmax(0,1fr) auto;background:#050707}
          .review.hidden{display:none}.review-head{display:flex;align-items:center;justify-content:space-between;
                 padding:calc(env(safe-area-inset-top) + 14px) 18px 12px;background:#0b1012}
          .review-head strong{font-size:17px}.review-head span{color:#8ea0a3;font-size:12px}
          .review-frame{min-height:0;display:grid;place-items:center;background:#000;overflow:hidden}
          .review-frame img{width:100%;height:100%;object-fit:contain}
          .review-actions{display:grid;grid-template-columns:1fr 1.4fr;gap:10px;padding:14px 16px calc(env(safe-area-inset-bottom) + 16px);background:#0b1012}
          .review-actions button{min-height:52px;border-radius:14px;font-size:15px;font-weight:820}
          .retake{border:1px solid #314144;background:#172124;color:#eef5f5}.use{border:0;background:var(--gold);color:#211504}
          .flash{position:fixed;z-index:30;inset:0;background:#fff;opacity:0;pointer-events:none;transition:opacity .18s}
          /* The horizon. Two lines: one fixed, one that rolls with the phone. They meet and turn
             gold when the phone is square to the item, which is the only moment that matters. */
          .level{position:absolute;left:50%;top:50%;width:120px;height:2px;margin-left:-60px;
                 background:rgba(255,255,255,.55);pointer-events:none;transform-origin:center}
          .level.flat{background:#c79a36;height:3px}
          .levelref{position:absolute;left:50%;top:50%;width:44px;height:2px;margin-left:-22px;
                    background:rgba(255,255,255,.25);pointer-events:none}
          .tapdot{position:absolute;width:64px;height:64px;margin:-32px 0 0 -32px;border:2px solid #c79a36;
                  border-radius:10px;pointer-events:none;opacity:0;transition:opacity .3s}
          .tapdot.show{opacity:1}
          .hint{position:absolute;z-index:4;left:50%;top:50%;transform:translate(-50%,52px);color:rgba(255,255,255,.62);
                font-size:11px;text-shadow:0 1px 3px #000;white-space:nowrap}
          .hidden{display:none!important}
          @media (orientation:landscape) and (max-height:560px){
            .camera{max-width:none;grid-template-columns:minmax(0,1fr) 220px;grid-template-rows:1fr}
            .dock{grid-column:2;padding-top:calc(env(safe-area-inset-top) + 12px);display:flex;flex-direction:column;justify-content:center}
            .controls-sheet{position:fixed;left:auto;right:10px;bottom:10px;top:10px;width:min(360px,70vw);max-height:none}
          }
        </style></head>
        <body>
          <main class="camera">
            <div class="stage">
              <video id="v" playsinline muted autoplay></video>
              <div class="camera-top">
                <div class="brand"><span class="brandmark">ING</span><span>Photo Box</span></div>
                <div class="stage-actions">
                  <button class="iconbtn" id="flipbtn" type="button" aria-label="Switch front or back camera">↻</button>
                  <button class="iconbtn" id="levelquick" type="button" aria-label="Toggle level guide">⊖</button>
                </div>
              </div>
              <div class="status-float busy" id="s">Starting camera…</div>
              <div class="levelref" id="levelref" style="display:none"></div>
              <div class="level" id="level" style="display:none"></div>
              <div class="tapdot" id="tapdot"></div>
              <div class="hint" id="taphint" style="display:none">Tap anywhere to focus</div>
              <div class="lensrow" id="lensrow" style="display:none"></div>
            </div>

            <section class="dock" aria-label="Camera shutter and controls">
              <div class="shutterrow">
                <button class="thumb" id="lastphoto" type="button" aria-label="Review last photo" disabled><span>▧</span><img id="lastphotoimg" class="hidden" alt="Last photo"></button>
                <button class="shutter" id="shot" type="button" aria-label="Take photo"><span></span></button>
                <button class="controls-toggle" id="controls-toggle" type="button" aria-label="Camera controls">☷</button>
              </div>
              <div class="capture-meta">
                <span class="live">Connected</span>
                <span class="count" id="c">No photos yet</span>
                <span class="awake" id="awake"></span>
              </div>

              <div class="controls-sheet" id="controls-sheet">
                <div class="sheet-head"><h2>Camera controls</h2><button id="controls-close" type="button" aria-label="Close controls">×</button></div>
                <div class="rig" id="rig">
                  <div class="rigrow" id="flashrow" style="display:none">
                    <span class="riglab">Flash</span>
                    <button class="pill" type="button" data-flash="off">Off</button>
                    <button class="pill" type="button" data-flash="auto">Auto</button>
                    <button class="pill" type="button" data-flash="on">On</button>
                  </div>
                  <div class="rigrow">
                    <span class="riglab">Brightness</span>
                    <input id="phone-exposure" type="range" min="-2" max="2" step="0.25" value="0" aria-label="Brightness">
                    <span id="phone-exposure-value">Auto</span>
                  </div>
                  <div class="rigrow">
                    <span class="riglab">Focus</span>
                    <button class="pill on" type="button" data-focus="auto">Auto</button>
                    <button class="pill" type="button" data-focus="macro">Macro</button>
                    <button class="pill" type="button" data-focus="far">Far</button>
                  </div>
                  <div class="rigrow">
                    <span class="riglab">Color</span>
                    <button class="pill on" type="button" data-wb="auto">Auto</button>
                    <button class="pill" type="button" data-wb="daylight">Daylight</button>
                    <button class="pill" type="button" data-wb="tungsten">Warm lamp</button>
                    <button class="pill" type="button" data-wb="cool">Cool light</button>
                  </div>
                  <div class="rigrow" id="levelrow">
                    <span class="riglab">Guide</span>
                    <button class="pill" type="button" id="levelbtn">Level off</button>
                  </div>
                </div>
                <div class="zoomrow">
                  <button class="mini" id="zout" type="button" aria-label="Zoom out">−</button>
                  <input id="zr" type="range" min="1" max="8" step="0.1" value="1" aria-label="Zoom">
                  <button class="mini" id="zin" type="button" aria-label="Zoom in">+</button>
                </div>
                <div class="zoom" id="z">1× zoom</div>
              </div>
            </section>
            <button id="start">Allow camera</button>
          </main>

          <section class="review hidden" id="review" aria-label="Review captured photo">
            <div class="review-head"><strong id="review-title">Review photo</strong><span id="review-note">Full-resolution capture</span></div>
            <div class="review-frame"><img id="reviewimg" alt="Photo just taken"></div>
            <div class="review-actions">
              <button class="retake" id="retake" type="button">Retake</button>
              <button class="use" id="usephoto" type="button">Use photo</button>
            </div>
          </section>
          <canvas id="cv" style="display:none"></canvas>
          <div class="flash" id="fl"></div>
        <script>
        const TOKEN = {{"\"" + token + "\""}};
        const v = document.getElementById('v'), cv = document.getElementById('cv');
        const s = document.getElementById('s'), c = document.getElementById('c');
        const startBtn = document.getElementById('start'), fl = document.getElementById('fl');
        const awakeEl = document.getElementById('awake');
        const review = document.getElementById('review'), reviewImg = document.getElementById('reviewimg');
        const reviewTitle = document.getElementById('review-title'), reviewNote = document.getElementById('review-note');
        const retakeBtn = document.getElementById('retake'), usePhotoBtn = document.getElementById('usephoto');
        let taken = 0, running = false, wakeLock = null;
        let pendingPhoto = null, pendingPhotoUrl = '', lastPhotoUrl = '', reviewCommitted = false, capturing = false;
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

        function say(t, cls) { s.textContent = t; s.className = 'status-float ' + (cls || ''); }

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
            say('Ready — tap the shutter', 'ok');
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
        // Two ways, and the phone decides how much of each. A lens that will move is asked to
        // move (real optical zoom, no pixels lost); whatever it will not do is done to the
        // pixels instead, because at twelve megapixels a 4x centre crop is still comfortably
        // larger than anything eBay shows. The viewfinder and the saved photograph are cropped
        // by the same number, so what the desk sees is what the library gets.
        //
        // WHY IT IS A BLEND AND NOT A CHOICE (2026-08-22, "the zoom isn't working").
        // It used to be a choice, and it decided by whether applyConstraints THREW. That was
        // written when no iPhone reported a zoom capability to a web page at all, so the throw
        // was reliable and every iPhone landed on the crop. iOS then started reporting one — and
        // `advanced` constraints are best-effort by specification: a lens that cannot or will not
        // move satisfies them by ignoring them, and the promise resolves exactly as it does on a
        // lens that moved. So the code took the optical branch, switched the crop OFF for a zoom
        // that never happened, and the slider read 1.9x over a picture that had not changed.
        //
        // The second half of the same bug: the old mapping was linear across the lens's reported
        // range (lo + (hi-lo)*(z-1)/7), which is not what a number followed by an x means. On a
        // phone reporting 1-2x, asking for 1.9x moved the lens to 1.13x — technically honoured,
        // visually nothing, and the crop was off.
        //
        // So: ask the lens for the whole thing in its own units, MEASURE what it actually did,
        // and crop away the difference. The total magnification is then exactly what was asked
        // for on every phone, whether its lens moves, moves partway, or does not move at all.
        let zoom = 1, cropZoom = 1, zoomOptical = false;

        function track() { return v.srcObject && v.srcObject.getVideoTracks ? v.srcObject.getVideoTracks()[0] : null; }

        async function applyZoom(z) {
          zoom = Math.max(1, Math.min(8, Number(z) || 1));
          cropZoom = zoom;          // the answer if the lens does nothing, set before it is asked
          zoomOptical = false;

          const t = track();
          const caps = (t && t.getCapabilities) ? (t.getCapabilities() || {}) : {};
          if (t && caps.zoom && caps.zoom.max > (caps.zoom.min || 1)) {
            // In the lens's own units. Our 1x is the lens at its widest (goWidest put it there),
            // so 3x is three times that — capped at what the lens actually has.
            const lo = caps.zoom.min || 1, hi = caps.zoom.max;
            const target = Math.min(hi, lo * zoom);
            try {
              await t.applyConstraints({ advanced: [{ zoom: target }] });
              // MEASURED, never assumed. This one line is the fix: a resolved promise says the
              // request was accepted, not that the lens moved.
              const got = (t.getSettings && t.getSettings().zoom);
              const lensFactor = typeof got === 'number' && got > 0 ? got / lo : 1;
              cropZoom = Math.max(1, zoom / lensFactor);
              zoomOptical = lensFactor > 1.01 && cropZoom < 1.01;
            } catch (e) { /* refused outright; cropZoom above already covers the whole zoom */ }
          }
          zoomNote(zoom, zoomOptical);
          reportZoom();
        }

        function zoomNote(z, optical) {
          zEl.textContent = z > 1.01 ? (z.toFixed(1) + '× ' + (optical ? 'lens' : 'crop')) : '';
        }

        // The desktop's zoom chip used to print the number the desktop had asked for, which is
        // how "1.9x" sat over a picture at 1x for as long as it did. Now it prints what the phone
        // actually did. Sent only when the KIND changes — lens to crop or back — because that is
        // the only part the desk does not already know.
        let zoomReported = null;
        function reportZoom() {
          if (zoomReported === zoomOptical) return;
          zoomReported = zoomOptical;
          try { sendCaps(); } catch (e) { /* the picture is right either way */ }
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
                zoomOptical: zoomOptical,
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
          try {
            await t.applyConstraints({ advanced: [{ zoom: target }] });
            // Same measurement, same reason: a lens button that silently did nothing must not
            // also turn off the crop that was standing in for it. See applyZoom.
            const got = (t.getSettings && t.getSettings().zoom);
            const moved = typeof got === 'number' && Math.abs(got - target) < Math.max(0.05, (hi - lo) * 0.02);
            if (moved) { cropZoom = 1; zoomOptical = true; reportZoom(); }
          }
          catch (e) { /* the lens stays where it was, and so does the crop */ }
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
          document.querySelectorAll('[data-focus]').forEach(b =>
            b.classList.toggle('on', b.dataset.focus === focusMode));
          document.querySelectorAll('[data-wb]').forEach(b =>
            b.classList.toggle('on', b.dataset.wb === wbMode));
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

        function showReview(blob, needsApproval) {
          if (pendingPhotoUrl) URL.revokeObjectURL(pendingPhotoUrl);
          pendingPhoto = blob;
          pendingPhotoUrl = URL.createObjectURL(blob);
          reviewImg.src = pendingPhotoUrl;
          reviewCommitted = false;
          reviewTitle.textContent = needsApproval ? 'How does it look?' : 'Photo captured';
          reviewNote.textContent = needsApproval ? 'Zoom in to check detail' : 'Sending to Photo Library…';
          retakeBtn.style.display = needsApproval ? '' : 'none';
          usePhotoBtn.disabled = !needsApproval;
          usePhotoBtn.textContent = needsApproval ? 'Use photo' : 'Saving…';
          review.classList.remove('hidden');
        }

        function rememberPhoto(blob) {
          if (lastPhotoUrl) URL.revokeObjectURL(lastPhotoUrl);
          lastPhotoUrl = URL.createObjectURL(blob);
          const img = document.getElementById('lastphotoimg'), button = document.getElementById('lastphoto');
          img.src = lastPhotoUrl;
          img.classList.remove('hidden');
          button.querySelector('span').classList.add('hidden');
          button.disabled = false;
        }

        function closeReview() {
          review.classList.add('hidden');
          pendingPhoto = null;
          if (pendingPhotoUrl) URL.revokeObjectURL(pendingPhotoUrl);
          pendingPhotoUrl = '';
          say('Ready — tap the shutter', 'ok');
        }

        async function sendPhoto(blob) {
          say('Saving photo…', 'busy');
          const res = await fetch('/p/' + TOKEN + '/photo', { method: 'POST', body: blob });
          if (!res.ok) { say('The computer would not take that photo.', 'bad'); return false; }
          taken++;
          c.textContent = taken + (taken === 1 ? ' photo saved' : ' photos saved');
          rememberPhoto(blob);
          say('Saved to Photo Library', 'ok');
          return true;
        }

        async function shoot(reviewBeforeSending) {
          if (!running || capturing) return;
          capturing = true;
          // The lamp, if this phone has one and the mode asked for it. Turned off in the finally
          // below whatever happens next, because a torch left burning is a flat battery and a
          // seller who thinks the app broke their phone.
          let lit = false;
          try {
          lit = await fireFlash();
          const q = window_();
          cv.width = Math.round(q.sw); cv.height = Math.round(q.sh);
          const g = cv.getContext('2d');
          g.drawImage(v, q.sx, q.sy, q.sw, q.sh, 0, 0, cv.width, cv.height);
          developFrame(g, cv.width, cv.height);
          fl.style.opacity = '0.85'; setTimeout(() => fl.style.opacity = '0', 120);
          const blob = await new Promise(r => cv.toBlob(r, 'image/jpeg', 0.95));
          if (!blob) { say('That frame could not be captured. Try again.', 'bad'); return; }
          showReview(blob, !!reviewBeforeSending);
          if (!reviewBeforeSending) {
            const saved = await sendPhoto(blob);
            if (saved) {
              reviewCommitted = true;
              reviewTitle.textContent = 'Saved to Photo Library';
              reviewNote.textContent = cv.width + ' × ' + cv.height + ' full-resolution photo';
              usePhotoBtn.disabled = false;
              usePhotoBtn.textContent = 'Back to camera';
            } else {
              usePhotoBtn.disabled = false;
              usePhotoBtn.textContent = 'Try again';
            }
          }
          } finally { if (lit) await applyTorch(false); capturing = false; }
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
        const MAX_PREVIEW_BACKLOG = 128 * 1024;
        const pv = document.createElement('canvas');
        const pctx = pv.getContext('2d', { alpha: false, desynchronized: true });

        // One binary connection for the whole viewfinder. A POST for every frame paid the HTTP
        // request machinery 20 times a second and, worse, awaited frames that were already stale.
        // WebSocket.send queues bytes immediately. When one or two JPEGs are already queued we
        // drop the current preview and encode the next camera position instead; full-resolution
        // still photographs use sendPhoto() and are never dropped or sent through this channel.
        let previewSocket = null, previewSocketRetryAt = 0;
        async function previewLink() {
          if (previewSocket && previewSocket.readyState === WebSocket.OPEN) return previewSocket;
          if (Date.now() < previewSocketRetryAt) return null;
          previewSocketRetryAt = Date.now() + 3000;
          const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
          const ws = new WebSocket(scheme + '//' + location.host + '/p/' + TOKEN + '/preview-stream');
          ws.binaryType = 'arraybuffer';
          try {
            await new Promise((ok, no) => {
              const timer = setTimeout(() => no(new Error('preview socket timed out')), 1800);
              ws.onopen = () => { clearTimeout(timer); ok(); };
              ws.onerror = () => { clearTimeout(timer); no(new Error('preview socket unavailable')); };
            });
            ws.onclose = () => { if (previewSocket === ws) previewSocket = null; };
            previewSocket = ws;
            return ws;
          } catch (e) {
            try { ws.close(); } catch (_) { }
            return null;
          }
        }

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
                if (b) {
                  const ws = await previewLink();
                  if (ws && ws.readyState === WebSocket.OPEN) {
                    // Latest-frame-wins: never build a video queue the seller has to watch catch up.
                    if (ws.bufferedAmount < MAX_PREVIEW_BACKLOG) ws.send(b);
                  } else {
                    // Old browser / filtered network. Still a live view, just with per-frame POSTs.
                    await fetch('/p/' + TOKEN + '/preview', { method: 'POST', body: b });
                  }
                }
              }
            } catch (e) { /* a dropped preview frame is not worth a message */ }
            const spent = performance.now() - began;
            if (spent < MIN_FRAME_MS) await new Promise(r => setTimeout(r, MIN_FRAME_MS - spent));
          }
          try { previewSocket?.close(); } catch (e) { }
          previewSocket = null;
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
              if (j.shoot || j.command === 'shoot') await shoot(false);
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
        document.getElementById('shot').addEventListener('click', () => {
          document.getElementById('controls-sheet').classList.remove('open');
          shoot(true);
        });
        usePhotoBtn.addEventListener('click', async () => {
          if (reviewCommitted) { closeReview(); return; }
          if (!pendingPhoto) return;
          usePhotoBtn.disabled = true;
          usePhotoBtn.textContent = 'Saving…';
          const saved = await sendPhoto(pendingPhoto);
          usePhotoBtn.disabled = false;
          if (!saved) { usePhotoBtn.textContent = 'Try again'; return; }
          reviewCommitted = true;
          reviewTitle.textContent = 'Saved to Photo Library';
          reviewNote.textContent = cv.width + ' × ' + cv.height + ' full-resolution photo';
          retakeBtn.style.display = 'none';
          usePhotoBtn.textContent = 'Take another';
        });
        retakeBtn.addEventListener('click', closeReview);
        document.getElementById('lastphoto').addEventListener('click', () => {
          if (!lastPhotoUrl) return;
          reviewImg.src = lastPhotoUrl;
          pendingPhoto = null;
          reviewCommitted = true;
          reviewTitle.textContent = 'Last saved photo';
          reviewNote.textContent = 'Already in Photo Library';
          retakeBtn.style.display = 'none';
          usePhotoBtn.textContent = 'Back to camera';
          review.classList.remove('hidden');
        });
        const controlsSheet = document.getElementById('controls-sheet');
        document.getElementById('controls-toggle').addEventListener('click', () => controlsSheet.classList.toggle('open'));
        document.getElementById('controls-close').addEventListener('click', () => controlsSheet.classList.remove('open'));
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
          const focusBtn = e.target.closest ? e.target.closest('[data-focus]') : null;
          if (focusBtn) { applyFocus(focusBtn.dataset.focus).then(() => paintRig(!!caps_().torch)); return; }
          const wbBtn = e.target.closest ? e.target.closest('[data-wb]') : null;
          if (wbBtn) { applyWhiteBalance(wbBtn.dataset.wb).then(() => paintRig(!!caps_().torch)); return; }
        });
        document.getElementById('levelbtn').addEventListener('click', () => applyLevel(!levelOn));
        document.getElementById('levelquick').addEventListener('click', () => {
          const on = !levelOn;
          applyLevel(on);
          document.getElementById('levelquick').classList.toggle('on', on);
        });
        document.getElementById('flipbtn').addEventListener('click', () => applyFacing(facing === 'environment' ? 'user' : 'environment'));
        document.getElementById('phone-exposure').addEventListener('input', e => {
          const ev = Number(e.target.value);
          document.getElementById('phone-exposure-value').textContent = Math.abs(ev) < .01 ? 'Auto' : (ev > 0 ? '+' : '') + ev.toFixed(2);
          applyExposure(ev);
        });

        startBtn.addEventListener('click', begin);
        // Safari needs a tap before it will hand over a camera, so the button is the real entry
        // point; this only helps the browsers that do not.
        begin();
        </script>
        </body></html>
        """;
}
