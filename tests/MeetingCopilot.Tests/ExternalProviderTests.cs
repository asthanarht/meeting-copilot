using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public class ExternalProviderTests
{
    private static readonly ModelConnection[] Providers =
        [ModelConnection.OpenAICompatible, ModelConnection.Gemini, ModelConnection.Anthropic, ModelConnection.Grok];
    public static IEnumerable<object[]> ProviderCases => Providers.Select(p => new object[] { p });
    public static IEnumerable<object[]> ErrorCases => Providers.SelectMany(p => new[] { 301, 307, 400, 401, 403, 429, 500 }
        .Select(code => new object[] { p, code }));
    public static IEnumerable<object[]> BadBodies => Providers.SelectMany(p =>
        new[] { "{}", "null", "[]", "invalid-json", new string('x', 128_001), Envelope(p, Valid, incomplete: true),
            Envelope(p, Valid.Replace("[1]", "[999]")) }.Select(body => new object[] { p, body }));
    private static AnalysisRequest Request => new(1, "PRIVATE-MEETING-BRIEF", "",
        [new(1, DateTimeOffset.UtcNow, "Playback", "We will retry payments.")], [], null);
    private const string Valid = """
        {"memory":"Retries proposed.","currentCueStatus":"none","suggestion":{
        "kind":"recommendation","text":"Reuse the original idempotency key when retrying.","reason":"Retries can repeat a payment.",
        "caveat":"Assumes the gateway supports idempotency.","evidenceIds":[1]},"noSuggestionReason":""}
        """;

    private static ModelSettings Settings(ModelConnection provider, CompatibleModelOptions? options = null) =>
        new(ExternalModelProfile.Default(provider).Endpoint, "synthetic-model", "synthetic-provider-key", provider, Compatible: options);

    private static string Envelope(ModelConnection provider, string content, bool incomplete = false) => provider switch
    {
        ModelConnection.Gemini => JsonSerializer.Serialize(new
        {
            candidates = new[] { new { finishReason = incomplete ? "MAX_TOKENS" : "STOP",
                content = new { parts = new[] { new { text = content } } } } }
        }),
        ModelConnection.Anthropic => JsonSerializer.Serialize(new
        {
            stop_reason = incomplete ? "max_tokens" : "tool_use",
            content = new[] { new { type = "tool_use", name = "meeting_analysis", input = JsonSerializer.Deserialize<JsonElement>(content) } }
        }),
        ModelConnection.Grok => JsonSerializer.Serialize(new
        {
            status = incomplete ? "incomplete" : "completed",
            output = new[] { new { type = "message", status = "completed", content = new[] { new { type = "output_text", text = content } } } }
        }),
        _ => JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = incomplete ? "length" : "stop", message = new { content } } }
        })
    };

    private static HttpResponseMessage Response(string body, int code = 200) =>
        new((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory, MemberData(nameof(ProviderCases))]
    public async Task NativeProtocolsUseOnlyTheirOwnCredentialsAndSameGroundedContract(ModelConnection provider)
    {
        using var http = new HttpClient(new Handler(async (message, cancellationToken) =>
        {
            var settings = Settings(provider);
            Assert.Equal(settings.GetRequestUri(), message.RequestUri);
            Assert.False(message.Headers.Contains("api-key"));
            var body = await message.Content!.ReadAsStringAsync();
            Assert.DoesNotContain(settings.ApiKey, body);
            Assert.DoesNotContain("AZURE", body);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            string system;
            switch (provider)
            {
                case ModelConnection.Gemini:
                    Assert.Equal("/v1beta/models/synthetic-model:generateContent", message.RequestUri!.AbsolutePath);
                    Assert.Equal(settings.ApiKey, message.Headers.GetValues("x-goog-api-key").Single());
                    Assert.Null(message.Headers.Authorization);
                    system = root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
                    Assert.Equal("user", root.GetProperty("contents")[0].GetProperty("role").GetString());
                    Assert.Equal("application/json", root.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
                    Assert.Equal("object", root.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetProperty("type").GetString());
                    Assert.Equal(4096, root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
                    break;
                case ModelConnection.Anthropic:
                    Assert.Equal("/v1/messages", message.RequestUri!.AbsolutePath);
                    Assert.Equal(settings.ApiKey, message.Headers.GetValues("x-api-key").Single());
                    Assert.Equal("2023-06-01", message.Headers.GetValues("anthropic-version").Single());
                    Assert.Null(message.Headers.Authorization);
                    system = root.GetProperty("system").GetString()!;
                    Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
                    Assert.Equal("meeting_analysis", root.GetProperty("tool_choice").GetProperty("name").GetString());
                    Assert.True(root.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean());
                    Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
                    break;
                default:
                    Assert.Equal($"Bearer {settings.ApiKey}", message.Headers.Authorization!.ToString());
                    Assert.False(message.Headers.Contains("x-api-key"));
                    Assert.False(message.Headers.Contains("x-goog-api-key"));
                    var messages = root.GetProperty(provider == ModelConnection.Grok ? "input" : "messages");
                    system = messages[0].GetProperty("content").GetString()!;
                    Assert.Equal("system", messages[0].GetProperty("role").GetString());
                    Assert.Equal("user", messages[1].GetProperty("role").GetString());
                    if (provider == ModelConnection.Grok)
                    {
                        Assert.Equal("/v1/responses", message.RequestUri!.AbsolutePath);
                        Assert.False(root.GetProperty("store").GetBoolean());
                        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
                        Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
                    }
                    else
                    {
                        Assert.Equal("/v1/chat/completions", message.RequestUri!.AbsolutePath);
                        Assert.Equal(4096, root.GetProperty("max_completion_tokens").GetInt32());
                        Assert.False(root.TryGetProperty("max_tokens", out _));
                    }
                    break;
            }
            Assert.Contains("Do not default to clarification questions", system);
            Assert.DoesNotContain(Request.MeetingBrief, system);
            Assert.Contains(Request.MeetingBrief, body);
            return Response(Envelope(provider, Valid));
        }));
        var model = MeetingModelFactory.Create(http, Settings(provider, new(TimeoutSeconds: 90, ContextCharacters: 48_000)));
        Assert.Equal(TimeSpan.FromSeconds(90), model.RequestTimeout);
        Assert.Equal(48_000, model.ContextCharacterBudget);
        Assert.False(model is ISlideModel);
        var result = await model.AnalyzeAsync(Request, default);
        Assert.Equal("recommendation", result.Suggestion!.Kind);
        Assert.Equal(1, Assert.Single(result.Suggestion.EvidenceIds));
    }

    [Theory, MemberData(nameof(ErrorCases))]
    public async Task FailedPreflightNeverOpensAudioOrLeaksPrivateData(ModelConnection provider, int status)
    {
        int devices = 0, calls = 0;
        using var http = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            calls++;
            var payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("Synthetic connection test", payload);
            Assert.DoesNotContain(Request.MeetingBrief, payload);
            return Response("PRIVATE-PROVIDER-ERROR", status);
        }));
        var modelSettings = Settings(provider);
        await using var session = new MeetingSession(new("speech-key", "eastus", "en-US", "playback", null, [],
            Request.MeetingBrief, modelSettings), (_, _) =>
            { devices++; throw new InvalidOperationException("Audio must never open."); },
            MeetingModelFactory.Create(http, modelSettings));
        var error = await Record.ExceptionAsync(session.StartAsync);
        Assert.NotNull(error);
        Assert.DoesNotContain("PRIVATE", error.Message);
        if (status is 401 or 403) Assert.IsType<ModelAuthenticationException>(error);
        else Assert.Equal((HttpStatusCode)status, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal(0, devices);
        Assert.False(session.IsRunning);
    }

    [Theory, MemberData(nameof(BadBodies))]
    public async Task RejectsMalformedIncompleteOversizedOrUngroundedOutput(ModelConnection provider, string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body))));
        var error = await Record.ExceptionAsync(() => MeetingModelFactory.Create(http, Settings(provider)).AnalyzeAsync(Request, default));
        Assert.True(error is InvalidDataException or JsonException, error?.GetType().Name ?? "No error");
    }

    [Theory, MemberData(nameof(ProviderCases))]
    public async Task LiveSuggestionIsRetractedWhenDiscussionAddressesIt(ModelConnection provider)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async (message, _) =>
        {
            var payload = await message.Content!.ReadAsStringAsync();
            var content = Valid;
            if (calls++ > 0)
            {
                Assert.Contains("Idempotency keys", payload);
                Assert.Contains("Retries proposed.", payload);
                content = """{"memory":"Duplicate protection agreed.","currentCueStatus":"addressed","suggestion":null}""";
            }
            return Response(Envelope(provider, content));
        }));
        var engine = new MeetingEngine(MeetingModelFactory.Create(http, Settings(provider)), Request.MeetingBrief);
        AnalysisUpdate? update = null;
        engine.Updated += u => update = u;
        engine.Add(Request.Utterances[0]);
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal("recommendation", update!.Cue!.Kind);
        engine.Add(new(2, DateTimeOffset.UtcNow, "Playback", "Idempotency keys are now agreed."));
        await engine.AnalyzeOnceAsync(default);
        Assert.Null(update.Cue);
        Assert.Contains("Addressed", update.Status);
    }

    [Theory]
    [InlineData(CompatibleOutputFormat.JsonObject, CompatibleTokenParameter.MaxCompletionTokens)]
    [InlineData(CompatibleOutputFormat.JsonSchema, CompatibleTokenParameter.MaxTokens)]
    [InlineData(CompatibleOutputFormat.PromptOnly, CompatibleTokenParameter.MaxTokens)]
    public async Task LocalCompatibilityControlsChangeWireParameters(CompatibleOutputFormat format, CompatibleTokenParameter tokens)
    {
        using var http = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal(format != CompatibleOutputFormat.PromptOnly, root.TryGetProperty("response_format", out var output));
            if (format == CompatibleOutputFormat.JsonSchema) Assert.True(output.GetProperty("json_schema").GetProperty("strict").GetBoolean());
            Assert.Equal(tokens == CompatibleTokenParameter.MaxTokens, root.TryGetProperty("max_tokens", out _));
            Assert.Equal(tokens == CompatibleTokenParameter.MaxCompletionTokens, root.TryGetProperty("max_completion_tokens", out _));
            return Response(Envelope(ModelConnection.OpenAICompatible, Valid));
        }));
        var settings = new ModelSettings("http://localhost:11434/v1", "local-model", "", ModelConnection.OpenAICompatible,
            Compatible: new(CompatibleAuthentication.None, format, tokens));
        await MeetingModelFactory.Create(http, settings).AnalyzeAsync(Request, default);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:1234/v1", true)]
    [InlineData("http://localhost.attacker.example/v1", false)]
    [InlineData("http://192.168.1.2/v1", false)]
    [InlineData("https://api.example/v1?key=secret", false)]
    [InlineData("https://api.example/v1#secret", false)]
    [InlineData("https://user:secret@api.example/v1", false)]
    [InlineData("https://api.example/v1/chat/completions", false)]
    [InlineData("https://api.example/v1/responses", false)]
    public void ValidatesBaseUrls(string endpoint, bool valid)
    {
        var options = new CompatibleModelOptions();
        if (valid) Assert.EndsWith("/chat/completions", options.GetRequestUri(endpoint, "model", "key").AbsolutePath);
        else Assert.Throws<ArgumentException>(() => options.GetRequestUri(endpoint, "model", "key"));
    }

    [Fact]
    public void RejectsInvalidLimitsCredentialsAndNativeModelPaths()
    {
        Assert.Throws<ArgumentException>(() => new CompatibleModelOptions(ContextCharacters: 8000).Validate());
        Assert.Throws<ArgumentException>(() => new CompatibleModelOptions(TimeoutSeconds: 0).Validate());
        Assert.Throws<ArgumentException>(() => new CompatibleModelOptions(MaxOutputTokens: 0).Validate());
        Assert.Throws<ArgumentException>(() => new CompatibleModelOptions(Authentication: CompatibleAuthentication.None)
            .GetRequestUri("https://api.example/v1", "model", ""));
        Assert.Throws<ArgumentException>(() => new CompatibleModelOptions().GetRequestUri("https://api.example/v1", "model", "key\r\nheader"));
        Assert.Throws<ArgumentException>(() => (Settings(ModelConnection.Gemini) with { Deployment = "models/wrong" }).GetRequestUri());
        Assert.Throws<ArgumentException>(() => (Settings(ModelConnection.Anthropic) with { Endpoint = "https://api.anthropic.com/v1/messages" }).GetRequestUri());
        foreach (var provider in Providers.Where(p => p != ModelConnection.OpenAICompatible))
        {
            Assert.Throws<ArgumentException>(() => Settings(provider, new(Authentication: CompatibleAuthentication.None)).GetRequestUri());
            Assert.Throws<ArgumentException>(() => (Settings(provider) with { Endpoint = "http://localhost/v1" }).GetRequestUri());
        }
    }

    [Theory, MemberData(nameof(ProviderCases))]
    public async Task CancellationReachesProviderTransport(ModelConnection provider)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Must cancel.");
        }));
        var analysis = MeetingModelFactory.Create(http, Settings(provider)).PrepareAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
    }

    [Theory, MemberData(nameof(ProviderCases))]
    public async Task ChunkedResponsesAreBoundedWithoutContentLength(ModelConnection provider)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(new byte[128_001])
        })));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MeetingModelFactory.Create(http, Settings(provider)).AnalyzeAsync(Request, default));
        Assert.Contains("permitted size", error.Message);
    }

    [Theory, MemberData(nameof(ProviderCases))]
    public async Task ValidButEmptyMemoryFailsTheSyntheticConnectionTest(ModelConnection provider)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Envelope(provider,
            """{"memory":"","currentCueStatus":"none","suggestion":null}""")))));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => MeetingModelFactory.Create(http, Settings(provider)).PrepareAsync(default));
        Assert.Contains("empty memory", error.Message);
    }

    [Fact]
    public async Task ProductionHttpClientDoesNotForwardCredentialsAcrossRedirects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = socket.GetStream();
            var buffer = new byte[4096];
            using var received = new MemoryStream();
            while (true)
            {
                int count = await stream.ReadAsync(buffer, deadline.Token);
                if (count == 0) throw new IOException("Client closed before completing its request.");
                received.Write(buffer, 0, count);
                Assert.True(received.Length < 128_000);
                string request = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
                int headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0) continue;
                string headers = request[..headerEnd];
                if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.EndsWith("\r\n0\r\n\r\n", StringComparison.Ordinal)) break;
                }
                else
                {
                    string lengthHeader = headers.Split("\r\n").Single(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    int length = int.Parse(lengthHeader.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (received.Length >= headerEnd + 4 + length) break;
                }
            }
            var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply, deadline.Token);
        });
        using var http = MeetingModelFactory.CreateHttpClient();
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        var model = MeetingModelFactory.Create(http, new($"http://127.0.0.1:{port}/v1", "model", "synthetic-key", ModelConnection.OpenAICompatible));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => model.AnalyzeAsync(Request, deadline.Token));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
        await serve;
        Assert.False(listener.Pending());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
