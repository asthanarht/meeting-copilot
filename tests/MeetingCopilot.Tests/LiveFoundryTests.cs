using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace MeetingCopilot.Tests;

public class LiveFoundryTests(ITestOutputHelper output)
{
    [SavedRecommendationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedConfigurationProposesAnActionInsteadOfOnlyClarifying(bool requested)
    {
        var saved = new SettingsStore().Load() ?? throw new InvalidOperationException("No saved configuration.");
        using var http = new HttpClient(new DiagnosticHandler(output, saved.ModelKey, saved.SpeechKey))
        { Timeout = TimeSpan.FromSeconds(15) };
        var model = new FoundryResponsesModel(http,
            new(saved.ModelEndpoint, saved.Deployment, saved.ModelKey, saved.ModelConnection, saved.TenantId));
        var prior = new Cue("question", "What retry delay should we use?", "Retry delay was undecided.", "", [1]);
        var request = new AnalysisRequest(2, "", "Payment retries discussed; delay was undecided.",
            [
                new(1, DateTimeOffset.UtcNow, "Synthetic speaker", "Our checkout retries a payment after a network timeout. We have not picked the retry delay."),
                new(2, DateTimeOffset.UtcNow, "Synthetic speaker", "We have now agreed on one retry after a second. Testing shows the first charge sometimes succeeds despite the timeout, then the retry charges again."),
                new(3, DateTimeOffset.UtcNow, "Synthetic speaker", "Our existing gateway supports an idempotency key and returns the same stored result for the same key. We currently generate a new key on each attempt. Preventing duplicate charges is the remaining decision; replacing the gateway is out of scope.")
            ], [], prior, QuestionRequested: requested);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var result = await model.AnalyzeAsync(request, deadline.Token);
        Assert.Equal("addressed", result.CurrentCueStatus);
        var cue = Assert.IsType<Cue>(result.Suggestion);
        Assert.Equal("recommendation", cue.Kind);
        Assert.Contains("idempotency", cue.Text + " " + cue.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(cue.Text.TrimEnd().EndsWith('?'), "An actionable recommendation must not be only another question.");
        Assert.Contains(3L, cue.EvidenceIds);
        Assert.DoesNotContain("replace the gateway", cue.Text, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"{(requested ? "Suggest now" : "Automatic")}: {cue.Text}");
        output.WriteLine($"Reason: {cue.Reason}; caveat: {cue.Caveat}; evidence: {string.Join(", ", cue.EvidenceIds)}");

        using var followUpDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var resolved = await model.AnalyzeAsync(request with
        {
            Revision = 3, Memory = result.Memory, CurrentCue = cue, QuestionRequested = false,
            Utterances = [.. request.Utterances, new(4, DateTimeOffset.UtcNow, "Synthetic speaker",
                "Agreed and implemented: one stable idempotency key per payment is reused across attempts; testing confirms no duplicate charges. This resolves the recommendation. We are ending the meeting, with no other topics.")]
        }, followUpDeadline.Token);
        Assert.Equal("addressed", resolved.CurrentCueStatus);
        Assert.Null(resolved.Suggestion);
    }

    private sealed class SavedRecommendationTheoryAttribute : TheoryAttribute
    {
        public SavedRecommendationTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_RECOMMENDATION_TEST") != "1")
                Skip = "Opt-in only: uses saved model credentials with invented meeting text; no audio or real meeting context.";
        }
    }

    [SavedSlideFact]
    public async Task SavedDeploymentReadsASyntheticSlideAndGroundsAQuestionInItsContext()
    {
        var saved = new SettingsStore().Load() ?? throw new InvalidOperationException("No saved configuration.");
        using var http = new HttpClient(new DiagnosticHandler(output, saved.ModelKey, saved.SpeechKey))
        { Timeout = TimeSpan.FromSeconds(15) };
        var model = new FoundryResponsesModel(http,
            new(saved.ModelEndpoint, saved.Deployment, saved.ModelKey, saved.ModelConnection, saved.TenantId));
        byte[] png = SyntheticSlide.Png();
        SlideAnalysis slide;
        var watch = Stopwatch.StartNew();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            slide = await model.ReadSlideAsync(png, deadline.Token);
        Assert.Contains("30", slide.VisibleContent);
        Assert.Contains("storage", slide.VisibleContent, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"Synthetic slide read in {watch.Elapsed.TotalSeconds:F2}s; title: {slide.Title}");
        var engine = new MeetingEngine(model, "");
        engine.Add(new(1, DateTimeOffset.UtcNow, "Synthetic slide", slide.ToContext(), "slide"));
        engine.Add(new(2, DateTimeOffset.UtcNow, "Synthetic speaker", "This diagram describes the archive pipeline we are presenting."));
        engine.RequestNow();
        AnalysisUpdate? delivered = null;
        engine.Updated += value => delivered = value;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await engine.AnalyzeOnceAsync(deadline.Token);
        Assert.NotNull(delivered?.Cue);
        Assert.Contains(1, delivered.Cue.EvidenceIds);
        Assert.Contains(delivered.Evidence!, item => item.Kind == "slide");
        output.WriteLine($"Slide-grounded question: {delivered.Cue.Text}");
        output.WriteLine($"Reason: {delivered.Cue.Reason}");
    }

    private sealed class SavedSlideFactAttribute : FactAttribute
    {
        public SavedSlideFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_SLIDE_TEST") != "1")
                Skip = "Opt-in only: uses saved credentials with a locally rendered synthetic image; never captures the desktop.";
        }
    }

