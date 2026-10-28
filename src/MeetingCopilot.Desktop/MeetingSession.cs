using MeetingCopilot.Core;
using System.IO;
using System.Net.Http;

namespace MeetingCopilot.Desktop;

public enum SpeechProvider { AzureStreaming, MaiTranscribe2 }

public sealed record SessionSettings(
    string SpeechKey, string SpeechRegion, string Locale,
    string PlaybackId, string? MicrophoneId, string[] Phrases,
    string Brief, ModelSettings Model,
    SpeechProvider SpeechProvider = SpeechProvider.AzureStreaming, string SpeechEndpoint = "");

public sealed class MeetingSession : IAsyncDisposable
{
    private readonly SessionSettings settings;
    private readonly Func<string, bool, ISpeechAudioInput> createInput;
    private readonly IMeetingModel model;
    private readonly HttpClient http = MeetingModelFactory.CreateHttpClient();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object stateGate = new();
    private ISpeechAudioInput[] inputs = [];
    private CancellationTokenSource? run;
    private Task? analysis;
    private Task<SlideAnalysis>? slideAnalysis;
    private MeetingEngine? engine;
    private long utteranceId;
    private volatile bool active;
    private bool disposalRequested;
    private Task? disposal;

    public MeetingSession(SessionSettings settings) : this(settings,
        (device, loopback) => settings.SpeechProvider == SpeechProvider.MaiTranscribe2
            ? new MaiSpeechAudioInput(new(settings.SpeechEndpoint, settings.SpeechKey, settings.Phrases), device, loopback)
            : new SpeechAudioInput(settings.SpeechKey, settings.SpeechRegion,
                settings.Locale, device, loopback, settings.Phrases, settings.SpeechEndpoint), null)
    {
    }

    internal MeetingSession(SessionSettings settings, Func<string, bool, ISpeechAudioInput> createInput,
        IMeetingModel? model)
    {
        this.settings = settings;
        this.createInput = createInput;
        this.model = model ?? MeetingModelFactory.Create(http, settings.Model);
    }

