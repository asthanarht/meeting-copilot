using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetingCopilot.Core;

public sealed class CompatibleMeetingModel : IMeetingModel
{
    private readonly HttpClient http;
    private readonly ModelSettings settings;
    private readonly CompatibleModelOptions options;
    private readonly Uri requestUri;
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(options.TimeoutSeconds);
    public int ContextCharacterBudget => options.ContextCharacters;

    public CompatibleMeetingModel(HttpClient http, ModelSettings settings)
    {
        if (settings.Connection != ModelConnection.OpenAICompatible)
            throw new ArgumentException("Select the OpenAI-compatible connection.");
        this.http = http;
        this.settings = settings;
        options = settings.Compatible ?? new();
        requestUri = settings.GetRequestUri();
    }

    public Task PrepareAsync(CancellationToken cancellationToken) =>
        MeetingModelFactory.TestAsync(this, cancellationToken);

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = settings.Deployment.Trim(),
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new { role = "system", content = AzureMeetingModel.Instructions },
                new { role = "user", content = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }
            },
            [options.TokenParameter == CompatibleTokenParameter.MaxTokens ? "max_tokens" : "max_completion_tokens"] = options.MaxOutputTokens
        };
        if (options.OutputFormat == CompatibleOutputFormat.JsonObject)
            payload["response_format"] = new { type = "json_object" };
        else if (options.OutputFormat == CompatibleOutputFormat.JsonSchema)
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "meeting_analysis", strict = true,
                    schema = JsonSerializer.Deserialize<JsonElement>(AzureMeetingModel.OutputSchema) }
            };

        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri);
        if (options.Authentication == CompatibleAuthentication.ApiKey)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        message.Content = JsonContent.Create(payload);
        using var response = await CompletionResponse.SendAsync(http, message, cancellationToken);
        return AzureMeetingModel.Parse(CompletionResponse.ReadChatText(response.RootElement), request);
    }
}
