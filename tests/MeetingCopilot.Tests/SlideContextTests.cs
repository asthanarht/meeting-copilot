using System.Buffers.Binary;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public class SlideContextTests
{
    private static readonly byte[] Image = SyntheticSlide.Png();
    private static SessionSettings Settings => new("unused", "", "en-US", "synthetic", null, [], "",
        new("https://test.services.ai.azure.com/openai/v1", "test", "unused", ModelConnection.ResponsesApiKey));
    private static SlideAnalysis Result => new("Archive ingestion", "Retention: 30 days.", "Workers write to storage.", "Retry policy not visible.");

    [Fact]
    public void RejectsInvalidOrUnboundedImages()
    {
        Assert.Throws<ArgumentException>(() => SlideImage.Validate([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => SlideImage.Validate(new byte[SlideImage.MaxBytes + 1]));
        var oversized = Image.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 1601);
        Assert.Throws<ArgumentException>(() => SlideImage.Validate(oversized));
        SlideImage.Validate(Image);
    }

    [Fact]
    public async Task SlideAndSpeechHaveDistinctEvidenceAndAutomaticallyRequestAQuestion()
    {
        var model = new SlideModel();
        var input = new Input();
        await using var session = new MeetingSession(Settings, (_, _) => input, model);
        await session.StartAsync();
        var at = DateTimeOffset.UtcNow.AddSeconds(-3);
        input.Publish("The archive uses background workers.");
        var reading = session.AddSlideAsync(Image, at, default);
        input.Publish("The workers can retry.");
        Assert.Equal(2, session.GetContextSnapshot().Utterances.Length);
        model.Slide.SetResult(Result);
        Assert.Equal(Result, await reading);
        var snapshot = session.GetContextSnapshot();
        var slide = Assert.Single(snapshot.Utterances, u => u.Kind == "slide");
        Assert.Equal(at, slide.At);
        Assert.Contains("Retention: 30 days.", slide.Text);
        Assert.Equal(2, snapshot.Utterances.Count(u => u.Kind == "speech"));
        Assert.Equal(3, snapshot.Utterances.Select(u => u.Id).Distinct().Count());
        var request = await model.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(request.QuestionRequested);
        Assert.Contains(request.Utterances, u => u.Kind == "slide");
    }

    [Fact]
    public async Task PauseDiscardsALateSlideAndPreventsNewSends()
    {
        var model = new SlideModel();
        var input = new Input();
        await using var session = new MeetingSession(Settings, (_, _) => input, model);
        await session.StartAsync();
        var reading = session.AddSlideAsync(Image, DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AddSlideAsync(Image, DateTimeOffset.UtcNow, default));
        var pause = session.PauseAsync();
        Assert.True(input.Stopped);
        model.Slide.SetResult(Result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await pause;
        Assert.Empty(session.GetContextSnapshot().Utterances);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AddSlideAsync(Image, DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task CancelingSlideDoesNotStopSpeechOrAddLateContext()
    {
        var model = new SlideModel();
        var input = new Input();
        await using var session = new MeetingSession(Settings, (_, _) => input, model);
        await session.StartAsync();
        using var cancel = new CancellationTokenSource();
        var reading = session.AddSlideAsync(Image, DateTimeOffset.UtcNow, cancel.Token);
        cancel.Cancel();
        model.Slide.SetResult(Result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.True(session.IsRunning);
        input.Publish("Speech is still being captured.");
        Assert.Equal("speech", Assert.Single(session.GetContextSnapshot().Utterances).Kind);
    }

    [Fact]
    public void SlideSummaryDoesNotBecomeDuplicateMicrophoneEcho()
    {
        var engine = new MeetingEngine(new SlideModel(), "");
        var speech = new Utterance(1, DateTimeOffset.UtcNow, "Speech", "Retention: 30 days.");
        engine.Add(speech);
        engine.Add(speech with { Id = 2, Source = "Slide", Kind = "slide" });
        Assert.Equal(2, engine.Snapshot().Utterances.Length);
    }

    private sealed class Input : ISpeechAudioInput
    {
        public string Source => "Synthetic";
        public DateTimeOffset? LastPacket => null;
        public bool Stopped { get; private set; }
        public event Action<string, string, bool>? Transcript;
        public event Action<string>? Faulted { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void StopCapture() => Stopped = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(string text) => Transcript?.Invoke(Source, text, true);
    }

    private sealed class SlideModel : IMeetingModel, ISlideModel
    {
        public TaskCompletionSource<SlideAnalysis> Slide { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AnalysisRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SlideAnalysis> ReadSlideAsync(byte[] png, CancellationToken cancellationToken) => Slide.Task;
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            if (request.Utterances.Any(u => u.Kind == "slide")) Request.TrySetResult(request);
            return Task.FromResult(new AnalysisResult("Synthetic memory.", "none", null, "Synthetic silence."));
        }
    }
}
