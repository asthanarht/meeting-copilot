using System.Net;
using System.Text;
using System.Text.Json;
using MeetingCopilot.Core;
using Xunit;

namespace MeetingCopilot.Tests;

public class ModelTests
{
    private static AnalysisRequest Request => new(1, "", "",
        [new(1, DateTimeOffset.UtcNow, "Playback", "We will retry payments.")], [], null);
    private const string Valid = """
        {"memory":"Retries proposed.","currentCueStatus":"none","suggestion":{
        "kind":"question","text":"How do we prevent duplicates?","reason":"Retries can repeat a payment.",
        "caveat":"This may already be handled.","evidenceIds":[1]}}
        """;

    [Fact]
    public void AcceptsGroundedResult() => Assert.NotNull(AzureMeetingModel.Parse(Valid, Request).Suggestion);

    [Theory]
    [InlineData("question")]
    [InlineData("gap")]
    [InlineData("alternative")]
    [InlineData("recommendation")]
    public void AllContributionKindsRequireRetainedEvidence(string kind)
    {
        string content = Valid.Replace("\"kind\":\"question\"", $"\"kind\":\"{kind}\"");
        Assert.Equal(kind, AzureMeetingModel.Parse(content, Request).Suggestion!.Kind);
        Assert.Throws<InvalidDataException>(() =>
            AzureMeetingModel.Parse(content.Replace("\"evidenceIds\":[1]", "\"evidenceIds\":[999]"), Request));
    }

    [Theory]
    [InlineData("\"evidenceIds\":[1]", "\"evidenceIds\":[999]")]
    [InlineData("\"kind\":\"question\"", "\"kind\":\"unsupported\"")]
    [InlineData("\"currentCueStatus\":\"none\"", "\"currentCueStatus\":\"keep\"")]
    [InlineData("\"evidenceIds\":[1]", "\"evidenceIds\":null")]
    public void RejectsMalformedOrInventedEvidence(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => AzureMeetingModel.Parse(Valid.Replace(before, after), Request));

    [Fact]
    public void RetainsTheModelsExplanationForChoosingSilence()
    {
        var result = AzureMeetingModel.Parse(
            """{"memory":"Greetings.","currentCueStatus":"none","suggestion":null,"noSuggestionReason":"No substantive topic yet."}""",
            Request);
        Assert.Equal("No substantive topic yet.", result.NoSuggestionReason);
    }

    [Fact]
    public void RejectsOversizedSilenceExplanation()
    {
        string content = JsonSerializer.Serialize(new
        {
            memory = "", currentCueStatus = "none", suggestion = (object?)null,
            noSuggestionReason = new string('x', 301)
        });
        Assert.Throws<InvalidDataException>(() => AzureMeetingModel.Parse(content, Request));
    }

    [Fact]
    public void NullSuggestionIsValid() =>
        Assert.Null(AzureMeetingModel.Parse(
            """{"memory":"","currentCueStatus":"none","suggestion":null}""", Request).Suggestion);

    [Theory]
    [InlineData("http://example.openai.azure.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.openai.azure.com/other")]
    [InlineData("https://user:secret@example.openai.azure.com")]
    [InlineData("https://example.openai.azure.com?key=secret")]
    public void RejectsUnexpectedCredentialDestinations(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new ModelSettings(endpoint, "deployment", "key").GetRequestUri());

    [Theory]
    [InlineData("question")]
    [InlineData("recommendation")]
    public async Task SendsFinalizedContextWithSeparateSystemInstruction(string kind)
    {
        string? payload = null;
        var handler = new Handler(async request =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            var envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = Valid.Replace("\"kind\":\"question\"", $"\"kind\":\"{kind}\"") } } }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var client = new AzureMeetingModel(http, new("https://test.openai.azure.com", "deployment", "test-key"));
        var result = await client.AnalyzeAsync(Request, default);
        Assert.NotNull(result.Suggestion);
        Assert.Equal(kind, result.Suggestion.Kind);
        Assert.Contains("We will retry payments.", payload!);
        Assert.DoesNotContain("test-key", payload!);
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal("system", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        string instructions = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("Do not default to clarification questions", instructions);
        Assert.Contains("legacy-named questionRequested", instructions);
        Assert.Contains("recommendation", instructions);
    }

    [Fact]
    public async Task ServiceErrorsDoNotEchoPotentiallySensitiveResponseBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("private meeting content") })));
        var client = new AzureMeetingModel(http, new("https://test.openai.azure.com", "deployment", "test-key"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.AnalyzeAsync(Request, default));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private meeting", error.Message);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"finish_reason":"length"}]}""")]
    [InlineData("""{"error":"unexpected"}""")]
    public async Task RejectsInvalidCompletionEnvelopes(string body)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var client = new AzureMeetingModel(http, new("https://test.openai.azure.com", "deployment", "test-key"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.AnalyzeAsync(Request, default));
    }

    [Fact]
    public async Task FinalTranscriptThroughHttpAdapterProducesCueThenRetractsWhenAnswered()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async message =>
        {
            string payload = await message.Content!.ReadAsStringAsync();
            string content;
            if (calls++ == 0)
            {
                Assert.Contains("retry payments", payload);
                content = Valid;
            }
            else
            {
                Assert.Contains("Idempotency keys", payload);
                Assert.Contains("Retries proposed.", payload);
                content = """{"memory":"Duplicates handled with idempotency keys.","currentCueStatus":"addressed","suggestion":null}""";
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content } } }
                }))
            };
        }));
        var engine = new MeetingEngine(new AzureMeetingModel(http,
            new("https://test.openai.azure.com", "deployment", "test-key")), "");
        AnalysisUpdate? latest = null;
        engine.Updated += u => latest = u;
        engine.Add(Request.Utterances[0]);
        await engine.AnalyzeOnceAsync(default);
        Assert.NotNull(latest!.Cue);
        engine.Add(new(2, DateTimeOffset.UtcNow, "Playback", "Idempotency keys prevent duplicates."));
        await engine.AnalyzeOnceAsync(default);
        Assert.Null(latest.Cue);
        Assert.Contains("Addressed", latest.Status);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
