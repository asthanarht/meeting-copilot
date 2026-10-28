using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace MeetingCopilot.Tests;

public class LiveStreamingTests(ITestOutputHelper output)
{
    [LiveVoiceFollowFact]
    public async Task SavedSpeechResourceFollowsSyntheticScriptWithoutAMicrophoneOrLlm()
    {
        const string script = "Today we review our archive design. Workers receive files and store them safely. " +
            "Then we explain how retries prevent duplicate uploads.";
        var saved = new SettingsStore().Load() ?? throw new InvalidOperationException("Save the Speech connection first.");
        byte[] pcm = SynthesizeLocally(script);
        var matcher = new ScriptSpeechMatcher(ScriptSpeechMatcher.Tokenize(script).Select(word => word.Text));
        var gate = new object();
        var config = StreamingSpeechConfiguration.Create(saved.SpeechKey, saved.SpeechRegion, saved.Locale, saved.SpeechEndpoint);
        using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        using var stream = AudioInputStream.CreatePushStream(format);
        using var audio = AudioConfig.FromStreamInput(stream);
        using var recognizer = new SpeechRecognizer(config, audio);
        var watch = new Stopwatch();
        var firstMatch = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string text, bool final)
        {
            lock (gate)
            {
                var match = matcher.Observe(text, final);
                if (match.Matched && !final) firstMatch.TrySetResult(watch.Elapsed);
                if (final)
                {
                    output.WriteLine($"Synthetic recognition: {text}; aligned word {match.WordIndex + 1}/{matcher.WordCount}.");
                    if (match.AtEnd && match.Matched) completed.TrySetResult();
                }
            }
        }
        recognizer.Recognizing += (_, e) => Observe(e.Result.Text, false);
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech) Observe(e.Result.Text, true);
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                completed.TrySetException(new InvalidOperationException($"Azure Speech canceled: {e.ErrorCode}."));
        };
        bool started = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await recognizer.StartContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
            started = true;
            watch.Start();
            for (int offset = 0; offset < pcm.Length; offset += 3200)
            {
                byte[] block = pcm[offset..Math.Min(pcm.Length, offset + 3200)];
                stream.Write(block);
                await Task.Delay(TimeSpan.FromSeconds(block.Length / 32000.0), deadline.Token);
            }
            var audioEnd = watch.Elapsed;
            byte[] silence = new byte[3200];
            for (int i = 0; i < 30 && !completed.Task.IsCompleted; i++)
            {
                stream.Write(silence);
                await Task.Delay(100, deadline.Token);
            }
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            var first = await firstMatch.Task.WaitAsync(TimeSpan.FromSeconds(1), deadline.Token);
            Assert.True(first < audioEnd, "Word following must begin before the utterance ends.");
            int position;
            lock (gate) position = matcher.CurrentWord;
            for (int i = 0; i < 10; i++)
            {
                stream.Write(silence);
                await Task.Delay(100, deadline.Token);
            }
            lock (gate) Assert.Equal(position, matcher.CurrentWord);
            output.WriteLine($"First provisional word match: {first.TotalSeconds:F2}s. Full-script alignment and silence hold verified without a microphone or LLM.");
        }
        finally
        {
            stream.Close();
            if (started) await recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Array.Clear(pcm);
        }
    }

    [LiveStreamingFact]
    public async Task SavedSpeechResourceStreamsSyntheticAudioAndFeedsTheQuestionModel()
    {
        var store = new SettingsStore();
        var saved = store.Load() ?? throw new InvalidOperationException("Save the Speech connection first.");
        byte[] pcm = SynthesizeLocally(
            "We retry a payment after a network timeout. We have not decided how to prevent duplicate charges.");
        Assert.InRange(pcm.Length, 32000, 640000);
        var config = StreamingSpeechConfiguration.Create(saved.SpeechKey, saved.SpeechRegion, saved.Locale, saved.SpeechEndpoint);
        using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        using var stream = AudioInputStream.CreatePushStream(format);
        using var audio = AudioConfig.FromStreamInput(stream);
        using var recognizer = new SpeechRecognizer(config, audio);
        var feed = new StreamingPcmFeed(new WaveFormat(16000, 16, 1), (data, count) => stream.Write(data, count));
        var watch = Stopwatch.StartNew();
        var partial = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finals = Channel.CreateUnbounded<(string Text, TimeSpan At)>();
        string? failure = null;
        recognizer.Recognizing += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Result.Text)) partial.TrySetResult(watch.Elapsed);
        };
        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech)
                finals.Writer.TryWrite((e.Result.Text, watch.Elapsed));
        };
        recognizer.Canceled += (_, e) =>
        {
            failure = $"Azure streaming canceled: {e.Reason}, {e.ErrorCode}.";
            finals.Writer.TryComplete(new InvalidOperationException(failure));
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        bool started = false;
        try
        {
            await recognizer.StartContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
            started = true;
            TimeSpan firstAudioEnd = default;
            (string Text, TimeSpan At) firstFinal = ("", TimeSpan.Zero);
            int rounds = Math.Max(3, (int)Math.Ceiling(20 / (pcm.Length / 32000.0 + 1.5)));
            for (int round = 0; round < rounds; round++)
            {
                for (int offset = 0; offset < pcm.Length; offset += 3200)
                {
                    Assert.Null(failure);
                    byte[] block = pcm[offset..Math.Min(pcm.Length, offset + 3200)];
                    feed.Push(block, block.Length);
                    await Task.Delay(TimeSpan.FromSeconds(block.Length / 32000.0), deadline.Token);
                }
                if (round == 0) firstAudioEnd = watch.Elapsed;
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(100, deadline.Token);
                    feed.PumpIdle();
                }
                var final = await finals.Reader.ReadAsync(deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                output.WriteLine($"Streaming final {round + 1}: {final.Text}");
                if (round == 0) firstFinal = final;
            }
            TimeSpan firstPartial = await partial.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(firstPartial < firstAudioEnd, "Partial transcription must arrive while audio is still being sent.");
            Assert.True(firstFinal.At - firstAudioEnd < TimeSpan.FromSeconds(5), "Final transcription must not require the next utterance.");
            Assert.True(watch.Elapsed > TimeSpan.FromSeconds(15), "The stream must remain active beyond the failed MAI deadline.");
            Assert.Null(failure);
            Assert.Contains("payment", firstFinal.Text, StringComparison.OrdinalIgnoreCase);
            output.WriteLine($"First partial: {firstPartial.TotalSeconds:F2}s after stream start; " +
                $"first final: {Math.Max(0, (firstFinal.At - firstAudioEnd).TotalSeconds):F2}s after utterance audio; " +
                $"continuous session: {watch.Elapsed.TotalSeconds:F2}s.");

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
            var model = new FoundryResponsesModel(http,
                new(saved.ModelEndpoint, saved.Deployment, saved.ModelKey, saved.ModelConnection, saved.TenantId));
            var engine = new MeetingEngine(model, "Synthetic live speech test; review the payment retry design.");
            engine.Add(new(1, DateTimeOffset.UtcNow, "Synthetic speech", firstFinal.Text));
            AnalysisUpdate? update = null;
            engine.Updated += value => update = value;
            var modelWatch = Stopwatch.StartNew();
            await engine.AnalyzeOnceAsync(deadline.Token);
            Assert.NotNull(update);
            Assert.Null(update.Error);
            Assert.NotNull(update.Cue);
            output.WriteLine($"Question from recognized audio in {modelWatch.Elapsed.TotalSeconds:F2}s: {update.Cue.Text}");
            Assert.All(update.Cue.EvidenceIds, id => Assert.Equal(1, id));

            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_ENABLE_VALIDATED_STREAMING") == "1")
            {
                if (store.Load() != saved)
                    throw new InvalidOperationException("Settings changed during validation; provider selection was not overwritten.");
                store.Save(saved with { SpeechProvider = SpeechProvider.AzureStreaming });
                Assert.Equal(SpeechProvider.AzureStreaming, store.Load()!.SpeechProvider);
                output.WriteLine("Saved provider switched to verified Azure streaming; other settings preserved.");
            }
        }
        finally
        {
            stream.Close();
            if (started) await recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Array.Clear(pcm);
        }
    }

    private static byte[] SynthesizeLocally(string text)
    {
        object voiceObject = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: true)!)!;
        object? memoryObject = null;
        try
        {
            memoryObject = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpMemoryStream", throwOnError: true)!)!;
            dynamic voice = voiceObject;
            dynamic memory = memoryObject;
            memory.Format.Type = 18; // SAPI's 16 kHz, 16-bit, mono PCM format.
            voice.AudioOutputStream = memory;
            voice.Speak(text, 0);
            return (byte[])memory.GetData();
        }
        finally
        {
            Marshal.ReleaseComObject(voiceObject);
            if (memoryObject is not null) Marshal.ReleaseComObject(memoryObject);
        }
    }

    private sealed class LiveVoiceFollowFactAttribute : FactAttribute
    {
        public LiveVoiceFollowFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_VOICE_FOLLOW_TEST") != "1")
                Skip = "Explicit opt-in: uses saved Speech keys with invented synthetic speech only; no microphone, script library or LLM.";
        }
    }

    private sealed class LiveStreamingFactAttribute : FactAttribute
    {
        public LiveStreamingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEETINGCOPILOT_LIVE_STREAMING_TEST") != "1")
                Skip = "Explicit opt-in: uses saved keys with synthetic local speech, no microphone or speaker output.";
        }
    }
}
