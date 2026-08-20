using System.IO.Ports;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The desk-side product camera — a Freenove ESP32-S3 board running
/// <c>firmware/photobox/photobox.ino</c> — and everything the app does with it:
/// find it on a COM port, hand it the WiFi over the USB cable, remember where it
/// landed, and pull frames off it into the Photo Library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why provisioning goes over the cable and not an AP.</b> The usual ESP32 dance —
/// the board opens its own hotspot, the person switches networks, types the password
/// into a captive portal, switches back — loses the seller exactly at the step where
/// their laptop drops off the network the app is on. The board is already plugged
/// into this machine by USB; a COM port is a wire with nobody else on it. One JSON
/// line down that wire and the board is on WiFi without the seller's browser ever
/// leaving the app.
/// </para>
/// <para>
/// <b>Desktop only.</b> The hosted build has no USB and no LAN in common with the
/// seller; its endpoints refuse with a sentence saying to use the desktop app.
/// </para>
/// </remarks>
public sealed record PhotoBoxProvisionRequest(string? Port, string? Ssid, string? Password);

public sealed class PhotoBoxCamera(IHttpClientFactory httpFactory)
{
    /// <summary>The provisioning conversation's speed — matches the firmware's Serial.begin.</summary>
    public const int BaudRate = 115200;

    /// <summary>Photo Library folder every snap lands in.</summary>
    public const string LibraryFolder = "photo-box";

    private static readonly JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    private static string SettingsPath => Path.Combine(AppPaths.DataHome, "photobox.json");

    public sealed class Settings
    {
        public string CameraUrl { get; set; } = "";
        public string Ssid { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; }
    }

