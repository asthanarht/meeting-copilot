using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetingCopilot.Core;

public sealed class NativeMeetingModel : IMeetingModel
{
    private readonly HttpClient http;
    private readonly ModelSettings settings;
    private readonly CompatibleModelOptions options;
    private readonly Uri requestUri;
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(options.TimeoutSeconds);
    public int ContextCharacterBudget => options.ContextCharacters;

    public NativeMeetingModel(HttpClient http, ModelSettings settings)
    {
        if (settings.Connection is not (ModelConnection.Gemini or ModelConnection.Anthropic or ModelConnection.Grok))
            throw new ArgumentException("Select Gemini, Anthropic or Grok.");
        this.http = http;
        this.settings = settings;
        options = settings.Compatible ?? new();
        requestUri = settings.GetRequestUri();
    }

    public Task PrepareAsync(CancellationToken cancellationToken) => MeetingModelFactory.TestAsync(this, cancellationToken);

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        string context = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schema = JsonSerializer.Deserialize<JsonElement>(AzureMeetingModel.OutputSchema);
        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri);
        object payload;
        switch (settings.Connection)
        {
            case ModelConnection.Gemini:
                message.Headers.Add("x-goog-api-key", settings.ApiKey.Trim());
                payload = new
                {
                    systemInstruction = new { parts = new[] { new { text = AzureMeetingModel.Instructions } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = context } } } },
                    generationConfig = new { maxOutputTokens = options.MaxOutputTokens, responseMimeType = "application/json",
                        responseJsonSchema = schema }
                };
                break;
            case ModelConnection.Anthropic:
                message.Headers.Add("x-api-key", settings.ApiKey.Trim());
                message.Headers.Add("anthropic-version", "2023-06-01");
                payload = new
                {
                    model = settings.Deployment.Trim(), max_tokens = options.MaxOutputTokens,
                    system = AzureMeetingModel.Instructions + "\nReturn the result through the meeting_analysis output tool.",
                    messages = new[] { new { role = "user", content = context } },
                    tools = new[] { new { name = "meeting_analysis",
                        description = "Return the structured meeting analysis. This is an output format only; it executes no action.",
                        input_schema = schema } },
                    tool_choice = new { type = "tool", name = "meeting_analysis", disable_parallel_tool_use = true }
                };
                break;
            default:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
                payload = new
                {
                    model = settings.Deployment.Trim(), store = false, max_output_tokens = options.MaxOutputTokens,
                    input = new[] { new { role = "system", content = AzureMeetingModel.Instructions },
                        new { role = "user", content = context } },
                    text = new { format = new { type = "json_schema", name = "meeting_analysis", strict = true, schema } }
                };
                break;
        }
        message.Content = JsonContent.Create(payload);
        using var response = await CompletionResponse.SendAsync(http, message, cancellationToken);
        string text;
        try { text = ReadText(response.RootElement); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The provider response did not match the selected API. No suggestion was accepted.");
        }
        return AzureMeetingModel.Parse(text, request);
    }

    private string ReadText(JsonElement root)
    {
        if (settings.Connection == ModelConnection.Gemini)
        {
            var candidates = root.GetProperty("candidates");
            if (candidates.GetArrayLength() != 1 || candidates[0].GetProperty("finishReason").GetString() != "STOP")
                throw new InvalidDataException("Gemini returned a blocked or incomplete response. No suggestion was accepted.");
            var parts = candidates[0].GetProperty("content").GetProperty("parts").EnumerateArray();
            return string.Concat(parts.Where(p => !p.TryGetProperty("thought", out var thought) || !thought.GetBoolean())
                .Select(p => p.GetProperty("text").GetString()));
        }
        if (settings.Connection == ModelConnection.Anthropic)
        {
            if (root.GetProperty("stop_reason").GetString() != "tool_use")
                throw new InvalidDataException("Claude did not complete the structured output. Check model tool support and output-token budget.");
            var results = root.GetProperty("content").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == "tool_use").ToArray();
            if (results.Length != 1 || results[0].GetProperty("name").GetString() != "meeting_analysis" ||
                results[0].GetProperty("input").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Claude returned an unexpected structured output. No tool was executed.");
            return results[0].GetProperty("input").GetRawText();
        }
        if (root.GetProperty("status").GetString() != "completed")
            throw new InvalidDataException("Grok returned an incomplete response. Check model support and output-token budget.");
        var texts = new List<string>();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "reasoning") continue;
            if (item.GetProperty("type").GetString() != "message")
                throw new InvalidDataException("Grok returned an unexpected output type.");
            if (item.TryGetProperty("status", out var status) && status.GetString() != "completed")
                throw new InvalidDataException("Grok returned an incomplete output message.");
            foreach (var part in item.GetProperty("content").EnumerateArray())
            {
                if (part.GetProperty("type").GetString() != "output_text")
                    throw new InvalidDataException("Grok refused or did not return a text response.");
                texts.Add(part.GetProperty("text").GetString()!);
            }
        }
        return string.Concat(texts);
    }
}
