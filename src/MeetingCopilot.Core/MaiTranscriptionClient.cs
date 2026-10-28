using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeetingCopilot.Core;

public sealed record MaiSpeechSettings(string Endpoint, string ApiKey, string[] Phrases)
{
    public Uri GetRequestUri()
    {
        var uri = SpeechResourceEndpoint.Parse(Endpoint);
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("Enter the Speech API key in the password field.");
        if (Phrases.Length > 50 || Phrases.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 100))
            throw new ArgumentException("Use up to 50 vocabulary hints, each at most 100 characters.");
        return new(uri, "speechtotext/transcriptions:transcribe?api-version=2025-10-15");
    }
}

public interface IAudioTranscriber
{
    Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellationToken);
}

public sealed class MaiTranscriptionClient(HttpClient http, MaiSpeechSettings settings) : IAudioTranscriber
{
    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.GetRequestUri());
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.ApiKey);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(CreateWav(pcm));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "meeting-segment.wav");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            locales = new[] { "en" },
            diarization = new { enabled = true },
            phraseList = new { phrases = settings.Phrases },
            enhancedMode = new
            {
                enabled = true,
                model = "mai-transcribe-2",
                modelOptions = new { timestamps = "word", transcribeStyle = "verbatim" }
            }
        }), Encoding.UTF8, "application/json"), "definition");
        request.Content = form;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MAI transcription returned HTTP {(int)response.StatusCode}. Check the Speech key, endpoint, model region availability and quota.",
                null, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 1_000_000)
                throw new InvalidDataException("MAI response exceeded the safe size limit.");
            buffer.Write(bytes, 0, count);
        }
        return Parse(buffer.ToArray());
    }

    public static byte[] CreateWav(byte[] pcm)
    {
        if (pcm.Length is 0 or > PcmSegmenter.MaxBytes || pcm.Length % 2 != 0)
            throw new ArgumentException("Audio must be up to six seconds of 16 kHz mono PCM16.");
        using var output = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return output.ToArray();
    }

    public static string Parse(byte[] response)
    {
        using var json = JsonDocument.Parse(response);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("combinedPhrases", out var phrases) ||
            phrases.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MAI returned an invalid transcription response.");
        var parts = new List<string>();
        int length = 0;
        foreach (var phrase in phrases.EnumerateArray())
        {
            if (phrase.ValueKind != JsonValueKind.Object ||
                !phrase.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("MAI returned an invalid transcript phrase.");
            string value = text.GetString()!;
            length += value.Length + 1;
            if (length > 8000)
                throw new InvalidDataException("MAI transcript exceeded the safe utterance limit.");
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(value.Trim());
        }
        // Empty combinedPhrases is valid for silence; missing text is not.
        return string.Join(" ", parts);
    }
}
