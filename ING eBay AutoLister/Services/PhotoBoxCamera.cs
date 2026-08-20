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

/// <summary>The viewfinder's zoom at the moment the shutter was pressed. 1 (or absent) = full frame.</summary>
public sealed record PhotoBoxSnapRequest(double? Zoom);

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

    /// <summary>
    /// One frame off the camera, straight into the Photo Library. A zoom above 1 crops
    /// the centred 1/zoom window out of the frame before saving — the same window the
    /// screen's viewfinder was showing — so the photo is what the seller framed, not a
    /// wide shot they then have to crop by hand. The lens is fixed; this is the only
    /// kind of zoom this hardware will ever have, and it is honest about being a crop.
    /// </summary>
    public async Task<(string? Url, string? Error)> SnapAsync(PhotoLibrary photos, double zoom, CancellationToken ct)
    {
        var s = Load();
        if (s is null) return (null, "No camera is set up yet.");
        try
        {
            var http = httpFactory.CreateClient();
            // A full-size frame from a sensor with no hardware encoder is seconds of
            // software JPEG on the board, plus a size switch — the shutter gets time.
            http.Timeout = TimeSpan.FromSeconds(25);
            var bytes = await http.GetByteArrayAsync(s.CameraUrl + "/jpg", ct);
            if (bytes.Length < 1000) return (null, "The camera answered with an empty frame.");

            if (zoom > 1.01 && OperatingSystem.IsWindows())
            {
                var cropped = CropCenter(bytes, Math.Min(zoom, 8.0));
                if (cropped is not null) bytes = cropped;
                // A crop that fails for any reason saves the full frame rather than nothing:
                // the seller pressed a shutter, and a shutter that answers "error" over a
                // resize is worse than a photo that needs a trim.
            }

            var url = await photos.SavePhotoAsync(LibraryFolder, bytes, "jpg");
            return (url, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't get a frame: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? CropCenter(byte[] jpeg, double zoom)
    {
        try
        {
            using var src = System.Drawing.Image.FromStream(new MemoryStream(jpeg));
            var w = (int)(src.Width / zoom);
            var h = (int)(src.Height / zoom);
            if (w < 16 || h < 16) return null;
            var x = (src.Width - w) / 2;
            var y = (src.Height - h) / 2;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h),
                            new System.Drawing.Rectangle(x, y, w, h), System.Drawing.GraphicsUnit.Pixel);
            }
            using var outStream = new MemoryStream();
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ep = new System.Drawing.Imaging.EncoderParameters(1);
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            bmp.Save(outStream, codec, ep);
            return outStream.ToArray();
        }
        catch { return null; }
    }
}

/// <summary>
/// Whether the camera is even on the USB — answered from Windows' own device list, before any
/// serial port is opened. Three different desks produce the same empty port list: nothing plugged
/// in, a charge-only cable (the board enumerates as an unidentifiable device), and a serial chip
/// whose driver Windows doesn't have. The seller cannot tell them apart and the fix for each is
/// different, so this names which one it is and what to do — with the driver download when THAT
/// is the problem.
/// </summary>
public static class PhotoBoxUsb
{
    /// <summary>One USB device as Windows reports it. Problem is ConfigManagerErrorCode; 0 is healthy.</summary>
    public sealed record Device(string Name, string PnpId, uint Problem);

    public sealed record Diagnosis(string Verdict, string Sentence, string WhatToDo, string? DriverUrl,
                                   IReadOnlyList<Device> Seen);

    /// <summary>WCH's own CH343 driver page — the UART chip on the Freenove ESP32-S3 board.</summary>
    public const string WchDriverUrl = "https://www.wch-ic.com/downloads/CH343SER_EXE.html";

    /// <summary>Silicon Labs' CP210x driver page, the other common ESP32 UART chip.</summary>
    public const string SiliconLabsDriverUrl = "https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers";

