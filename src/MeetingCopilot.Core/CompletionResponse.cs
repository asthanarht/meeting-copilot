using System.Text.Json;
using System.Net;

namespace MeetingCopilot.Core;

internal static class CompletionResponse
{
    internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = await ReadDocumentAsync(response, cancellationToken);
        return ReadChatText(json.RootElement);
    }

    internal static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ModelAuthenticationException($"Model provider denied access (HTTP {(int)response.StatusCode}). Check this provider's API key and model permissions.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Model provider returned HTTP {(int)response.StatusCode}. Check the API base URL, model ID, output format, token parameter, request size and quota. Redirects are not followed.",
                    null, response.StatusCode);
            return await ReadDocumentAsync(response, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new HttpRequestException("Could not reach the model provider. Check the URL, TLS certificate, network or running local server.");
        }
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > 128_000)
            throw new InvalidDataException("Model response exceeded the permitted size.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 128_000)
                throw new InvalidDataException("Model response exceeded the permitted size.");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    internal static string ReadChatText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw new InvalidDataException("Model returned an invalid completion envelope.");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("finish_reason", out var finish) ||
            finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop")
            throw new InvalidDataException("Model response was incomplete or filtered; no suggestion was accepted.");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Model returned no text completion.");
        return content.GetString()!;
    }
}
