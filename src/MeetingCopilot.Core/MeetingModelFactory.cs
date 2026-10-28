namespace MeetingCopilot.Core;

public static class MeetingModelFactory
{
    public static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static IMeetingModel Create(HttpClient http, ModelSettings settings) => settings.Connection switch
    {
        ModelConnection.ResponsesApiKey or ModelConnection.ResponsesEntra => new FoundryResponsesModel(http, settings),
        ModelConnection.ChatCompletionsApiKey => new AzureMeetingModel(http, settings),
        ModelConnection.OpenAICompatible => new CompatibleMeetingModel(http, settings),
        ModelConnection.Gemini or ModelConnection.Anthropic or ModelConnection.Grok => new NativeMeetingModel(http, settings),
        _ => throw new ArgumentException("Select a supported model connection.")
    };

    public static async Task TestAsync(IMeetingModel model, CancellationToken cancellationToken)
    {
        var result = await model.AnalyzeAsync(new(1, "Synthetic connection test, not a real meeting.", "",
            [new(1, DateTimeOffset.UtcNow, "Synthetic playback",
                "Our payment gateway supports idempotency keys. Retrying with new keys caused duplicate charges. We need a safer retry approach.")],
            [], null, QuestionRequested: true), cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Memory))
            throw new InvalidDataException("The model returned empty memory for the synthetic discussion. Check the model and output-format settings.");
    }
}