    [SavedSettingsFoundryFact]
    public async Task SavedConfigurationAutomaticallySuggestsDuringASyntheticPresentationAndExplainsSilence()
    {
        var saved = new SettingsStore().Load() ?? throw new InvalidOperationException("No saved configuration.");
        using var http = new HttpClient(new DiagnosticHandler(output, saved.ModelKey, saved.SpeechKey))
        { Timeout = TimeSpan.FromSeconds(15) };
        var settings = new ModelSettings(saved.ModelEndpoint, saved.Deployment, saved.ModelKey,
            saved.ModelConnection, saved.TenantId);
        var model = new FoundryResponsesModel(http, settings);
        var input = new SyntheticInput();
        var sessionSettings = new SessionSettings("unused", "", "en-US", "synthetic", null, [], "", settings);
        var analyzing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<AnalysisUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        await using (var session = new MeetingSession(sessionSettings, (_, _) => input, model))
        {
            session.AnalysisStatusChanged += status =>
            {
                if (status.StartsWith("Analyzing", StringComparison.Ordinal)) analyzing.TrySetResult();
            };
            session.Updated += update =>
            {
                if (update.Cue is not null) delivered.TrySetResult(update);
                else if (update.Latency is not null || update.Error is not null)
                    delivered.TrySetException(new InvalidOperationException("No automatic question: " + update.Status + " " + update.Error));
            };
            await session.StartAsync();
            watch.Restart();
            input.Publish("This demonstration processes satellite imagery. The service splits each image into tiles and a worker pool computes vegetation indices.");
            input.Publish("A coordinator assembles tile outputs into a mosaic and writes a manifest for downstream processing.");
            await analyzing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Publish("Analysts can launch a run from the desktop app or a scheduled batch. Each run has a worker limit and a fixed compute budget.");
            var update = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains(update.Cue!.Kind, new[] { "question", "gap", "alternative", "recommendation" });
            Assert.Contains("Provisional", update.Status);
            Assert.NotEmpty(update.Evidence!);
            Assert.All(update.Cue.EvidenceIds, id => Assert.Contains(session.GetContextSnapshot().Utterances, u => u.Id == id));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Automatic delivery exceeded the synthetic live budget.");
            output.WriteLine($"Automatic presentation question through the running session in {watch.Elapsed.TotalSeconds:F2}s: {update.Cue.Text}");
            output.WriteLine($"Grounding: {update.Cue.Reason}");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var silence = await model.AnalyzeAsync(new(1, "", "",
            [new(1, DateTimeOffset.UtcNow, "Synthetic speaker", "Hello everyone. Good morning. Can you hear me? Yes. Thanks.")],
            [], null, QuestionRequested: true), deadline.Token);
        Assert.Null(silence.Suggestion);
        Assert.False(string.IsNullOrWhiteSpace(silence.NoSuggestionReason));
        output.WriteLine("Chatter-only outcome explanation: " + silence.NoSuggestionReason);
    }

    private sealed class SyntheticInput : ISpeechAudioInput
    {
        public string Source => "Synthetic presentation";
        public DateTimeOffset? LastPacket => null;
        public event Action<string, string, bool>? Transcript;
        public event Action<string>? Faulted { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void StopCapture() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(string text) => Transcript?.Invoke(Source, text, true);
    }

    [SavedSettingsFoundryFact]
    public async Task SavedConfigurationAsksGroundedClarificationWithoutAMeetingBrief()
    {
        var saved = new SettingsStore().Load() ?? throw new InvalidOperationException("No saved configuration.");
        using var http = new HttpClient(new DiagnosticHandler(output, saved.ModelKey, saved.SpeechKey))
        { Timeout = TimeSpan.FromSeconds(15) };
        var model = new FoundryResponsesModel(http,
            new(saved.ModelEndpoint, saved.Deployment, saved.ModelKey, saved.ModelConnection, saved.TenantId));
        var request = new AnalysisRequest(1, "", "",
            [
                new(1, DateTimeOffset.UtcNow, "Synthetic speaker", "We need to reduce this number before the next release."),
                new(2, DateTimeOffset.UtcNow, "Synthetic speaker", "Another team may handle the change. We need a reduction plan."),
                new(3, DateTimeOffset.UtcNow, "Synthetic speaker", "Let's choose the approach before allocating two weeks of work.")
            ], [], null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var watch = Stopwatch.StartNew();
        var result = await model.AnalyzeAsync(request, deadline.Token);
        Assert.NotNull(result.Suggestion);
        Assert.Equal("question", result.Suggestion.Kind);
        Assert.All(result.Suggestion.EvidenceIds, id => Assert.Contains(request.Utterances, u => u.Id == id));
        output.WriteLine($"Grounded clarification without a brief in {watch.Elapsed.TotalSeconds:F2}s: {result.Suggestion.Text}");
        output.WriteLine($"Evidence IDs: {string.Join(", ", result.Suggestion.EvidenceIds)}");
    }

    [SavedSettingsFoundryFact]
    public async Task SavedConfigurationSupportsPreflightAndLiveSyntheticQuestions()
    {
        var saved = new SettingsStore().Load()
            ?? throw new InvalidOperationException("No saved application configuration exists.");
        var settings = new ModelSettings(saved.ModelEndpoint, saved.Deployment, saved.ModelKey,
            saved.ModelConnection, saved.TenantId);
        Assert.True(settings.Connection is ModelConnection.ResponsesApiKey or ModelConnection.ResponsesEntra);
        using var http = new HttpClient(new DiagnosticHandler(output, saved.ModelKey, saved.SpeechKey))
        { Timeout = TimeSpan.FromSeconds(15) };
        var model = new FoundryResponsesModel(http, settings);
        using (var preparation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await model.PrepareAsync(preparation.Token);
        output.WriteLine("Saved configuration: resource preflight succeeded without opening audio devices.");

        var request = new AnalysisRequest(1, "Synthetic payment design review; no real meeting data.", "",
            [new(1, DateTimeOffset.UtcNow, "Synthetic participant",
                "We retry a payment whenever the gateway times out. We have not decided how to prevent duplicate charges.")],
            [], null);
        AnalysisResult result;
        var watch = Stopwatch.StartNew();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            result = await model.AnalyzeAsync(request, deadline.Token);
        output.WriteLine($"Live synthetic question completed in {watch.Elapsed.TotalSeconds:F2}s.");
        Assert.NotNull(result.Suggestion);
        output.WriteLine($"Synthetic question: {result.Suggestion.Text}");
        Assert.All(result.Suggestion.EvidenceIds, id => Assert.Contains(request.Utterances, u => u.Id == id));

        var followUp = request with
        {
            Revision = 2,
            Memory = result.Memory,
            CurrentCue = result.Suggestion,
            Utterances = [.. request.Utterances, new(2, DateTimeOffset.UtcNow, "Synthetic participant",
                "The gateway uses the same idempotency key for every retry and atomically returns the original payment. Duplicate charges are prevented. That resolves the question.")]
        };
        watch.Restart();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            result = await model.AnalyzeAsync(followUp, deadline.Token);
        output.WriteLine($"Follow-up completed in {watch.Elapsed.TotalSeconds:F2}s; prior cue status: {result.CurrentCueStatus}.");
        Assert.NotEqual("keep", result.CurrentCueStatus);
    }

    [LiveFoundryFact]
    public async Task ConfiguredDeploymentReturnsValidSyntheticMeetingAnalysis()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
        string key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "";
        var settings = new ModelSettings(
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT for this opt-in live test."),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? throw new InvalidOperationException("Set AZURE_OPENAI_DEPLOYMENT for this opt-in live test."), key,
            string.IsNullOrWhiteSpace(key) ? ModelConnection.ResponsesEntra : ModelConnection.ResponsesApiKey,
            Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "");
        var model = new FoundryResponsesModel(http, settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await model.PrepareAsync(timeout.Token);
        var result = await model.AnalyzeAsync(new(1, "Synthetic test; no real meeting content.", "",
            [new(1, DateTimeOffset.UtcNow, "Test", "We propose retrying payments when the request times out.")],
            [], null), timeout.Token);
        Assert.NotNull(result.Memory);
    }

    private sealed class LiveFoundryFactAttribute : FactAttribute
    {
        public LiveFoundryFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_FOUNDRY_TEST") != "1")
                Skip = "Opt-in only: requires a resource key or authorized Azure sign-in and makes billable synthetic model requests.";
        }
    }

    private sealed class SavedSettingsFoundryFactAttribute : FactAttribute
    {
        public SavedSettingsFoundryFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_SAVED_SETTINGS_TEST") != "1")
                Skip = "Explicit opt-in only: decrypts saved app keys in memory and sends billable synthetic text, never audio.";
        }
    }

    private sealed class DiagnosticHandler(ITestOutputHelper output, params string[] secrets)
        : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken);
            output.WriteLine($"Foundry HTTP {(int)response.StatusCode} in {watch.Elapsed.TotalSeconds:F2}s.");
            if (!response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(64_000, cancellationToken);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    foreach (string field in new[] { "code", "param", "message" })
                        if (error.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            string text = value.GetString()!;
                            foreach (string secret in secrets.Where(s => !string.IsNullOrWhiteSpace(s)))
                                text = text.Replace(secret.Trim(), "[redacted]", StringComparison.Ordinal);
                            output.WriteLine($"Azure error {field}: {text[..Math.Min(text.Length, 2000)]}");
                        }
            }
            return response;
        }
    }
}
