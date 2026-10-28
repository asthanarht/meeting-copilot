using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using System.Net;
using System.Text;
using Xunit;

namespace MeetingCopilot.Tests;

public class SessionLifecycleTests
{
    private static SessionSettings Settings => new(
        "synthetic-key", "eastus", "en-US", "playback", "microphone", [],
        "Payment design review", new("https://test.openai.azure.com", "deployment", "synthetic-key"));

    [Fact]
    public async Task PauseStopsBothDevicesBeforeWaitingForCloudCleanup()
    {
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new FakeInput("Playback") { Cleanup = releaseCleanup.Task };
        var microphone = new FakeInput("Microphone");
        await using var session = new MeetingSession(Settings, (_, loopback) => loopback ? playback : microphone,
            new FakeModel());
        await session.StartAsync();
        Assert.True(playback.Capturing);
        Assert.True(microphone.Capturing);
        var pause = session.PauseAsync();
        try
        {
            Assert.False(playback.Capturing);
            Assert.False(microphone.Capturing);
            Assert.False(session.IsRunning);
            Assert.False(pause.IsCompleted);
        }
        finally { releaseCleanup.SetResult(); }
        await pause;
        Assert.True(playback.Disposed);
        Assert.True(microphone.Disposed);
    }

    [Fact]
    public async Task MicrophoneStartupFailureRollsBackPlaybackAndAllResources()
    {
        var playback = new FakeInput("Playback");
        var microphone = new FakeInput("Microphone") { FailStart = true };
        await using var session = new MeetingSession(Settings, (_, loopback) => loopback ? playback : microphone,
            new FakeModel());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.False(playback.Capturing);
        Assert.False(microphone.Capturing);
        Assert.True(playback.Disposed);
        Assert.True(microphone.Disposed);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StopCancelsAnInflightSpeechConnectionAndRollsBack()
    {
        var playback = new FakeInput("Playback") { WaitForCancellation = true };
        await using var session = new MeetingSession(Settings with { MicrophoneId = null },
            (_, _) => playback, new FakeModel());
        var start = session.StartAsync();
        session.RequestStop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(playback.Disposed);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task OnlyFinalSpeechTravelsThroughSessionToQuestionModel()
    {
        var playback = new FakeInput("Playback");
        var model = new FakeModel();
        await using var session = new MeetingSession(Settings with { MicrophoneId = null },
            (_, _) => playback, model);
        var update = new TaskCompletionSource<AnalysisUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += value => update.TrySetResult(value);
        Assert.Equal("Payment design review", session.GetContextSnapshot().MeetingBrief);
        Assert.Empty(session.GetContextSnapshot().Utterances);
        await session.StartAsync();
        playback.Publish("We might retry a...", false);
        Assert.Empty(session.GetContextSnapshot().Utterances);
        playback.Publish("We retry payments after a timeout.", true);
        var result = await update.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result.Cue);
        Assert.Equal("We retry payments after a timeout.", Assert.Single(model.LastRequest!.Utterances).Text);
        Assert.Equal("Payment design review", model.LastRequest.MeetingBrief);
        var context = session.GetContextSnapshot();
        Assert.Equal("Retries discussed.", context.Memory);
        Assert.Equal(model.LastRequest.Utterances, context.Utterances);
        Assert.DoesNotContain(Settings.SpeechKey, System.Text.Json.JsonSerializer.Serialize(context));
        await session.PauseAsync();
        Assert.Equal(context.Utterances, session.GetContextSnapshot().Utterances);
        Assert.Equal(context.Memory, session.GetContextSnapshot().Memory);
        playback.Publish("Must not reach the model while paused.", true);
        Assert.Single(model.LastRequest.Utterances);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndPreventsRestart()
    {
        var input = new FakeInput("Playback");
        var session = new MeetingSession(Settings with { MicrophoneId = null }, (_, _) => input, new FakeModel());
        await session.StartAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
        Assert.Equal(1, input.DisposeCount);
    }

    private sealed class FakeModel : IMeetingModel
    {
        public AnalysisRequest? LastRequest { get; private set; }
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new AnalysisResult("Retries discussed.", "none",
                new("question", "What prevents duplicate charges?", "Retries can duplicate requests.",
                    "May already be handled.", [request.Utterances[0].Id])));
        }
    }

