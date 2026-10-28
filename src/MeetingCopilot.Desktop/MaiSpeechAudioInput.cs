using System.Net.Http;
using System.Runtime.InteropServices;
using MeetingCopilot.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingCopilot.Desktop;

internal sealed class MaiSpeechAudioInput : ISpeechAudioInput
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(16) };
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly PcmAudioConverter converter;
    private readonly PcmSegmenter segmenter = new();
    private readonly MaiSegmentPipeline pipeline;
    private readonly object gate = new();
    private Timer? idleFlush;
    private long lastPacketTicks;
    private bool stopping;
    private int faulted;

    public string Source { get; }
    public string? ProcessingStatus => pipeline.Status;
    public DateTimeOffset? LastPacket => Interlocked.Read(ref lastPacketTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public event Action<string, string, bool>? Transcript;
    public event Action<string>? Faulted;

    public MaiSpeechAudioInput(MaiSpeechSettings settings, string deviceId, bool loopback)
    {
        Source = loopback ? "Meeting playback" : "Your microphone";
        try
        {
            _ = settings.GetRequestUri();
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            converter = new(capture.WaveFormat);
            pipeline = new(new MaiTranscriptionClient(http, settings));
            pipeline.Transcribed += text =>
            {
                lock (gate)
                    if (!stopping) Transcript?.Invoke(Source, text, true);
            };
            pipeline.Faulted += SignalFault;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) =>
            {
                lock (gate)
                    if (!stopping)
                        SignalFault($"{Source}: audio capture stopped. Check the device and resume.");
            };
        }
        catch
        {
            capture?.Dispose();
            device?.Dispose();
            http.Dispose();
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pipeline.Start(cancellationToken);
        capture.StartRecording();
        idleFlush = new Timer(_ => FlushIdle(), null, 250, 250);
        return Task.CompletedTask;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (stopping) return;
            try
            {
                Interlocked.Exchange(ref lastPacketTicks, DateTimeOffset.UtcNow.Ticks);
                converter.Push(e.Buffer, e.BytesRecorded,
                    (pcm, count) => segmenter.Push(pcm.AsSpan(0, count), pipeline.Enqueue));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
            {
                SignalFault($"{Source}: audio conversion failed ({ex.GetType().Name}). Resume capture.");
            }
        }
    }

    private void FlushIdle()
    {
        lock (gate)
        {
            if (stopping || LastPacket is not { } last ||
                DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(800)) return;
            try
            {
                converter.Flush((pcm, count) => segmenter.Push(pcm.AsSpan(0, count), pipeline.Enqueue));
                segmenter.Flush(pipeline.Enqueue);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
            {
                SignalFault($"{Source}: trailing audio conversion failed ({ex.GetType().Name}). Resume capture.");
            }
        }
    }

    private void SignalFault(string message)
    {
        if (Interlocked.Exchange(ref faulted, 1) == 0)
            Faulted?.Invoke(message);
    }

    public void StopCapture()
    {
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            idleFlush?.Dispose();
            segmenter.Clear();
            pipeline.Stop();
        }
        capture.StopRecording();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            StopCapture();
            await pipeline.DisposeAsync();
        }
        finally
        {
            capture.Dispose();
            device.Dispose();
            http.Dispose();
        }
    }
}
