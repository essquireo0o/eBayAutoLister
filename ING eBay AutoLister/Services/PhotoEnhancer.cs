using System.Diagnostics;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a raw phone frame into the photo that should actually go on the listing. A lightweight
/// segmentation model supplies the subject bounds when available; a deterministic edge/background
/// detector is the offline fallback, so enhancement never depends on a model download succeeding.
/// The original Photo Box image remains in the library and the enhanced copy is saved beside it.
/// </summary>
public sealed class PhotoEnhancer(IWebHostEnvironment env, PhotoLibrary photos, ActionLog log)
{
    public sealed record Result(string Url, bool AiDetected, int CropPercent, int Width, int Height);

    public async Task<Result> EnhanceAsync(string sourceUrl, CancellationToken ct)
    {
        var source = ResolveLocalPhoto(sourceUrl);
        var input = Path.Combine(Path.GetTempPath(), $"ing_enhance_in_{Guid.NewGuid():N}{Path.GetExtension(source)}");
        var output = Path.Combine(Path.GetTempPath(), $"ing_enhance_out_{Guid.NewGuid():N}.jpg");
        var script = Path.Combine(Path.GetTempPath(), $"ing_enhance_{Guid.NewGuid():N}.py");

        try
        {
            File.Copy(source, input, overwrite: false);
            await File.WriteAllTextAsync(script, PythonScript, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add(output);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Python could not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"Photo enhancement failed (exit {process.ExitCode}): {stderr[..Math.Min(400, stderr.Length)]}");

            var detail = JsonSerializer.Deserialize<ScriptResult>(stdout.Trim(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Photo enhancement returned no result.");

            var bytes = await File.ReadAllBytesAsync(output, ct);
            var url = await photos.SavePhotoAsync(PhotoLibrary.PhotoBoxFolder, bytes, "jpg");
            log.Add("Info", "AI photo enhanced",
                $"{url} — {(detail.AiDetected ? "AI subject" : "smart fallback")}, {detail.CropPercent}% crop, {detail.Width}x{detail.Height}");
            return new Result(url, detail.AiDetected, detail.CropPercent, detail.Width, detail.Height);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
            TryDelete(script);
        }
    }

    private string ResolveLocalPhoto(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A photo URL is required.");
        var clean = Uri.UnescapeDataString(url.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        string root;
        string relative;
        var photosPrefix = "photos" + Path.DirectorySeparatorChar;
        var generatedPrefix = "generated-photos" + Path.DirectorySeparatorChar;
        if (clean.StartsWith(photosPrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(env.ContentRootPath, "photos");
            relative = clean[photosPrefix.Length..];
        }
        else if (clean.StartsWith(generatedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(env.ContentRootPath, "generated-photos");
            relative = clean[generatedPrefix.Length..];
        }
        else
        {
            throw new ArgumentException("Only photos already saved by ING Lister can be enhanced.");
        }

        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("The saved photo could not be found.");
        return full;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record ScriptResult(bool AiDetected, int CropPercent, int Width, int Height);

    // AI gives us subject location, not permission to delete pixels. The classic border detector
    // is unioned with its mask so a small charger, strap, memory card, or photographed flaw cannot
    // disappear merely because a segmentation model thought the main product mattered more.
    private const string PythonScript = """
import json, sys
import numpy as np
from PIL import Image, ImageOps, ImageEnhance, ImageFilter

src, dst = sys.argv[1], sys.argv[2]
img = ImageOps.exif_transpose(Image.open(src)).convert('RGB')
ow, oh = img.size

# Keep enough resolution for eBay zoom while keeping local model inference responsive.
if max(img.size) > 2400:
    scale = 2400.0 / max(img.size)
    img = img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.Resampling.LANCZOS)

arr = np.asarray(img).astype(np.int16)
h, w = arr.shape[:2]
edge = max(2, int(min(w, h) * .025))
border = np.concatenate((arr[:edge].reshape(-1, 3), arr[-edge:].reshape(-1, 3),
                         arr[:, :edge].reshape(-1, 3), arr[:, -edge:].reshape(-1, 3)), axis=0)
bg = np.median(border, axis=0)
border_dist = np.sqrt(((border - bg) ** 2).sum(axis=1))
dist = np.sqrt(((arr - bg) ** 2).sum(axis=2))
threshold = max(20.0, float(np.percentile(border_dist, 92)) + 11.0)
classic = dist > threshold

# Reject thin edge noise, but retain small real accessories down to 0.04% of the frame.
try:
    from scipy import ndimage
    labels, count = ndimage.label(classic)
    if count:
        sizes = np.bincount(labels.ravel())
        keep = sizes >= max(16, int(w * h * .0004))
        keep[0] = False
        classic = keep[labels]
except Exception:
    pass

ai_used = False
ai_mask = None
try:
    from rembg import remove, new_session
    # u2netp is the lightweight subject model: fast enough to run after every shutter press.
    ai = remove(img.convert('RGBA'), session=new_session('u2netp'), only_mask=True)
    ai_mask = np.asarray(ai.convert('L')) > 18
    ratio = float(ai_mask.mean())
    ai_used = .002 < ratio < .94
except Exception as ex:
    print('AI subject detector unavailable; using smart border detection: ' + str(ex), file=sys.stderr)

mask = classic
if ai_used:
    # Union protects accessories the AI did not call the main subject.
    mask = np.logical_or(ai_mask, classic)

ys, xs = np.where(mask)
if len(xs) < max(64, int(w * h * .001)):
    box = (0, 0, w, h)
else:
    x0, x1 = int(xs.min()), int(xs.max()) + 1
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    bw, bh = x1 - x0, y1 - y0
    pad = max(18, int(max(bw, bh) * .09))
    box = (max(0, x0 - pad), max(0, y0 - pad), min(w, x1 + pad), min(h, y1 + pad))
    # Busy backgrounds can make the classic mask cover everything. In that case prefer the AI box.
    if ai_used and ((box[2]-box[0]) * (box[3]-box[1]) > w * h * .94):
        ay, ax = np.where(ai_mask)
        ax0, ax1, ay0, ay1 = int(ax.min()), int(ax.max()) + 1, int(ay.min()), int(ay.max()) + 1
        apad = max(18, int(max(ax1-ax0, ay1-ay0) * .11))
        box = (max(0, ax0-apad), max(0, ay0-apad), min(w, ax1+apad), min(h, ay1+apad))

cropped = img.crop(box)
crop_percent = max(0, min(99, round(100 * (1 - (cropped.width * cropped.height) / float(w * h)))))

# Exposure is deliberately gentle. It lifts a dim phone frame without turning a black product gray.
lum = np.asarray(cropped.convert('L'))
mean_lum = float(np.mean(lum))
exposure = max(1.04, min(1.20, 185.0 / max(1.0, mean_lum)))
enhanced = ImageEnhance.Brightness(cropped).enhance(exposure)
enhanced = ImageOps.autocontrast(enhanced, cutoff=.35)
enhanced = ImageEnhance.Contrast(enhanced).enhance(1.07)
enhanced = ImageEnhance.Color(enhanced).enhance(1.06)
enhanced = ImageEnhance.Sharpness(enhanced).enhance(1.18)

# eBay's gallery is square. Preserve the entire safe crop, then center it with a clean margin.
canvas = 1600
margin = int(canvas * .065)
enhanced.thumbnail((canvas - margin * 2, canvas - margin * 2), Image.Resampling.LANCZOS)
result = Image.new('RGB', (canvas, canvas), (255, 255, 255))
result.paste(enhanced, ((canvas - enhanced.width) // 2, (canvas - enhanced.height) // 2))
result.save(dst, 'JPEG', quality=95, optimize=True, progressive=True)
print(json.dumps({'aiDetected': ai_used, 'cropPercent': crop_percent, 'width': canvas, 'height': canvas}))
""";
}
