using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingCopilot.Desktop;

public sealed class SpeechAudioInput : ISpeechAudioInput
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly StreamingPcmFeed feed;
    private readonly PushAudioInputStream stream;
    private readonly AudioConfig audio;
    private readonly SpeechConfig config;
    private readonly SpeechRecognizer recognizer;
    private readonly object audioGate = new();
    private volatile bool stopping;
    private bool started;
    private int faulted;
    private long lastPacketTicks;
    private Timer? idlePump;

    public string Source { get; }
    public DateTimeOffset? LastPacket => Interlocked.Read(ref lastPacketTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public event Action<string, string, bool>? Transcript;
    public event Action<string>? Faulted;

    public SpeechAudioInput(string key, string region, string locale, string deviceId,
        bool loopback, IEnumerable<string> phrases, string endpoint = "")
    {
        Source = loopback ? "Meeting playback" : "Your microphone";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
            using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            stream = AudioInputStream.CreatePushStream(format);
            feed = new StreamingPcmFeed(capture.WaveFormat, (data, count) => stream.Write(data, count));
            audio = AudioConfig.FromStreamInput(stream);
            config = StreamingSpeechConfiguration.Create(key, region, locale, endpoint);
            recognizer = new SpeechRecognizer(config, audio);
            var grammar = PhraseListGrammar.FromRecognizer(recognizer);
            foreach (string phrase in phrases)
                grammar.AddPhrase(phrase);
            recognizer.Recognizing += (_, e) =>
            {
                if (!stopping && !string.IsNullOrWhiteSpace(e.Result.Text))
                    Transcript?.Invoke(Source, e.Result.Text, false);
            };
            recognizer.Recognized += (_, e) =>
            {
                if (stopping) return;
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    Transcript?.Invoke(Source, e.Result.Text, true);
                else if (e.Result.Reason == ResultReason.NoMatch)
                    Transcript?.Invoke(Source, "[Speech could not be recognized clearly]", false);
            };
            recognizer.Canceled += (_, e) =>
            {
                if (!stopping)
                    SignalFault($"{Source}: streaming speech stopped ({e.Reason}, {e.ErrorCode}). Check the Speech key, endpoint or region, and connectivity, then resume.");
            };
            recognizer.SessionStopped += (_, _) =>
            {
                if (!stopping) SignalFault($"{Source}: speech session ended unexpectedly. Resume to reconnect.");
            };
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, e) =>
            {
                if (!stopping)
                    SignalFault($"{Source}: audio capture stopped{(e.Exception is null ? "" : $" ({e.Exception.GetType().Name})")}. Check the selected device and resume.");
            };
        }
        catch
        {
            // A native SDK/device failure can occur before this instance is owned by the session.
            stopping = true;
            capture?.Dispose();
            recognizer?.Dispose();
            audio?.Dispose();
            stream?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await recognizer.StartContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        started = true;
        lock (audioGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopping) throw new OperationCanceledException("Audio startup was stopped.", cancellationToken);
            if (Volatile.Read(ref faulted) != 0)
                throw new InvalidOperationException($"{Source}: speech connection failed.");
            capture.StartRecording();
            idlePump = new Timer(_ => PumpIdle(), null, 100, 100);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (audioGate)
        {
            if (stopping || e.BytesRecorded == 0) return;
            try
            {
                Interlocked.Exchange(ref lastPacketTicks, DateTimeOffset.UtcNow.Ticks);
                feed.Push(e.Buffer, e.BytesRecorded);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
            {
                SignalFault($"{Source}: audio conversion failed ({ex.GetType().Name}). Capture must be restarted.");
            }
        }
    }

    private void PumpIdle()
    {
        lock (audioGate)
        {
            if (stopping) return;
            try { feed.PumpIdle(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
            {
                SignalFault($"{Source}: streaming audio flush failed ({ex.GetType().Name}). Resume capture.");
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
        lock (audioGate)
        {
            if (stopping) return;
            stopping = true;
            idlePump?.Dispose();
        }
        capture.StopRecording();
        stream.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            StopCapture();
            if (started)
                await recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            capture.Dispose();
            recognizer.Dispose();
            audio.Dispose();
            stream.Dispose();
            device.Dispose();
        }
    }
}
