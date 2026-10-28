using System.Net;

namespace MeetingCopilot.Core;

public enum CompatibleAuthentication { ApiKey, None }
public enum CompatibleOutputFormat { JsonObject, JsonSchema, PromptOnly }
public enum CompatibleTokenParameter { MaxCompletionTokens, MaxTokens }

public sealed record CompatibleModelOptions(
    CompatibleAuthentication Authentication = CompatibleAuthentication.ApiKey,
    CompatibleOutputFormat OutputFormat = CompatibleOutputFormat.JsonObject,
    CompatibleTokenParameter TokenParameter = CompatibleTokenParameter.MaxCompletionTokens,
    int MaxOutputTokens = 4096,
    int TimeoutSeconds = 45,
    int ContextCharacters = 24_000)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Authentication) || !Enum.IsDefined(OutputFormat) || !Enum.IsDefined(TokenParameter) ||
            MaxOutputTokens is not (2200 or 4096 or 8192) || TimeoutSeconds is not (45 or 60 or 90) ||
            ContextCharacters is not (16_000 or 24_000 or 48_000))
            throw new ArgumentException("Select supported compatible-model authentication, output and request limits.");
    }

    public Uri GetRequestUri(string endpoint, string model, string key)
    {
        Validate();
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsLocal(uri)))
            throw new ArgumentException("Use an HTTPS API base URL, or HTTP only on localhost / a loopback IP. Do not put credentials, query strings or fragments in the URL.");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter the API base URL (usually ending in /v1), not the chat/completions or responses URL.");
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Enter the exact model ID accepted by this server.");
        if (Authentication == CompatibleAuthentication.None && !IsLocal(uri))
            throw new ArgumentException("No-key authentication is supported only for a local loopback server. Use an API key for remote endpoints.");
        if (Authentication == CompatibleAuthentication.ApiKey &&
            (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)))
            throw new ArgumentException("Enter this provider's API key without control characters. Foundry keys are not reused.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }

    private static bool IsLocal(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}
