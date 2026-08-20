// ING Photo Box Camera — Freenove ESP32-S3 WROOM CAM board.
//
// What this firmware is: the camera half of the app's "Photo Box Camera" screen.
// The app provisions it over the USB cable — a single JSON line with the WiFi
// name and password — and from then on the board joins that network by itself,
// serves a live MJPEG stream and single JPEG frames, and answers on
// http://photobox.local as well as its raw IP.
//
// Serial protocol (115200, both USB-CDC and the UART-labelled port):
//   app -> board:  {"ssid":"MyNetwork","pass":"secret"}\n
//   board -> app:  {"status":"connecting","ssid":"MyNetwork"}
//                  {"status":"connected","ip":"192.168.1.42","mdns":"photobox.local"}
//               or {"status":"failed","reason":"wrong password or out of range"}
//   While unprovisioned it says {"status":"waiting_for_wifi"} every 2 seconds,
//   which is how the app's port scan recognises the board.
//
// HTTP endpoints once on WiFi:
//   GET /        -> {"device":"ing-photobox","ip":...}   (identify)
//   GET /jpg     -> one JPEG frame  (what the app's Snap button fetches)
//   GET /stream  -> multipart MJPEG (what the app's live view embeds)

#include "esp_camera.h"
#include "img_converters.h"   // frame2jpg: software JPEG for sensors without a hardware encoder
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <Preferences.h>

// ── Freenove ESP32-S3 WROOM CAM pin map (same as ESP32S3_EYE) ────────────────
#define PWDN_GPIO_NUM  -1
#define RESET_GPIO_NUM -1
#define XCLK_GPIO_NUM  15
#define SIOD_GPIO_NUM  4
#define SIOC_GPIO_NUM  5
#define Y9_GPIO_NUM    16
#define Y8_GPIO_NUM    17
#define Y7_GPIO_NUM    18
#define Y6_GPIO_NUM    12
#define Y5_GPIO_NUM    10
#define Y4_GPIO_NUM    8
#define Y3_GPIO_NUM    9
#define Y2_GPIO_NUM    11
#define VSYNC_GPIO_NUM 6
#define HREF_GPIO_NUM  7
#define PCLK_GPIO_NUM  13

Preferences prefs;
WebServer server(80);
// The stream lives on its own port with its own socket, written a frame at a
// time from loop(). WebServer serves exactly one connection at a time, and a
// stream inside a handler owned that slot forever: while anybody watched, /jpg
// hung, a page refresh wedged on the old half-dead stream, and the serial
// setup went deaf. One viewer, frame by frame, everything else still answering.
WiFiServer streamServer(81);
WiFiClient streamClient;
unsigned long lastStreamFrame = 0;
// While a snap is being served, the stream holds its breath: this board cannot
// push a viewfinder and a full-quality frame through one small radio at once,
// and the shutter is the one that must not lose.
volatile unsigned long snapHoldUntil = 0;
bool cameraOk = false;
bool jpegNative = true;     // false = the sensor has no JPEG encoder; frames get converted in software
bool serverStarted = false; // the web server exists only once WiFi does — see startServerOnce()
bool wifiUp = false;
unsigned long lastBeacon = 0;

static void say(const String& line) {
  Serial.println(line);   // native USB (the port labelled USB)
  Serial0.println(line);  // CH340 (the port labelled UART/COM)
}

