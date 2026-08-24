using System.Diagnostics;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a raw phone frame into the photo that should actually go on the listing. A lightweight
/// segmentation model supplies the subject bounds, but only when independent quality checks agree
/// that the complete product was found. Otherwise enhancement is refused and the original remains
/// untouched. An accepted enhanced copy is saved beside the original in the Photo Library.
/// </summary>
public sealed class PhotoEnhancer(IWebHostEnvironment env, PhotoLibrary photos, ActionLog log)
{
    /// <summary>The model ran, but its mask was not trustworthy enough to alter a product photo.</summary>
    public sealed class RejectedException(string message) : Exception(message);

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
            if (process.ExitCode == 42 && stderr.Contains("SAFE_REJECT:", StringComparison.Ordinal))
            {
                var reason = stderr[(stderr.IndexOf("SAFE_REJECT:", StringComparison.Ordinal) + "SAFE_REJECT:".Length)..]
                    .Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                throw new RejectedException(reason);
            }
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

    // AI gives us the subject silhouette. If that silhouette cannot prove it is safe, enhancement
    // refuses the job and the caller keeps the original. A damaged product photo is never a useful
    // fallback; conservative failure is part of the feature.
    private const string PythonScript = """
import json, sys
import numpy as np
from PIL import Image, ImageOps, ImageEnhance, ImageFilter

src, dst = sys.argv[1], sys.argv[2]

def safe_reject(reason):
    print('SAFE_REJECT:' + reason, file=sys.stderr)
    raise SystemExit(42)

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
ai_alpha = None
mask_quality = {}
try:
    from rembg import remove, new_session
    # u2netp is the lightweight subject model: fast enough to run after every shutter press.
    ai = remove(img.convert('RGBA'), session=new_session('u2netp'), only_mask=True)
    ai_alpha = np.asarray(ai.convert('L'))
    ai_mask = ai_alpha > 18
    ratio = float(ai_mask.mean())
    ai_used = .02 < ratio < .82
    if ai_used:
        # Keep the product and any substantial disconnected accessory, but discard dust, sheet
        # marks and isolated segmentation flecks. A component under 0.8% of the main subject is
        # not allowed to become a floating artifact in the gallery image.
        try:
            from scipy import ndimage
            ai_labels, ai_count = ndimage.label(ai_mask)
            if ai_count > 1:
                ai_sizes = np.bincount(ai_labels.ravel())
                main_size = int(ai_sizes[1:].max())
                ai_keep = ai_sizes >= max(24, int(main_size * .008))
                ai_keep[0] = False
                kept_size = int(ai_sizes[ai_keep].sum())
                original_size = int(ai_sizes[1:].sum())
                significant = int(ai_keep[1:].sum())
                ai_mask = ai_keep[ai_labels]
                ai_alpha = np.where(ai_mask, ai_alpha, 0).astype(np.uint8)
                mask_quality['kept_fraction'] = kept_size / max(1.0, original_size)
                mask_quality['significant_components'] = significant
        except Exception:
            pass
except Exception as ex:
    safe_reject('The subject detector was unavailable, so the original was kept.')

if not ai_used:
    safe_reject('The app could not isolate the whole product with enough confidence, so the original was kept.')

# Quality gate. These are deliberately strict: AI Enhance is automatic, so uncertainty is a reason
# to do nothing. The separate Cut out button remains available when the seller wants to try an
# aggressive background removal by hand.
ai_area = int(ai_mask.sum())
clean_ratio = ai_area / max(1.0, w * h)
if not .02 < clean_ratio < .82:
    safe_reject('The cleaned subject mask was incomplete or covered too much of the frame, so the original was kept.')
if mask_quality.get('kept_fraction', 1.0) < .94:
    safe_reject('The mask would discard separate product details or accessories, so the original was kept.')
if mask_quality.get('significant_components', 1) > 8:
    safe_reject('The scene contains too many separate objects for a safe automatic cut-out.')

edge_band = max(2, int(min(w, h) * .012))
edge_hits = (int(ai_mask[:edge_band].sum()) + int(ai_mask[-edge_band:].sum()) +
             int(ai_mask[:, :edge_band].sum()) + int(ai_mask[:, -edge_band:].sum()))
if edge_hits / max(1.0, ai_area) > .035:
    safe_reject('The detected product reaches the frame edge and could be clipped, so the original was kept.')

classic_agreement = float(np.logical_and(ai_mask, classic).sum()) / max(1.0, ai_area)
if classic_agreement < .58:
    safe_reject('Two independent subject checks disagreed, so the original was kept.')

uncertain_alpha = np.logical_and(ai_alpha > 18, ai_alpha < 180)
if float(uncertain_alpha.sum()) / max(1.0, ai_area) > .24:
    safe_reject('The subject edge was too uncertain for a clean replacement, so the original was kept.')

# The mask passed every gate above. Never merge in the border heuristic: doing that reintroduces
# uneven patches of the photographed sheet as literal islands beside the product.
mask = ai_mask

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

# Keep a soft subject alpha through the same crop. Never mix a fallback background heuristic into
# a successful AI silhouette; it cannot distinguish a white accessory from a bright patch of sheet.
subject_alpha = ai_alpha
alpha_img = Image.fromarray(subject_alpha, mode='L').crop(box)
alpha_img = alpha_img.filter(ImageFilter.MedianFilter(3)).filter(ImageFilter.GaussianBlur(1.15))

# Exposure is deliberately gentle. It lifts a dim phone frame without turning a black product gray.
lum = np.asarray(cropped.convert('L'))
mean_lum = float(np.mean(lum))
exposure = max(1.04, min(1.20, 185.0 / max(1.0, mean_lum)))
enhanced = ImageEnhance.Brightness(cropped).enhance(exposure)
enhanced = ImageOps.autocontrast(enhanced, cutoff=.35)
enhanced = ImageEnhance.Contrast(enhanced).enhance(1.07)
enhanced = ImageEnhance.Color(enhanced).enhance(1.06)
enhanced = ImageEnhance.Sharpness(enhanced).enhance(1.18)

# Build a neutral editorial studio—not a white document canvas. The gradient gives a dark product
# separation at every edge, while the subject mask removes the photographed sheet and its uneven
# corners completely. A soft shadow grounds the item without inventing any product detail.
canvas = 1600
margin = int(canvas * .075)
scale = min((canvas - margin * 2) / enhanced.width, (canvas - margin * 2) / enhanced.height)
new_size = (max(1, round(enhanced.width * scale)), max(1, round(enhanced.height * scale)))
subject = enhanced.resize(new_size, Image.Resampling.LANCZOS).convert('RGBA')
subject_alpha = alpha_img.resize(new_size, Image.Resampling.LANCZOS)
subject.putalpha(subject_alpha)

yy, xx = np.mgrid[0:canvas, 0:canvas]
t = (yy / float(canvas - 1))[..., None]
top = np.array([220.0, 216.0, 207.0])
bottom = np.array([166.0, 174.0, 176.0])
studio = top * (1.0 - t) + bottom * t
studio = np.broadcast_to(studio, (canvas, canvas, 3)).copy()
radial = np.sqrt(((xx - canvas * .50) / (canvas * .72)) ** 2 + ((yy - canvas * .40) / (canvas * .66)) ** 2)
spot = np.clip(1.0 - radial, 0.0, 1.0)[..., None]
studio += spot * 13.0
studio -= np.clip(radial - .55, 0.0, .65)[..., None] * 17.0
studio = np.clip(studio, 0, 255).astype(np.uint8)
result = Image.fromarray(studio, mode='RGB').convert('RGBA')

x = (canvas - subject.width) // 2
y = (canvas - subject.height) // 2 - int(canvas * .012)
shadow_alpha = subject_alpha.filter(ImageFilter.GaussianBlur(max(14, canvas / 70)))
shadow_alpha = shadow_alpha.point(lambda p: int(p * .28))
shadow = Image.new('RGBA', subject.size, (25, 30, 31, 0))
shadow.putalpha(shadow_alpha)
result.alpha_composite(shadow, (x + int(canvas * .012), y + int(canvas * .025)))
result.alpha_composite(subject, (x, y))
result = result.convert('RGB')
result.save(dst, 'JPEG', quality=95, optimize=True, progressive=True)
print(json.dumps({'aiDetected': ai_used, 'cropPercent': crop_percent, 'width': canvas, 'height': canvas}))
""";
}