    public Settings? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Json);
        }
        catch { return null; }   // a corrupt settings file reads as "not set up", never as a crash
    }

    private void Save(Settings s)
    {
        lock (_gate) File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));
    }

    public void Forget()
    {
        lock (_gate) { try { File.Delete(SettingsPath); } catch { } }
    }

    /// <summary>
    /// Every serial port on the machine, each probed briefly for the firmware's
    /// <c>waiting_for_wifi</c> heartbeat so the screen can say "this one is the camera"
    /// instead of listing three Bluetooth ports and a mystery.
    /// </summary>
    public async Task<List<object>> ListPortsAsync(CancellationToken ct)
    {
        var results = new List<object>();
        foreach (var name in SerialPort.GetPortNames().Distinct().OrderBy(n => n))
        {
            ct.ThrowIfCancellationRequested();
            var (isCamera, lastLine) = await ProbeAsync(name, ct);
            results.Add(new { port = name, isCamera, lastLine });
        }
        return results;
    }

    /// <summary>
    /// Listens on one port for ~2.5s for a line the firmware would say. Opening a
    /// Bluetooth SPP port can block for seconds on its own, so the whole probe runs
    /// under a hard timeout and every failure reads as "not the camera".
    /// </summary>
    private static async Task<(bool IsCamera, string LastLine)> ProbeAsync(string portName, CancellationToken ct)
    {
        try
        {
            var probe = Task.Run(() =>
            {
                using var port = new SerialPort(portName, BaudRate)
                {
                    ReadTimeout = 500,
                    DtrEnable = true,   // native USB-CDC says nothing until DTR is up
                    RtsEnable = true,   // and DTR+RTS together leave the auto-reset circuit alone
                };
                port.Open();
                var deadline = DateTime.UtcNow.AddSeconds(2.5);
                string last = "";
                while (DateTime.UtcNow < deadline)
                {
                    string line;
                    try { line = port.ReadLine(); }
                    catch (TimeoutException) { continue; }
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    last = line;
                    if (line.Contains("waiting_for_wifi") || line.Contains("\"connected\"") ||
                        line.Contains("camera_failed"))
                        return (true, line);
                }
                return (false, last);
            }, ct);

            var done = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(6), ct));
            return done == probe ? await probe : (false, "(port did not answer)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, $"({ex.Message})");
        }
    }

    public sealed record ProvisionResult(bool Ok, string Status, string? Ip, string? Mdns, string Detail);

    /// <summary>
    /// The whole setup, one call: send the WiFi down the cable, wait for the board to
    /// say where it landed, remember that address. The firmware answers in JSON lines;
    /// everything it said rides back in <see cref="ProvisionResult.Detail"/> so a
    /// failure is a transcript rather than a shrug.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(string portName, string ssid, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return new(false, "invalid", null, null, "No port chosen. Press ↻ and pick the one marked as the camera.");
        if (string.IsNullOrWhiteSpace(ssid))
            return new(false, "invalid", null, null, "The WiFi network name is empty.");
        // The firmware's parser is one JSON line with no escape handling — honestly
        // documented there — so the two characters it cannot carry are refused here.
        if ((ssid + password).IndexOfAny(['"', '\\']) >= 0)
            return new(false, "invalid", null, null,
                "A quote or backslash in the network name or password can't be sent over this link.");

        return await Task.Run(() =>
        {
            var transcript = new List<string>();
            try
            {
                using var port = new SerialPort(portName, BaudRate)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 3000,
                    NewLine = "\n",
                    DtrEnable = true,
                    RtsEnable = true,
                };
                port.Open();
                try { port.DiscardInBuffer(); } catch { }

                port.WriteLine($"{{\"ssid\":\"{ssid}\",\"pass\":\"{password}\"}}");

                // 20s of WiFi attempt inside the firmware plus margin for the beacon gap.
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    string line;
                    try { line = port.ReadLine(); }
                    catch (TimeoutException) { continue; }
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    transcript.Add(line);
                    if (transcript.Count > 40) transcript.RemoveAt(0);

                    if (line.Contains("\"status\":\"connected\""))
                    {
                        var ip = ReadField(line, "ip");
                        var mdns = ReadField(line, "mdns");
                        if (ip is null) continue;
                        Save(new Settings { CameraUrl = $"http://{ip}", Ssid = ssid, SavedAt = DateTimeOffset.UtcNow });
                        return new ProvisionResult(true, "connected", ip, mdns, string.Join("\n", transcript));
                    }
                    if (line.Contains("\"status\":\"failed\""))
                        return new ProvisionResult(false, "failed", null, null,
                            ReadField(line, "reason") ?? "The camera couldn't join that network.");
                    if (line.Contains("camera_failed"))
                        transcript.Add("(the board answered, but its camera module did not start — check the ribbon cable)");
                }
                return new ProvisionResult(false, "timeout", null, null,
                    transcript.Count > 0
                        ? "The board answered but never reported a connection:\n" + string.Join("\n", transcript)
                        : "Nothing answered on that port. Is it the one marked as the camera, with the Photo Box firmware flashed?");
            }
            catch (Exception ex)
            {
                return new ProvisionResult(false, "error", null, null, ex.Message);
            }
        }, ct);
    }

    private static string? ReadField(string jsonLine, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    public sealed record CameraStatus(bool Configured, string? Url, string? Ssid, bool Reachable, string? Detail);

    /// <summary>Is the remembered camera still answering where it said it would be?</summary>
    public async Task<CameraStatus> StatusAsync(CancellationToken ct)
    {
        var s = Load();
        if (s is null || string.IsNullOrWhiteSpace(s.CameraUrl))
            return new(false, null, null, false, null);

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(4);
            var body = await http.GetStringAsync(s.CameraUrl + "/", ct);
            var ours = body.Contains("ing-photobox");
            return new(true, s.CameraUrl, s.Ssid, ours,
                ours ? null : "Something answered at that address, but it isn't the Photo Box camera.");
        }
        catch (Exception ex)
        {
            return new(true, s.CameraUrl, s.Ssid, false,
                $"The camera didn't answer at {s.CameraUrl} ({ex.Message}). It may still be booting, or the router gave it a new address — run the USB setup again to find it.");
        }
    }

    /// <summary>One full-resolution frame off the camera, straight into the Photo Library.</summary>
    public async Task<(string? Url, string? Error)> SnapAsync(PhotoLibrary photos, CancellationToken ct)
    {
        var s = Load();
        if (s is null) return (null, "No camera is set up yet.");
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var bytes = await http.GetByteArrayAsync(s.CameraUrl + "/jpg", ct);
            if (bytes.Length < 1000) return (null, "The camera answered with an empty frame.");
            var url = await photos.SavePhotoAsync(LibraryFolder, bytes, "jpg");
            return (url, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't get a frame: {ex.Message}");
        }
    }
}