static bool tryInitCamera(pixformat_t fmt) {
  camera_config_t c = {};
  c.ledc_channel = LEDC_CHANNEL_0;
  c.ledc_timer   = LEDC_TIMER_0;
  c.pin_d0 = Y2_GPIO_NUM;  c.pin_d1 = Y3_GPIO_NUM;  c.pin_d2 = Y4_GPIO_NUM;  c.pin_d3 = Y5_GPIO_NUM;
  c.pin_d4 = Y6_GPIO_NUM;  c.pin_d5 = Y7_GPIO_NUM;  c.pin_d6 = Y8_GPIO_NUM;  c.pin_d7 = Y9_GPIO_NUM;
  c.pin_xclk = XCLK_GPIO_NUM; c.pin_pclk = PCLK_GPIO_NUM; c.pin_vsync = VSYNC_GPIO_NUM;
  c.pin_href = HREF_GPIO_NUM; c.pin_sccb_sda = SIOD_GPIO_NUM; c.pin_sccb_scl = SIOC_GPIO_NUM;
  c.pin_pwdn = PWDN_GPIO_NUM; c.pin_reset = RESET_GPIO_NUM;
  // 10MHz, not the usual 20: the no-JPEG sensor in this kit loses bytes on the
  // parallel bus at 20MHz — cam_hal logged FB-SIZE mismatches and threw away
  // almost every frame, which read as a one-frame-per-eight-seconds stream.
  // Native-JPEG modules keep the full clock; they were never the ones dropping.
  c.xclk_freq_hz = (fmt == PIXFORMAT_JPEG) ? 20000000 : 10000000;
  c.pixel_format = fmt;
  if (fmt == PIXFORMAT_JPEG && psramFound()) {
    c.frame_size   = FRAMESIZE_UXGA;   // 1600x1200 off the hardware encoder
    c.jpeg_quality = 10;
    c.fb_count     = 2;
    c.grab_mode    = CAMERA_GRAB_LATEST;
    c.fb_location  = CAMERA_FB_IN_PSRAM;
  } else if (fmt == PIXFORMAT_JPEG) {
    c.frame_size   = FRAMESIZE_SVGA;
    c.jpeg_quality = 12;
    c.fb_count     = 1;
    c.fb_location  = CAMERA_FB_IN_DRAM;
  } else {
    // Raw RGB565. VGA, not SVGA: the sensor in this kit silently delivers ~640x480
    // whatever bigger size is asked of it (the cam_hal FB-SIZE mismatch), and asking
    // for the phantom size is also what wedged the driver on a mid-run switch.
    // Init at the largest size the sensor actually produces.
    c.frame_size   = psramFound() ? FRAMESIZE_VGA : FRAMESIZE_QVGA;
    c.fb_count     = psramFound() ? 2 : 1;
    c.grab_mode    = CAMERA_GRAB_LATEST;
    c.fb_location  = psramFound() ? CAMERA_FB_IN_PSRAM : CAMERA_FB_IN_DRAM;
  }
  if (esp_camera_init(&c) != ESP_OK) {
    // A failed init can leave the driver half-standing — and every camera call
    // after that is the reboot loop this firmware once shipped with. Tear it down.
    esp_camera_deinit();
    return false;
  }
  sensor_t* s = esp_camera_sensor_get();
  if (s) {
    s->set_vflip(s, 1);         // the Freenove module ships upside-down relative to its case
    // A photo box is BRIGHT. The old brightness=+1 pushed an already-clipping GC0308
    // further over, and everything near a light smeared into purple noise. Neutral
    // brightness, auto white balance and auto gain, saturation eased a notch: let the
    // sensor's own loops fight the light instead of leaning into it.
    s->set_brightness(s, 0);
    s->set_saturation(s, -1);
    s->set_whitebal(s, 1);
    s->set_awb_gain(s, 1);
    s->set_gain_ctrl(s, 1);
    s->set_exposure_ctrl(s, 1);
    // Which module is actually on the ribbon — the one fact every remote
    // diagnosis of this board has had to guess at. Printed once, kept in /.
    say(String("{\"status\":\"sensor\",\"pid\":\"0x") + String(s->id.PID, HEX) + "\"}");
  }
  return true;
}

// JPEG straight off the sensor when it has an encoder; raw RGB converted in
// software when it doesn't (Freenove ships both kinds of module in this kit,
// and "JPEG format is not supported on this sensor" was this exact board).
static bool initCamera() {
  if (tryInitCamera(PIXFORMAT_JPEG)) { jpegNative = true; return true; }
  if (tryInitCamera(PIXFORMAT_RGB565)) {
    jpegNative = false;
    say("{\"status\":\"camera_soft_jpeg\"}");
    return true;
  }
  return false;
}

// The sensor's mode is set once at boot and never touched again: this module
// wedges its driver on ANY runtime set_framesize (tried both directions; the
// stream died from the first switched frame). A 2:1 decimated viewfinder lived
// here for one evening and is gone: once the 10MHz clock stopped the byte loss,
// the sensor's own frame rate set the pace and the shrink only cost pixels.

