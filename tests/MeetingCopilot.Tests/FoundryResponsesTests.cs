using Azure;
using Azure.Core;
using Azure.Identity;
using MeetingCopilot.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MeetingCopilot.Tests;

public class FoundryResponsesTests
{
    [Fact]
    public async Task ApprovedSlideIsSentAsOneImageWithoutConversationOrPersistentStorage()
    {
        byte[] png = SyntheticSlide.Png();
        const string slideJson = """{"title":"Archive","visibleContent":"Retention: 30 days.","interpretation":"Workers write to storage.","uncertainty":"Retry limit not visible."}""";
        int calls = 0;
        using var http = new HttpClient(new Handler(async (message, _) =>
        {
            calls++;
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Contains("untrusted", root.GetProperty("instructions").GetString());
            Assert.Equal("slide_context", root.GetProperty("text").GetProperty("format").GetProperty("name").GetString());
            var content = root.GetProperty("input")[0].GetProperty("content");
            var image = Assert.Single(content.EnumerateArray(), part => part.GetProperty("type").GetString() == "input_image");
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), image.GetProperty("image_url").GetString());
            Assert.DoesNotContain("Review payment design", root.ToString());
            return Response(outputText: slideJson);
        }));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        var slide = await model.ReadSlideAsync(png, default);
        Assert.Equal("Retention: 30 days.", slide.VisibleContent);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SlideWithoutReadableContentIsNotAddedAsSuccess()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(
            outputText: """{"title":"","visibleContent":"","interpretation":"","uncertainty":"Blank image."}"""))));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        await Assert.ThrowsAsync<InvalidDataException>(() => model.ReadSlideAsync(SyntheticSlide.Png(), default));
    }

    private static ModelSettings Settings => new(
        "https://example.services.ai.azure.com/openai/v1", "gpt-6-astra", "", ModelConnection.ResponsesEntra);
    private static AnalysisRequest Request => new(1, "Review payment design", "Retry flow proposed.",
        [new(1, DateTimeOffset.UtcNow, "Playback", "We retry a payment after a timeout.")], [], null);
    private const string Valid = """
        {"memory":"Retries discussed.","currentCueStatus":"none","suggestion":{
        "kind":"question","text":"What prevents duplicate charges?","reason":"A timed-out request may have completed.",
        "caveat":"May be addressed elsewhere.","evidenceIds":[1]}}
        """;

    [Theory]
    [InlineData("https://example.services.ai.azure.com/openai/v1")]
    [InlineData("https://example.services.ai.azure.com/openai/v1/")]
    [InlineData("https://example.openai.azure.com/openai/v1/")]
    public void AcceptsV1EndpointsWithoutApiKey(string endpoint)
    {
        Uri uri = (Settings with { Endpoint = endpoint }).GetRequestUri();
        Assert.Equal("/openai/v1/responses", uri.AbsolutePath);
        Assert.Equal("", uri.Query);
    }

    [Theory]
    [InlineData("http://example.services.ai.azure.com/openai/v1")]
    [InlineData("https://example.services.ai.azure.com/")]
    [InlineData("https://example.services.ai.azure.com/openai/v1/responses")]
    [InlineData("https://example.services.ai.azure.com/openai/v1?key=secret")]
    [InlineData("https://example.services.ai.azure.com:8443/openai/v1")]
    [InlineData("https://untrusted.example/openai/v1")]
    public void RejectsUnexpectedEntraTokenDestinations(string endpoint) =>
        Assert.Throws<ArgumentException>(() => (Settings with { Endpoint = endpoint }).GetRequestUri());

    [Fact]
    public async Task OfficialSdkSendsResponsesPayloadAndEntraScopeWithoutApiKey()
    {
        var credential = new FakeCredential();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("https://example.services.ai.azure.com/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-bearer-token", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("api-key"));
            string payload = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            Assert.Equal("gpt-6-astra", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal(2200, root.GetProperty("max_output_tokens").GetInt32());
            var format = root.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            var schema = format.GetProperty("schema");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Contains("noSuggestionReason", schema.GetProperty("required").ToString());
            var cueProperties = schema.GetProperty("properties").GetProperty("suggestion")
                .GetProperty("anyOf")[1].GetProperty("properties");
            Assert.Equal(220, cueProperties.GetProperty("text").GetProperty("maxLength").GetInt32());
            Assert.Equal("string", cueProperties.GetProperty("caveat").GetProperty("type").GetString());
            Assert.Contains("recommendation", cueProperties.GetProperty("kind").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("Do not default to clarification questions", root.GetProperty("instructions").GetString());
            Assert.Contains("untrusted", root.GetProperty("instructions").GetString());
            Assert.Contains("JSON", root.GetProperty("input").ToString());
            Assert.Contains("We retry a payment", root.GetProperty("input").ToString());
            Assert.Contains("Review payment design", root.GetProperty("input").ToString());
            Assert.DoesNotContain("synthetic-bearer-token", payload);
            return Response();
        }));
        var model = new FoundryResponsesModel(http, Settings, credential);
        var result = await model.AnalyzeAsync(Request, default);
        Assert.Equal("What prevents duplicate charges?", result.Suggestion!.Text);
        Assert.All(credential.Scopes, scope => Assert.Equal("https://ai.azure.com/.default", scope));
        Assert.NotEmpty(credential.Scopes);
    }

    [Fact]
    public async Task RecommendationsRoundTripThroughTheStructuredResponsesAdapter()
    {
        string content = Valid.Replace("\"kind\":\"question\"", "\"kind\":\"recommendation\"")
            .Replace("What prevents duplicate charges?", "Reuse a stable idempotency key across payment retries to avoid duplicate charges.");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(outputText: content))));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        var result = await model.AnalyzeAsync(Request with { QuestionRequested = true }, default);
        Assert.Equal("recommendation", result.Suggestion!.Kind);
        Assert.Contains("idempotency", result.Suggestion.Text);
        Assert.Equal([1L], result.Suggestion.EvidenceIds);
    }

    [Fact]
    public async Task ApiKeyModeUsesOfficialSdkWithoutRequestingAnEntraToken()
    {
        var credential = new FakeCredential { Fail = true };
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            calls++;
            Assert.Equal("/openai/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-resource-key", request.Headers.Authorization.Parameter);
            string payload = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic-resource-key", payload);
            using var json = JsonDocument.Parse(payload);
            Assert.Equal("gpt-6-astra", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("JSON", json.RootElement.GetProperty("input").ToString());
            if (calls == 1)
            {
                Assert.Contains("synthetic connectivity check", payload);
                Assert.DoesNotContain("Review payment design", payload);
            }
            else Assert.Contains("We retry a payment", payload);
            return Response();
        }));
        var model = new FoundryResponsesModel(http, Settings with
        {
            Connection = ModelConnection.ResponsesApiKey,
            ApiKey = "synthetic-resource-key",
            EntraTenantId = "unused-invalid-tenant"
        }, credential);
        await model.PrepareAsync(default);
        Assert.NotNull((await model.AnalyzeAsync(Request, default)).Suggestion);
        Assert.Equal(2, calls);
        Assert.Empty(credential.Scopes);
    }

    [Fact]
    public async Task EntraPreparationValidatesResourceAccessNotJustTokenAcquisition()
    {
        var credential = new FakeCredential();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TenantMismatch())));
        var error = await Assert.ThrowsAsync<ModelAuthenticationException>(() =>
            new FoundryResponsesModel(http, Settings, credential).PrepareAsync(default));
        Assert.NotEmpty(credential.Scopes);
        Assert.Contains("tenant", error.Message);
        Assert.DoesNotContain("synthetic-private-identity", error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResponsesApiKeyIsRequired(string key) =>
        Assert.Throws<ArgumentException>(() =>
            (Settings with { Connection = ModelConnection.ResponsesApiKey, ApiKey = key }).GetRequestUri());

    [Theory]
    [InlineData("https://untrusted.example/openai/v1")]
    [InlineData("http://example.services.ai.azure.com/openai/v1")]
    [InlineData("https://example.services.ai.azure.com/")]
    public void ApiKeyModeRejectsUnsafeOrIncorrectEndpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => (Settings with
        {
            Connection = ModelConnection.ResponsesApiKey,
            ApiKey = "synthetic-key",
            Endpoint = endpoint
        }).GetRequestUri());

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RejectedApiKeyHasKeySpecificGuidanceWithoutSignInOrSecrets(int status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"message":"synthetic-private-secret"}}""",
                    Encoding.UTF8, "application/json")
            })));
        var error = await Assert.ThrowsAsync<ModelAuthenticationException>(() =>
            new FoundryResponsesModel(http, Settings with
            {
                Connection = ModelConnection.ResponsesApiKey,
                ApiKey = "synthetic-private-secret"
            }).PrepareAsync(default));
        Assert.Contains("API key", error.Message);
        Assert.DoesNotContain("Sign in", error.Message);
        Assert.DoesNotContain("synthetic-private-secret", error.ToString());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public async Task RejectsNonCompletedResponses(string status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(status))));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        await Assert.ThrowsAsync<InvalidDataException>(() => model.AnalyzeAsync(Request, default));
    }

    [Fact]
    public async Task RefusalIsNotAcceptedAsAdvice()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(refusal: true))));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        await Assert.ThrowsAsync<InvalidDataException>(() => model.AnalyzeAsync(Request, default));
    }

    [Fact]
    public async Task AzureAuthenticationDetailsAreNotExposed()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("HTTP must not run.")));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential { Fail = true });
        var error = await Assert.ThrowsAsync<ModelAuthenticationException>(() => model.PrepareAsync(default));
        Assert.Contains("az login", error.Message);
        Assert.DoesNotContain("private identity detail", error.ToString());
        var analysisError = await Assert.ThrowsAsync<ModelAuthenticationException>(() => model.AnalyzeAsync(Request, default));
        Assert.DoesNotContain("private identity detail", analysisError.ToString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task HttpFailureIsSanitizedAndNotRetried(int status)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"message":"private meeting content","code":"test_error"}}""",
                    Encoding.UTF8, "application/json")
            });
        }));
        var error = await Record.ExceptionAsync(() =>
            new FoundryResponsesModel(http, Settings, new FakeCredential()).AnalyzeAsync(Request, default));
        if (status is 401 or 403) Assert.IsType<ModelAuthenticationException>(error);
        else Assert.IsType<HttpRequestException>(error);
        Assert.NotNull(error);
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain("private meeting", error.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationReachesTheSdkTransport()
    {
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response();
        }));
        using var cancel = new CancellationTokenSource();
        var task = new FoundryResponsesModel(http, Settings, new FakeCredential()).AnalyzeAsync(Request, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task TenantMismatchProvidesActionableErrorWithoutIdentityDetails()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TenantMismatch())));
        var model = new FoundryResponsesModel(http, Settings, new FakeCredential());
        var error = await Assert.ThrowsAsync<ModelAuthenticationException>(() => model.AnalyzeAsync(Request, default));
        Assert.Contains("az login --tenant", error.Message);
        Assert.DoesNotContain("synthetic-private-identity", error.ToString());
    }

    [Fact]
    public void InvalidTenantIdIsRejectedBeforeAuthentication() =>
        Assert.Throws<ArgumentException>(() => (Settings with { EntraTenantId = "not-a-tenant" }).GetRequestUri());

    private static HttpResponseMessage TenantMismatch() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""
            {"error":{"code":"Tenant provided in token does not match resource token",
            "message":"Token tenant synthetic-private-identity does not match resource tenant."}}
            """, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Response(string status = "completed", bool refusal = false, string? outputText = null)
    {
        object content = refusal
            ? new { type = "refusal", refusal = "Cannot provide this." }
            : new { type = "output_text", text = outputText ?? Valid, annotations = Array.Empty<object>() };
        string body = JsonSerializer.Serialize(new
        {
            id = "resp_test",
            @object = "response",
            created_at = 1700000000,
            status,
            model = "gpt-6-astra",
            output = new[] { new { type = "message", id = "msg_test", role = "assistant", status = "completed",
                content = new[] { content } } }
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeCredential : TokenCredential
    {
        public bool Fail { get; init; }
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.AddRange(requestContext.Scopes);
            if (Fail) throw new CredentialUnavailableException("private identity detail");
            return new("synthetic-bearer-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