    /// <summary>The live answer, from WMI. Desktop only — the endpoint refuses before this runs elsewhere.</summary>
    public static Diagnosis Diagnose()
    {
        if (!OperatingSystem.IsWindows()) return Classify([]);

        var seen = new List<Device>();
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
        foreach (var found in searcher.Get())
        {
            seen.Add(new Device(
                found["Name"]?.ToString() ?? "",
                found["PNPDeviceID"]?.ToString() ?? "",
                found["ConfigManagerErrorCode"] is { } code ? Convert.ToUInt32(code) : 0));
        }
        return Classify(seen);
    }

    /// <summary>
    /// The verdict for one snapshot of the USB tree. Pure, so the four desks this has to tell
    /// apart can each be a test instead of a piece of hardware.
    /// </summary>
    public static Diagnosis Classify(IReadOnlyList<Device> seen)
    {
        // A healthy USB serial port wins outright — whatever else is plugged in, there is
        // something to scan. Bluetooth COM ports don't count; their ids start BTHENUM, and two of
        // them sitting on every Windows machine is exactly how "there's a port" lies.
        var ready = seen.FirstOrDefault(d =>
            d.Problem == 0 && System.Text.RegularExpressions.Regex.IsMatch(d.Name, @"\(COM\d+\)"));
        if (ready is not null)
        {
            var com = System.Text.RegularExpressions.Regex.Match(ready.Name, @"\((COM\d+)\)").Groups[1].Value;
            return new Diagnosis("ok",
                $"A USB serial device is on {com} — that should be the camera.",
                "Press ↻ Find camera below.",
                null, seen);
        }

        // A serial chip that is present but broken is almost always code 28 — no driver. This is
        // the one problem a download actually fixes, so it outranks the cable verdict: a machine
        // can show both, and installing the driver is the step that changes what happens next.
        var chip = seen.FirstOrDefault(d => d.Problem != 0 && ChipOf(d.PnpId) is not null);
        if (chip is not null)
        {
            var (name, url) = ChipOf(chip.PnpId)!.Value;
            return new Diagnosis("driver",
                $"The camera's USB chip ({name}) is plugged in, but Windows has no working driver for it.",
                url is null
                    ? "Unplug and replug the board — Windows carries this driver and usually just needs the nudge."
                    : "Install the driver, replug the board, then press Check USB again.",
                url, seen);
        }

        // Enumeration failed at the electrical level: the device cannot even say what it is. No
        // driver can be installed for a device with no identity — this is the cable or the socket.
        if (seen.Any(d => d.Problem != 0 &&
                (d.Name.Contains("Device Descriptor Request Failed", StringComparison.OrdinalIgnoreCase)
                 || d.PnpId.Contains(@"VID_0000", StringComparison.OrdinalIgnoreCase))))
            return new Diagnosis("cable",
                "Something on USB cannot identify itself — that is the cable or the socket, not a missing driver.",
                "Use a DATA cable (a charge-only cable does exactly this), plugged into the board socket " +
                "labelled UART/COM, straight into the PC. Then press Check USB again.",
                null, seen);

        return new Diagnosis("none",
            "Nothing that could be the camera is on USB.",
            "Plug the board's UART/COM socket into this computer with a data cable, then press Check USB again.",
            null, seen);
    }

    /// <summary>The serial chip a PNP id names, or null when it isn't one this recognises.</summary>
    /// <remarks>Espressif's native USB carries no url: Windows ships that driver, so the fix is a replug, not a download.</remarks>
    private static (string Name, string? Url)? ChipOf(string pnpId) =>
        pnpId.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase) ? ("WCH CH340/CH343", WchDriverUrl)
      : pnpId.Contains("VID_10C4", StringComparison.OrdinalIgnoreCase) ? ("Silicon Labs CP210x", SiliconLabsDriverUrl)
      : pnpId.Contains("VID_303A", StringComparison.OrdinalIgnoreCase) ? ("Espressif native USB", (string?)null)
      : null;
}
