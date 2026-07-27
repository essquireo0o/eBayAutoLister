using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

public class ImageGenerationService(
    CredentialsStore creds,
    IHttpClientFactory httpClientFactory,
    ActionLog log,
    IWebHostEnvironment env)
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private const string DefaultPromptTemplate =
        "Professional ecommerce product photo of {ITEM}, white seamless background, " +
        "studio lighting, realistic detail, marketplace listing photography, no text, no watermarks";

    private const string NegativePrompt =
        "text, watermark, logo, signature, blurry, low quality, distorted, ugly, " +
        "bad anatomy, extra limbs, cropped, deformed, duplicate";

    private string PhotosDir => Path.Combine(env.ContentRootPath, "generated-photos");

    // ── Public API ─────────────────────────────────────────────────

    public async Task<List<string>> GenerateProductPhotosAsync(string title, string description, string? visualDescription = null, string? refImageBase64 = null, string? refMimeType = null)
    {
        var c = creds.Get();
        var mode = (c.ImageGenMode ?? "disabled").ToLowerInvariant();

        return mode switch
        {
            "local_sd" => await GenerateLocalSdAsync(title, visualDescription, c, refImageBase64, refMimeType),
            "dalle"    => await GenerateDalleAsync(title, visualDescription, c),
            _          => throw new InvalidOperationException(
                              "Image generation is off. Turn it on in Settings → Image generation (optional).")
        };
    }

    public async Task<(bool Online, string Message)> TestLocalServerAsync()
    {
        var c = creds.Get();
        return await TestEndpointAsync(c.LocalSdEndpoint ?? "http://127.0.0.1:7860", c.LocalSdBackend ?? "automatic1111");
    }

    public async Task<(bool Online, string Message)> TestEndpointAsync(string endpoint, string backend)
    {
        endpoint = (endpoint ?? "http://127.0.0.1:7860").TrimEnd('/');
        backend  = (backend  ?? "automatic1111").ToLowerInvariant();

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        try
        {
            if (backend == "comfyui")
            {
                var res = await client.GetAsync($"{endpoint}/system_stats");
                return res.IsSuccessStatusCode
                    ? (true,  $"ComfyUI online at {endpoint}")
                    : (false, $"ComfyUI HTTP {(int)res.StatusCode} at {endpoint}");
            }
            else
            {
                var res = await client.GetAsync($"{endpoint}/sdapi/v1/options");
                return res.IsSuccessStatusCode
                    ? (true,  $"AUTOMATIC1111 online at {endpoint}")
                    : (false, $"AUTOMATIC1111 HTTP {(int)res.StatusCode} at {endpoint}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Unreachable: {ex.Message.Split('\n')[0]}");
        }
    }

    public async Task<DetectResult> DetectLocalServersAsync()
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        const string a1111Ep = "http://127.0.0.1:7860";
        const string comfyEp = "http://127.0.0.1:8188";

        var a1111Task = ProbeEndpointAsync(client, a1111Ep, "automatic1111");
        var comfyTask = ProbeEndpointAsync(client, comfyEp,  "comfyui");
        await Task.WhenAll(a1111Task, comfyTask);

        var c = creds.Get();
        return new DetectResult(
            A1111Online:        a1111Task.Result,
            ComfyOnline:        comfyTask.Result,
            A1111Endpoint:      a1111Ep,
            ComfyEndpoint:      comfyEp,
            ConfiguredEndpoint: c.LocalSdEndpoint ?? a1111Ep,
            ConfiguredBackend:  c.LocalSdBackend  ?? "automatic1111",
            ConfiguredMode:     c.ImageGenMode    ?? "disabled"
        );
    }

    private static async Task<bool> ProbeEndpointAsync(HttpClient client, string endpoint, string backend)
    {
        try
        {
            var path = backend == "comfyui" ? "/system_stats" : "/sdapi/v1/options";
            var res  = await client.GetAsync($"{endpoint}{path}");
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public sealed record DetectResult(
        bool   A1111Online, bool   ComfyOnline,
        string A1111Endpoint, string ComfyEndpoint,
        string ConfiguredEndpoint, string ConfiguredBackend, string ConfiguredMode
    );

    public async Task<List<string>> GetComfyUiModelsAsync(string endpoint)
    {
        endpoint = (endpoint ?? "http://127.0.0.1:8188").TrimEnd('/');
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            var res = await client.GetAsync($"{endpoint}/object_info/CheckpointLoaderSimple");
            if (!res.IsSuccessStatusCode)
                return [];

            var body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("CheckpointLoaderSimple", out var node)) return [];
            if (!node.TryGetProperty("input", out var input)) return [];
            if (!input.TryGetProperty("required", out var required)) return [];
            if (!required.TryGetProperty("ckpt_name", out var ckptNameProp)) return [];

            var arr = ckptNameProp.EnumerateArray().FirstOrDefault();
            if (arr.ValueKind != JsonValueKind.Array) return [];

            return [.. arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
        }
        catch { return []; }
    }

    // ── Local Stable Diffusion ────────────────────────────────────

    private async Task<List<string>> GenerateLocalSdAsync(string title, string? visualDescription, Credentials c, string? refImageBase64 = null, string? refMimeType = null)
    {
        var endpoint = (c.LocalSdEndpoint ?? "http://127.0.0.1:7860").TrimEnd('/');
        var backend  = (c.LocalSdBackend  ?? "automatic1111").ToLowerInvariant();
        var template = string.IsNullOrWhiteSpace(c.ImagePromptTemplate) ? DefaultPromptTemplate : c.ImagePromptTemplate;
        var subject  = !string.IsNullOrWhiteSpace(visualDescription) ? visualDescription : title;
        var basePrompt = template.Replace("{ITEM}", subject, StringComparison.OrdinalIgnoreCase);

        string[] suffixes = ["front view on white background", "3/4 angle view showing all sides", "close-up detail view of screen and ports"];
        var prompts = suffixes.Select(s => $"{basePrompt}, {s}").ToArray();

        bool hasRef = !string.IsNullOrWhiteSpace(refImageBase64);
        byte[][] images;

        if (backend == "comfyui")
        {
            var list = new List<byte[]>();
            if (hasRef)
            {
                var uploaded = await UploadImageToComfyAsync(endpoint, refImageBase64!, refMimeType ?? "image/jpeg");
                foreach (var p in prompts) list.Add(await GenerateComfyUiImg2ImgAsync(endpoint, p, c.LocalSdModelName, uploaded));
            }
            else
            {
                foreach (var p in prompts) list.Add(await GenerateComfyUiAsync(endpoint, p, c.LocalSdModelName));
            }
            images = [.. list];
        }
        else
        {
            images = hasRef
                ? await Task.WhenAll(prompts.Select(p => GenerateA1111Img2ImgAsync(endpoint, p, refImageBase64!)))
                : await Task.WhenAll(prompts.Select(p => GenerateA1111Async(endpoint, p)));
        }

        var mode = hasRef ? "img2img (product photo reference)" : "txt2img";
        log.Add("Info", $"Local SD: {images.Length} image(s) generated ({mode})", $"Backend: {backend}; Item: {title}");
        return [.. await Task.WhenAll(images.Select(b => SaveLocallyAsync(b, "png")))];
    }

    private async Task<byte[]> GenerateA1111Async(string endpoint, string prompt)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(180);

        var payload = JsonSerializer.Serialize(new
        {
            prompt,
            negative_prompt = NegativePrompt,
            width      = 1024,
            height     = 1024,
            steps      = 28,
            cfg_scale  = 7.0,
            sampler_name = "DPM++ 2M",
            batch_size = 1
        }, _json);

        var response = await client.PostAsync($"{endpoint}/sdapi/v1/txt2img",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception(
                $"AUTOMATIC1111 returned HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var base64 = doc.RootElement.GetProperty("images")[0].GetString()
            ?? throw new Exception("AUTOMATIC1111 response contained no images.");

        return Convert.FromBase64String(base64);
    }

    private async Task<byte[]> GenerateComfyUiAsync(string endpoint, string prompt, string modelName)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(180);

        var clientId   = Guid.NewGuid().ToString("N");
        var checkpoint = string.IsNullOrWhiteSpace(modelName) ? "" : modelName;
        if (string.IsNullOrWhiteSpace(checkpoint))
            throw new Exception("ComfyUI checkpoint is not configured. Go to Settings → Image Generation, click Load Models, select a model, and save.");
        var workflow  = BuildComfyWorkflow(prompt, checkpoint, clientId);

        var queueBody = JsonSerializer.Serialize(new { prompt = workflow, client_id = clientId }, _json);
        var queueRes  = await client.PostAsync($"{endpoint}/prompt",
            new StringContent(queueBody, Encoding.UTF8, "application/json"));

        var queueRaw = await queueRes.Content.ReadAsStringAsync();
        if (!queueRes.IsSuccessStatusCode)
            throw new Exception($"ComfyUI queue error (HTTP {(int)queueRes.StatusCode}): {queueRaw[..Math.Min(300, queueRaw.Length)]}");

        using var queueDoc = JsonDocument.Parse(queueRaw);
        var promptId = queueDoc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new Exception("ComfyUI did not return a prompt_id.");

        // Poll history until the generation completes (max 90 s)
        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(2000);
            var histRes = await client.GetAsync($"{endpoint}/history/{promptId}");
            if (!histRes.IsSuccessStatusCode) continue;

            using var histDoc = JsonDocument.Parse(await histRes.Content.ReadAsStringAsync());
            if (!histDoc.RootElement.TryGetProperty(promptId, out var entry)) continue;
            if (!entry.TryGetProperty("outputs", out var outputs)) continue;

            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var images)) continue;
                var img = images.EnumerateArray().FirstOrDefault();
                if (img.ValueKind == JsonValueKind.Undefined) continue;

                var filename  = img.GetProperty("filename").GetString() ?? "";
                var subfolder = img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
                var imgUrl    = $"{endpoint}/view?filename={Uri.EscapeDataString(filename)}"
                              + (subfolder.Length > 0 ? $"&subfolder={Uri.EscapeDataString(subfolder)}" : "");

                var imgRes = await client.GetAsync(imgUrl);
                if (imgRes.IsSuccessStatusCode)
                    return await imgRes.Content.ReadAsByteArrayAsync();
            }
        }

        throw new Exception("ComfyUI generation timed out after 90 seconds.");
    }

    private async Task<string> UploadImageToComfyAsync(string endpoint, string base64, string mimeType)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var bytes = Convert.FromBase64String(base64);
        var ext   = mimeType.Contains("png") ? "png" : "jpg";
        var name  = $"ing_ref_{Guid.NewGuid():N}.{ext}";

        using var content = new MultipartFormDataContent();
        var imgContent = new ByteArrayContent(bytes);
        imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        content.Add(imgContent, "image", name);
        content.Add(new StringContent("input"), "type");
        content.Add(new StringContent("true"), "overwrite");

        var res  = await client.PostAsync($"{endpoint}/upload/image", content);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"ComfyUI image upload failed (HTTP {(int)res.StatusCode}): {body[..Math.Min(200, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("name").GetString()
               ?? throw new Exception("ComfyUI upload response missing 'name'.");
    }

    private async Task<byte[]> GenerateComfyUiImg2ImgAsync(string endpoint, string prompt, string? modelName, string uploadedImageName)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(180);

        var clientId   = Guid.NewGuid().ToString("N");
        var checkpoint = string.IsNullOrWhiteSpace(modelName) ? "" : modelName;
        if (string.IsNullOrWhiteSpace(checkpoint))
            throw new Exception("ComfyUI checkpoint is not configured. Go to Settings → Image Generation, click Load Models, select a model, and save.");

        var workflow = BuildComfyImg2ImgWorkflow(prompt, checkpoint, uploadedImageName, clientId);
        var queueBody = JsonSerializer.Serialize(new { prompt = workflow, client_id = clientId }, _json);
        var queueRes  = await client.PostAsync($"{endpoint}/prompt",
            new StringContent(queueBody, Encoding.UTF8, "application/json"));

        var queueRaw = await queueRes.Content.ReadAsStringAsync();
        if (!queueRes.IsSuccessStatusCode)
            throw new Exception($"ComfyUI queue error (HTTP {(int)queueRes.StatusCode}): {queueRaw[..Math.Min(300, queueRaw.Length)]}");

        using var queueDoc = JsonDocument.Parse(queueRaw);
        var promptId = queueDoc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new Exception("ComfyUI did not return a prompt_id.");

        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(2000);
            var histRes = await client.GetAsync($"{endpoint}/history/{promptId}");
            if (!histRes.IsSuccessStatusCode) continue;

            using var histDoc = JsonDocument.Parse(await histRes.Content.ReadAsStringAsync());
            if (!histDoc.RootElement.TryGetProperty(promptId, out var entry)) continue;
            if (!entry.TryGetProperty("outputs", out var outputs)) continue;

            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var images)) continue;
                var img = images.EnumerateArray().FirstOrDefault();
                if (img.ValueKind == JsonValueKind.Undefined) continue;

                var filename  = img.GetProperty("filename").GetString() ?? "";
                var subfolder = img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
                var imgUrl    = $"{endpoint}/view?filename={Uri.EscapeDataString(filename)}"
                              + (subfolder.Length > 0 ? $"&subfolder={Uri.EscapeDataString(subfolder)}" : "");

                var imgRes = await client.GetAsync(imgUrl);
                if (imgRes.IsSuccessStatusCode)
                    return await imgRes.Content.ReadAsByteArrayAsync();
            }
        }

        throw new Exception("ComfyUI img2img generation timed out after 90 seconds.");
    }

    private async Task<byte[]> GenerateA1111Img2ImgAsync(string endpoint, string prompt, string base64)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(180);

        var payload = JsonSerializer.Serialize(new
        {
            init_images      = new[] { base64 },
            prompt,
            negative_prompt  = NegativePrompt,
            width            = 1024,
            height           = 1024,
            steps            = 28,
            cfg_scale        = 7.0,
            sampler_name     = "DPM++ 2M",
            denoising_strength = 0.65,
            batch_size       = 1
        }, _json);

        var response = await client.PostAsync($"{endpoint}/sdapi/v1/img2img",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"AUTOMATIC1111 img2img HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var b64 = doc.RootElement.GetProperty("images")[0].GetString()
            ?? throw new Exception("AUTOMATIC1111 img2img response contained no images.");

        return Convert.FromBase64String(b64);
    }

    private static Dictionary<string, object> BuildComfyImg2ImgWorkflow(string prompt, string checkpoint, string imageName, string clientId)
    {
        return new Dictionary<string, object>
        {
            ["1"] = new { class_type = "LoadImage",               inputs = new { image = imageName, upload = "image" } },
            ["2"] = new { class_type = "VAEEncode",               inputs = new { pixels = new object[] { "1", 0 }, vae = new object[] { "3", 2 } } },
            ["3"] = new { class_type = "CheckpointLoaderSimple",  inputs = new { ckpt_name = checkpoint } },
            ["4"] = new { class_type = "CLIPTextEncode",          inputs = new { text = prompt,        clip = new object[] { "3", 1 } } },
            ["5"] = new { class_type = "CLIPTextEncode",          inputs = new { text = NegativePrompt, clip = new object[] { "3", 1 } } },
            ["6"] = new { class_type = "KSampler",                inputs = new {
                seed         = new Random().Next(0, int.MaxValue),
                steps        = 25,
                cfg          = 7.0,
                sampler_name = "dpmpp_2m",
                scheduler    = "karras",
                denoise      = 0.65,
                model        = new object[] { "3", 0 },
                positive     = new object[] { "4", 0 },
                negative     = new object[] { "5", 0 },
                latent_image = new object[] { "2", 0 }
            }},
            ["7"] = new { class_type = "VAEDecode",  inputs = new { samples = new object[] { "6", 0 }, vae = new object[] { "3", 2 } } },
            ["8"] = new { class_type = "SaveImage",  inputs = new { filename_prefix = $"ing_i2i_{clientId[..8]}", images = new object[] { "7", 0 } } }
        };
    }

    private static Dictionary<string, object> BuildComfyWorkflow(string prompt, string checkpoint, string clientId)
    {
        // Minimal KSampler workflow compatible with most ComfyUI installs
        return new Dictionary<string, object>
        {
            ["3"] = new { class_type = "KSampler", inputs = new {
                seed         = new Random().Next(0, int.MaxValue),
                steps        = 28,
                cfg          = 7.0,
                sampler_name = "dpmpp_2m",
                scheduler    = "karras",
                denoise      = 1.0,
                model        = new object[] { "4", 0 },
                positive     = new object[] { "6", 0 },
                negative     = new object[] { "7", 0 },
                latent_image = new object[] { "5", 0 }
            }},
            ["4"] = new { class_type = "CheckpointLoaderSimple", inputs = new { ckpt_name = checkpoint } },
            ["5"] = new { class_type = "EmptyLatentImage", inputs = new { width = 1024, height = 1024, batch_size = 1 } },
            ["6"] = new { class_type = "CLIPTextEncode", inputs = new { text = prompt,                      clip = new object[] { "4", 1 } } },
            ["7"] = new { class_type = "CLIPTextEncode", inputs = new { text = NegativePrompt,              clip = new object[] { "4", 1 } } },
            ["8"] = new { class_type = "VAEDecode",           inputs = new { samples = new object[] { "3", 0 }, vae = new object[] { "4", 2 } } },
            ["9"] = new { class_type = "SaveImage",           inputs = new { filename_prefix = $"ing_{clientId[..8]}", images = new object[] { "8", 0 } } }
        };
    }

    // ── DALL-E (legacy) ───────────────────────────────────────────

    private async Task<List<string>> GenerateDalleAsync(string title, string? visualDescription, Credentials c)
    {
        var apiKey = c.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Add it in Settings → AI Provider.");

        var template   = string.IsNullOrWhiteSpace(c.ImagePromptTemplate) ? DefaultPromptTemplate : c.ImagePromptTemplate;
        var subject    = !string.IsNullOrWhiteSpace(visualDescription) ? visualDescription : title;
        var basePrompt = template.Replace("{ITEM}", subject, StringComparison.OrdinalIgnoreCase);

        string[] suffixes = ["front view", "3/4 angle view", "close-up detail view"];
        var prompts = suffixes.Select(s => $"{basePrompt}, {s}").ToArray();

        using var apiClient = httpClientFactory.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var imageTasks = prompts.Select(async p =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model   = "dall-e-3",
                prompt  = p,
                n       = 1,
                size    = "1024x1024",
                quality = "standard"
            }, _json);

            var res = await apiClient.PostAsync("https://api.openai.com/v1/images/generations",
                new StringContent(body, Encoding.UTF8, "application/json"));

            var raw = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new Exception($"DALL-E HTTP {(int)res.StatusCode}: {raw[..Math.Min(400, raw.Length)]}");

            using var doc    = JsonDocument.Parse(raw);
            var imageUrl     = doc.RootElement.GetProperty("data")[0].GetProperty("url").GetString()!;

            // Download and save locally so the URL is permanent
            using var dlClient = httpClientFactory.CreateClient();
            return await dlClient.GetByteArrayAsync(imageUrl);
        });

        var bytes = await Task.WhenAll(imageTasks);
        log.Add("Info", $"DALL-E: {bytes.Length} image(s) generated and saved locally", title);
        return [.. await Task.WhenAll(bytes.Select(b => SaveLocallyAsync(b, "jpg")))];
    }

    // ── Local file storage ────────────────────────────────────────

    private async Task<string> SaveLocallyAsync(byte[] bytes, string ext)
    {
        Directory.CreateDirectory(PhotosDir);
        var filename = $"{Guid.NewGuid():N}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(PhotosDir, filename), bytes);
        return $"/generated-photos/{filename}";
    }
}
