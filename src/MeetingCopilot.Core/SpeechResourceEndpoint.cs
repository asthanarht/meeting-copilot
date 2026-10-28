namespace MeetingCopilot.Core;

public static class SpeechResourceEndpoint
{
    public static Uri Parse(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter the HTTPS Speech resource root URL, without an API path.");
        return uri;
    }
}