// One frame as JPEG bytes whatever the sensor speaks. Returns false when there is
// nothing to send; *ownedJpg is set when the buffer must be free()d by the caller.
static bool frameAsJpeg(camera_fb_t* fb, int quality, uint8_t** buf, size_t* len, bool* ownedJpg) {
  if (jpegNative) { *buf = fb->buf; *len = fb->len; *ownedJpg = false; return true; }
  *ownedJpg = frame2jpg(fb, quality, buf, len);
  return *ownedJpg;
}

// One JSON line in, credentials out. Tolerant of whitespace, intolerant of guesswork:
// anything that does not contain both fields is ignored rather than half-applied.
static bool parseCreds(const String& line, String& ssid, String& pass) {
  int s1 = line.indexOf("\"ssid\"");
  int p1 = line.indexOf("\"pass\"");
  if (s1 < 0 || p1 < 0) return false;
  auto value = [&](int keyPos) -> String {
    int colon = line.indexOf(':', keyPos);
    int q1 = line.indexOf('"', colon + 1);
    int q2 = q1 < 0 ? -1 : line.indexOf('"', q1 + 1);
    // A password with an escaped quote is beyond this parser on purpose; the app
    // warns rather than sending one.
    return (q1 < 0 || q2 < 0) ? String() : line.substring(q1 + 1, q2);
  };
  ssid = value(s1);
  pass = value(p1);
  return ssid.length() > 0;
}

static bool joinWifi(const String& ssid, const String& pass, unsigned long timeoutMs) {
  say(String("{\"status\":\"connecting\",\"ssid\":\"") + ssid + "\"}");
  WiFi.mode(WIFI_STA);
  WiFi.begin(ssid.c_str(), pass.c_str());
  unsigned long start = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - start < timeoutMs) delay(250);
  return WiFi.status() == WL_CONNECTED;
}

static void announceConnected() {
  String ip = WiFi.localIP().toString();
  MDNS.end();
  bool mdns = MDNS.begin("photobox");
  if (mdns) MDNS.addService("http", "tcp", 80);
  startServerOnce();
  say(String("{\"status\":\"connected\",\"ip\":\"") + ip +
      (mdns ? "\",\"mdns\":\"photobox.local\"}" : "\"}"));
}

static void handleRoot() {
  server.send(200, "application/json",
    String("{\"device\":\"ing-photobox\",\"camera\":") + (cameraOk ? "true" : "false") +
    ",\"ip\":\"" + WiFi.localIP().toString() + "\"}");
}

static void handleJpg() {
  if (!cameraOk) { server.send(503, "text/plain", "camera not initialised"); return; }
  snapHoldUntil = millis() + 4000;   // the stream yields; the shutter shoots
  camera_fb_t* fb = esp_camera_fb_get();
  if (!fb) { server.send(503, "text/plain", "no frame"); return; }
  uint8_t* jpg; size_t len; bool owned;
  if (!frameAsJpeg(fb, 88, &jpg, &len, &owned)) {
    esp_camera_fb_return(fb);
    server.send(503, "text/plain", "jpeg conversion failed");
    return;
  }
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.setContentLength(len);
  server.send(200, "image/jpeg", "");
  server.client().write(jpg, len);
  if (owned) free(jpg);
  esp_camera_fb_return(fb);
}

// Port 80's /stream is a signpost now, not the stream: browsers follow a 302 for
// an <img>, so old links keep working while the bytes come from port 81.
static void handleStreamRedirect() {
  server.sendHeader("Location", String("http://") + WiFi.localIP().toString() + ":81/stream");
  server.send(302, "text/plain", "the stream lives on :81");
}