    public event Action<AnalysisUpdate>? Updated;
    public event Action<string>? AnalysisStatusChanged;
    public event Action<string, string, bool>? Transcript;
    public event Action<string>? Faulted;
    public bool IsRunning => active;
    public bool SupportsSlides => model is ISlideModel;
    public AnalysisProgress GetAnalysisProgress() =>
        engine?.GetProgress() ?? new(0, 0, null, null, null, null, null);
    public AnalysisRequest GetContextSnapshot() =>
        engine?.Snapshot() ?? new(0, settings.Brief, "", [], [], null);
    public string TranscriptionDescription => settings.SpeechProvider == SpeechProvider.MaiTranscribe2
        ? "MAI-Transcribe-2: short audio uploads, not word streaming. No accuracy guarantee."
        : "Azure streaming speech: waiting for finalized speech. No accuracy guarantee.";
    public string AudioStatus => string.Join(" | ", inputs.Select(i =>
        $"{i.Source}: {(i.LastPacket is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(3) ? "receiving audio" : "waiting for audio")}" +
        (i.ProcessingStatus is { } status ? $" - {status}" : "")));

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            lock (stateGate) ObjectDisposedException.ThrowIf(disposalRequested, this);
            if (run is not null) return;
            engine ??= CreateEngine();
            lock (stateGate) run = new CancellationTokenSource();
            using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(run.Token))
            {
                authentication.CancelAfter(settings.Model.Connection is ModelConnection.OpenAICompatible or ModelConnection.Gemini or ModelConnection.Anthropic or ModelConnection.Grok
                    ? model.RequestTimeout : TimeSpan.FromSeconds(30));
                try { await model.PrepareAsync(authentication.Token).WaitAsync(authentication.Token); }
                catch (OperationCanceledException) when (!run.IsCancellationRequested)
                {
                    throw new ModelAuthenticationException("Model access check timed out before audio capture. Check provider credentials, model access and latency, then retry.");
                }
            }
            run.Token.ThrowIfCancellationRequested();
            var playback = createInput(settings.PlaybackId, true);
            inputs = [playback];
            if (settings.MicrophoneId is not null)
                inputs = [playback, createInput(settings.MicrophoneId, false)];
            foreach (var input in inputs)
            {
                input.Transcript += OnTranscript;
                input.Faulted += OnFault;
            }
            active = true;
            foreach (var input in inputs)
                await input.StartAsync(run.Token);
            analysis = RunAnalysisAsync(run.Token);
        }
        catch
        {
            // Roll back partial startup, including an already-running playback stream.
            await StopLockedAsync();
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private MeetingEngine CreateEngine()
    {
        var result = new MeetingEngine(model, settings.Brief);
        result.Updated += update =>
        {
            if (active) Updated?.Invoke(update);
        };
        result.AnalysisStatusChanged += status =>
        {
            if (active) AnalysisStatusChanged?.Invoke(status);
        };
        return result;
    }

    private async Task RunAnalysisAsync(CancellationToken cancellationToken)
    {
        try { await engine!.RunAsync(cancellationToken); }
        catch (ModelAuthenticationException ex) { OnFault(ex.Message); }
    }

    private void OnTranscript(string source, string text, bool final)
    {
        if (!active) return;
        if (final)
        {
            try
            {
                engine!.Add(new(Interlocked.Increment(ref utteranceId), DateTimeOffset.UtcNow, source, text));
            }
            catch (ArgumentException)
            {
                OnFault("A speech segment exceeded the safe context size. Pause and resume capture.");
                return;
            }
        }
        Transcript?.Invoke(source, text, final);
    }

    private void OnFault(string message)
    {
        active = false;
        RequestStop();
        Faulted?.Invoke(message);
    }

    public void RequestStop()
    {
        lock (stateGate) run?.Cancel();
    }

    public void Dismiss(bool covered, string visibleText) => engine?.Dismiss(covered, visibleText);
    public void FindGap() => engine?.RequestNow();

    public Task<SlideAnalysis> AddSlideAsync(byte[] png, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        SlideImage.Validate(png);
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposalRequested, this);
            if (!active || run is null) throw new InvalidOperationException("Start or resume listening before sending a slide.");
            if (model is not ISlideModel reader)
                throw new NotSupportedException("This connection is text-only. Slide reading requires a Foundry Responses connection with an image-capable deployment.");
            if (slideAnalysis is { IsCompleted: false })
                throw new InvalidOperationException("A slide is already being analyzed.");
            slideAnalysis = ReadAndAddSlideAsync(reader, png, capturedAt, run.Token, cancellationToken);
            return slideAnalysis;
        }
    }

    private async Task<SlideAnalysis> ReadAndAddSlideAsync(ISlideModel reader, byte[] png,
        DateTimeOffset capturedAt, CancellationToken runToken, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var result = await reader.ReadSlideAsync(png, timeout.Token);
        // Publish through the same bounded evidence context, without waiting for or replacing speech analysis.
        lock (stateGate)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!active || engine is null) throw new OperationCanceledException("Listening stopped before slide analysis completed.");
            engine.Add(new(Interlocked.Increment(ref utteranceId), capturedAt, "Slide capture (AI-extracted)", result.ToContext(), "slide"));
            engine.RequestNow();
        }
        return result;
    }

    public async Task PauseAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopLockedAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task StopLockedAsync()
    {
        active = false;
        RequestStop();
        var errors = new List<Exception>();
        // Stop every device before awaiting any network/session cleanup.
        foreach (var input in inputs)
        {
            try { input.StopCapture(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var input in inputs)
        {
            input.Transcript -= OnTranscript;
            input.Faulted -= OnFault;
            try { await input.DisposeAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        inputs = [];
        if (analysis is not null)
        {
            try { await analysis; }
            catch (OperationCanceledException) when (run?.IsCancellationRequested == true) { }
            catch (Exception ex) { errors.Add(ex); }
        }
        var pendingSlide = slideAnalysis;
        if (pendingSlide is not null)
        {
            try { await pendingSlide; }
            catch (OperationCanceledException) when (run?.IsCancellationRequested == true) { }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
                System.Text.Json.JsonException or ModelAuthenticationException or OperationCanceledException)
            {
                // The caller reports the slide failure; audio cleanup must still finish.
            }
        }
        slideAnalysis = null;
        analysis = null;
        lock (stateGate)
        {
            run?.Dispose();
            run = null;
        }
        engine?.ClearCue("Paused.");
        if (errors.Count > 0)
            throw new AggregateException("Capture stopped, but one or more resources failed to close cleanly.", errors);
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            disposalRequested = true;
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await PauseAsync(); }
        finally
        {
            engine = null;
            http.Dispose();
        }
    }
}
