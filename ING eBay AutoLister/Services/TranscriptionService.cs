using System.Net.Http.Headers;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a few seconds of a live show's audio into text.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Claude cannot hear. The Messages API accepts text, images and PDFs and
/// nothing else — there is no audio content block — so "have the AI listen to the auctioneer"
/// necessarily means transcribing the audio somewhere else first and handing Claude the words.
/// </para>
/// <para>
/// The transcript is not the answer to anything on its own. It goes back to the live-lot read as
/// context alongside the frame, where the host's "brand new, sealed, retails four hundred" settles
/// a condition the picture could only guess at.
/// </para>
/// </remarks>
public class TranscriptionService(IHttpClientFactory httpClientFactory, ActionLog log)
{
    /// <summary>Why a clip produced no words. Callers show <see cref="Detail"/> and carry on.</summary>
    public record Result(bool Ok, string Text, string Detail);

    /// <summary>
    /// Whisper charges by audio length, and a live show is hours long. Clips are short by design and
    /// anything longer than this is a bug in the caller, not a request to spend more.
    /// </summary>
    private const int MaxClipBytes = 8 * 1024 * 1024;

    public async Task<Result> TranscribeAsync(byte[] audio, string fileName, string contentType,
        Credentials credentials, CancellationToken cancellationToken = default)
    {
        if (audio.Length == 0)
            return new Result(false, "", "The clip was empty — no audio reached the app.");

        if (audio.Length > MaxClipBytes)
            return new Result(false, "",
                $"That clip is {audio.Length / (1024 * 1024)}MB. Listening records in short pieces; something sent a whole recording.");

        var apiKey = credentials.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new Result(false, "",
                "No speech-to-text key. Claude cannot process audio directly, so the show's sound has to be " +
                "transcribed first. Add an OpenAI API key in Settings → AI Provider and listening turns on.");

        // A silent stretch is normal — the host stops talking between lots — and Whisper answers a
        // near-silent clip with a hallucinated stock phrase. `temperature: 0` and a prompt that names
        // the setting keep it closer to what was actually said.
        using var form = new MultipartFormDataContent();
        var clip = new ByteArrayContent(audio);
        clip.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType);
        form.Add(clip, "file", string.IsNullOrWhiteSpace(fileName) ? "clip.webm" : fileName);
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("text"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent(
            "A live auction. The host describes an item, its brand, its condition, and calls out bids."),
            "prompt");

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var response = await client.PostAsync(
                "https://api.openai.com/v1/audio/transcriptions", form, cancellationToken);
            var raw = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

            if (!response.IsSuccessStatusCode)
            {
                // An OpenAI error comes back as JSON even when text was asked for.
                var message = raw;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var m))
                        message = m.GetString() ?? raw;
                }
                catch (JsonException) { /* not JSON — the raw body is the best detail available */ }

                log.Add("Warning", "Could not transcribe the show's audio",
                    $"HTTP {(int)response.StatusCode}: {message[..Math.Min(300, message.Length)]}");

                return new Result(false, "", (int)response.StatusCode switch
                {
                    401 => "The OpenAI key was rejected. Check it in Settings → AI Provider.",
                    429 => "Speech-to-text is rate-limited or out of credit right now.",
                    _   => $"Speech-to-text returned HTTP {(int)response.StatusCode}.",
                });
            }

            return new Result(true, raw, raw.Length == 0 ? "Nobody was speaking in that moment." : "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(false, "", "Speech-to-text took too long and was dropped.");
        }
        catch (HttpRequestException ex)
        {
            log.Add("Warning", "Could not reach speech-to-text", ex.Message);
            return new Result(false, "", "Could not reach the speech-to-text service.");
        }
    }
}