// Called every loop(): adopt a newly-knocked viewer (the newest one wins — a
// refresh must replace the dead stream, not queue behind it), then write one
// frame if it is time. Never blocks longer than one frame write.
static void pumpStream() {
  if (!serverStarted) return;
  WiFiClient knock = streamServer.accept();
  if (knock) {
    if (streamClient) streamClient.stop();
    streamClient = knock;
    streamClient.setTimeout(2);
    // Read and discard the request line + headers; the answer is the same
    // whatever was asked on this port.
    unsigned long until = millis() + 500;
    while (streamClient.connected() && millis() < until && streamClient.available())
      streamClient.read();
    streamClient.print("HTTP/1.1 200 OK\r\n"
                       "Access-Control-Allow-Origin: *\r\n"
                       "Cache-Control: no-cache\r\n"
                       "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n");
    lastStreamFrame = 0;
  }
  if (!streamClient || !streamClient.connected()) return;
  if (!cameraOk) { streamClient.stop(); return; }
  if (millis() < snapHoldUntil) return;          // a snap is in flight — hold this breath
  if (millis() - lastStreamFrame < 160) return;
  lastStreamFrame = millis();
  camera_fb_t* fb = esp_camera_fb_get();
  if (!fb) return;
  // Full frames on the stream, not the old 2:1 decimation. Once the 10MHz clock
  // stopped the byte loss, the sensor's own frame rate became the pace-setter —
  // shrinking the picture saved no meaningful time and cost three quarters of
  // the pixels, which the owner noticed immediately ("the resolution was way
  // bigger"). Quality 70 keeps a VGA frame near 30KB, small enough to send.
  uint8_t* jpg = nullptr; size_t len = 0; bool owned = false;
  bool ok = frameAsJpeg(fb, jpegNative ? 55 : 70, &jpg, &len, &owned);
  if (ok) {
    streamClient.printf("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: %u\r\n\r\n", len);
    streamClient.write(jpg, len);
    streamClient.print("\r\n");
    if (owned) free(jpg);
  }
  esp_camera_fb_return(fb);
}

// The server exists only once WiFi does. Standing it up with the radio off is
// how this firmware used to die: a socket over no netif asserted in FreeRTOS and
// the board rebooted every three seconds, taking the serial setup down with it.
static void startServerOnce() {
  if (serverStarted) return;
  server.on("/", handleRoot);
  server.on("/jpg", handleJpg);
  server.on("/stream", handleStreamRedirect);
  server.begin();
  streamServer.begin();
  streamServer.setNoDelay(true);
  serverStarted = true;
}

static void pollSerial(Stream& port) {
  if (!port.available()) return;
  String line = port.readStringUntil('\n');
  String ssid, pass;
  if (!parseCreds(line, ssid, pass)) return;
  prefs.begin("photobox", false);
  prefs.putString("ssid", ssid);
  prefs.putString("pass", pass);
  prefs.end();
  if (joinWifi(ssid, pass, 20000)) {
    wifiUp = true;
    announceConnected();
  } else {
    wifiUp = false;
    say("{\"status\":\"failed\",\"reason\":\"wrong password or out of range\"}");
  }
}

void setup() {
  Serial.begin(115200);
  Serial0.begin(115200);
  Serial.setTimeout(200);
  Serial0.setTimeout(200);
  delay(300);

  // The radio exists before anything asks the network stack for a socket.
  WiFi.mode(WIFI_STA);

  cameraOk = initCamera();
  if (!cameraOk) say("{\"status\":\"camera_failed\"}");

  prefs.begin("photobox", true);
  String ssid = prefs.getString("ssid", "");
  String pass = prefs.getString("pass", "");
  prefs.end();

  if (ssid.length() > 0 && joinWifi(ssid, pass, 15000)) {
    wifiUp = true;
    announceConnected();   // starts the web server too — WiFi is up by then
  }
}

void loop() {
  if (serverStarted) server.handleClient();
  pumpStream();
  pollSerial(Serial);
  pollSerial(Serial0);

  // The heartbeat the app's port scan listens for. Only while unprovisioned:
  // a board that is already streaming should not chatter over every COM port.
  if (!wifiUp && millis() - lastBeacon > 2000) {
    lastBeacon = millis();
    say("{\"status\":\"waiting_for_wifi\"}");
  }

  // WiFi that was up and fell over tries itself again rather than sulking:
  // the photo box lives on a shelf, and "unplug it and replug it" is a bad manual.
  static unsigned long lastRetry = 0;
  if (!wifiUp && millis() - lastRetry > 30000) {
    lastRetry = millis();
    prefs.begin("photobox", true);
    String ssid = prefs.getString("ssid", "");
    String pass = prefs.getString("pass", "");
    prefs.end();
    if (ssid.length() > 0 && joinWifi(ssid, pass, 15000)) {
      wifiUp = true;
      announceConnected();
    }
  }
}
