namespace MeetingCopilot.Core;

public sealed record Utterance(long Id, DateTimeOffset At, string Source, string Text, string Kind = "speech");

public sealed record Cue(
    string Kind, string Text, string Reason, string Caveat, long[] EvidenceIds);

public sealed record AnalysisRequest(
    long Revision,
    string MeetingBrief,
    string Memory,
    Utterance[] Utterances,
    string[] Dismissed,
    Cue? CurrentCue,
    bool QuestionRequested = false, // Legacy wire name: requests any useful contribution, not only a question.
    string HistoricalMemory = "");

public sealed record MeetingMemory(long ThroughRevision, string Text);

public sealed record AnalysisResult(
    string Memory, string CurrentCueStatus, Cue? Suggestion, string? NoSuggestionReason = null);

public sealed record AnalysisProgress(long ReceivedSegments, long AnalyzedRevision,
    DateTimeOffset? StartedAt, DateTimeOffset? LastSucceededAt, TimeSpan? LastDuration,
    DateTimeOffset? RetryAt, string? Error, int MemoryVersions = 0);

public sealed record AnalysisUpdate(
    Cue? Cue,
    string Status,
    string? Error = null,
    TimeSpan? Latency = null,
    Utterance[]? Evidence = null,
    CueState RetiredState = CueState.Superseded,
    bool Provisional = false);

public enum CueState { Live, Provisional, Superseded, Addressed, Expired, Dismissed, Covered, Paused, Unavailable }

public sealed record SlideAnalysis(string Title, string VisibleContent, string Interpretation, string Uncertainty)
{
    public string ToContext() =>
        $"Slide title (AI-extracted): {Title}\nVisible content (AI-extracted): {VisibleContent}\n" +
        $"Diagram interpretation (not a quotation): {Interpretation}\nUncertainty: {Uncertainty}";
}

public interface ISlideModel
{
    Task<SlideAnalysis> ReadSlideAsync(byte[] png, CancellationToken cancellationToken);
}

public interface IMeetingModel
{
    TimeSpan RequestTimeout => MeetingEngine.AnalysisTimeout;
    int ContextCharacterBudget => 24_000;
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken);
}

public enum ModelConnection
{
    ChatCompletionsApiKey = 0, ResponsesEntra = 1, ResponsesApiKey = 2,
    OpenAICompatible = 3, Gemini = 4, Anthropic = 5, Grok = 6
}

public sealed class ModelAuthenticationException(string message) : Exception(message);

public sealed record ModelSettings(string Endpoint, string Deployment, string ApiKey,
    ModelConnection Connection = ModelConnection.ChatCompletionsApiKey, string EntraTenantId = "",
    CompatibleModelOptions? Compatible = null)
{
    public Uri GetRequestUri()
    {
        if (!Enum.IsDefined(Connection))
            throw new ArgumentException("Select a supported model connection.");
        if (Connection == ModelConnection.OpenAICompatible)
            return (Compatible ?? new()).GetRequestUri(Endpoint, Deployment, ApiKey);
        if (Connection is ModelConnection.Gemini or ModelConnection.Anthropic or ModelConnection.Grok)
        {
            var options = Compatible ?? new();
            if (options.Authentication != CompatibleAuthentication.ApiKey)
                throw new ArgumentException("This provider requires its own API key.");
            _ = options.GetRequestUri(Endpoint, Deployment, ApiKey);
            var endpoint = new Uri(Endpoint.Trim().TrimEnd('/') + "/");
            if (endpoint.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Native cloud providers require HTTPS.");
            if (Connection == ModelConnection.Gemini &&
                Deployment.Trim().Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                throw new ArgumentException("Enter a bare Gemini Developer API model ID without models/, spaces or URL characters.");
            if (Connection == ModelConnection.Anthropic && endpoint.AbsolutePath.TrimEnd('/').EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Enter the Claude API base URL ending in /v1, not /v1/messages.");
            return Connection switch
            {
                ModelConnection.Gemini => new Uri(endpoint, $"models/{Uri.EscapeDataString(Deployment.Trim())}:generateContent"),
                ModelConnection.Anthropic => new Uri(endpoint, "messages"),
                _ => new Uri(endpoint, "responses")
            };
        }
        if (Connection is ModelConnection.ResponsesEntra or ModelConnection.ResponsesApiKey)
        {
            if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment) ||
                endpoint.AbsolutePath.TrimEnd('/') != "/openai/v1" ||
                !(endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
                  endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Enter the HTTPS Foundry OpenAI v1 URL ending in /openai/v1.");
            if (string.IsNullOrWhiteSpace(Deployment))
                throw new ArgumentException("Enter your deployed model name.");
            if (Connection == ModelConnection.ResponsesApiKey && string.IsNullOrWhiteSpace(ApiKey))
                throw new ArgumentException("Enter the Foundry resource API key for Responses.");
            if (Connection == ModelConnection.ResponsesEntra &&
                !string.IsNullOrWhiteSpace(EntraTenantId) && !Guid.TryParse(EntraTenantId.Trim(), out _))
                throw new ArgumentException("Enter the resource tenant ID as a GUID, or leave it blank to use the current Azure sign-in.");
            return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/responses");
        }
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/" ||
            !(uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Enter an Azure OpenAI resource URL, e.g. https://your-resource.openai.azure.com (no deployment path).");
        }
        if (string.IsNullOrWhiteSpace(Deployment) || string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("An Azure OpenAI deployment name and API key are required.");
        return new Uri(uri,
            $"openai/deployments/{Uri.EscapeDataString(Deployment.Trim())}/chat/completions?api-version=2024-10-21");
    }
}
