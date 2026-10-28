using MeetingCopilot.Core;
using Microsoft.CognitiveServices.Speech;

namespace MeetingCopilot.Desktop;

internal static class StreamingSpeechConfiguration
{
    public static void Validate(string key, string region, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Enter the Speech API key.");
        if (string.IsNullOrWhiteSpace(region))
            _ = SpeechResourceEndpoint.Parse(endpoint);
        else if (region.Trim().Length > 64 || !region.Trim().All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Enter a Speech region such as eastus, or leave it blank to use the resource endpoint.");
    }

    public static SpeechConfig Create(string key, string region, string locale, string endpoint)
    {
        Validate(key, region, endpoint);
        var config = string.IsNullOrWhiteSpace(region)
            ? SpeechConfig.FromEndpoint(new UriBuilder(SpeechResourceEndpoint.Parse(endpoint))
            {
                Scheme = "wss",
                Port = -1
            }.Uri, key.Trim())
            : SpeechConfig.FromSubscription(key.Trim(), region.Trim());
        config.SpeechRecognitionLanguage = locale;
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "650");
        config.SetProperty("Speech_SegmentationStrategy", "Time");
        config.SetProperty("Speech_SegmentationMaximumTimeMs", "10000");
        return config;
    }
}