    [Fact]
    public async Task ModelAuthenticationFailureDoesNotOpenAudioDevices()
    {
        int devicesCreated = 0;
        await using var session = new MeetingSession(Settings, (_, _) =>
        {
            devicesCreated++;
            return new FakeInput("Playback");
        }, new AuthenticationFailureModel());
        await Assert.ThrowsAsync<ModelAuthenticationException>(() => session.StartAsync());
        Assert.Equal(0, devicesCreated);
        Assert.False(session.IsRunning);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task FailedResponsesPreflightDoesNotOpenDevicesOrSendMeetingContext(int status)
    {
        int devicesCreated = 0, requests = 0;
        using var http = new HttpClient(new PreflightHandler(async request =>
        {
            requests++;
            string payload = await request.Content!.ReadAsStringAsync();
            Assert.Contains("synthetic connectivity check", payload);
            Assert.DoesNotContain(Settings.Brief, payload);
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"message":"synthetic private details"}}""",
                    Encoding.UTF8, "application/json")
            };
        }));
        var settings = Settings with
        {
            Model = new("https://example.services.ai.azure.com/openai/v1", "gpt-6-astra",
                "synthetic-resource-key", ModelConnection.ResponsesApiKey)
        };
        await using var session = new MeetingSession(settings, (_, _) =>
        {
            devicesCreated++;
            return new FakeInput("Playback");
        }, new FoundryResponsesModel(http, settings.Model));
        var error = await Record.ExceptionAsync(() => session.StartAsync());
        Assert.True(error is ModelAuthenticationException or HttpRequestException);
        Assert.Equal(1, requests);
        Assert.Equal(0, devicesCreated);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task AuthorizationLossAfterStartupRaisesSessionFaultInsteadOfRetrying()
    {
        var input = new FakeInput("Playback");
        var model = new ExpiredAuthorizationModel();
        await using var session = new MeetingSession(Settings with { MicrophoneId = null }, (_, _) => input, model);
        var fault = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += message => fault.TrySetResult(message);
        await session.StartAsync();
        input.Publish("We retry payments after a timeout.", true);
        Assert.Contains("authorization", await fault.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(session.IsRunning);
        await session.PauseAsync();
        Assert.False(input.Capturing);
        Assert.True(input.Disposed);
        Assert.Equal(1, model.Calls);
    }

    private sealed class ExpiredAuthorizationModel : IMeetingModel
    {
        public int Calls { get; private set; }
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new ModelAuthenticationException("Synthetic authorization revoked.");
        }
    }

    private sealed class PreflightHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    private sealed class AuthenticationFailureModel : IMeetingModel
    {
        public Task PrepareAsync(CancellationToken cancellationToken) =>
            Task.FromException(new ModelAuthenticationException("Synthetic sign-in required."));
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Analysis must not run without authentication.");
    }
    private sealed class FakeInput(string source) : ISpeechAudioInput
    {
        public string Source => source;
        public DateTimeOffset? LastPacket => null;
        public bool Capturing { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailStart { get; init; }
        public bool WaitForCancellation { get; init; }
        public Task Cleanup { get; init; } = Task.CompletedTask;
        public event Action<string, string, bool>? Transcript;
        public event Action<string>? Faulted;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Capturing = true;
            if (FailStart) throw new InvalidOperationException("Synthetic input failure");
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public void Publish(string text, bool final) => Transcript?.Invoke(Source, text, final);
        public void Fail() => Faulted?.Invoke("Synthetic device failure");
        public void StopCapture() => Capturing = false;
        public async ValueTask DisposeAsync()
        {
            StopCapture();
            await Cleanup;
            Disposed = true;
            DisposeCount++;
        }
    }
}
