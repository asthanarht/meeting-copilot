using System.Net;
using System.Text;
using System.Text.Json;
using MeetingCopilot.Core;
using Xunit;

namespace MeetingCopilot.Tests;

public class MaiTranscriptionTests
{
    private static MaiSpeechSettings Settings =>
        new("https://example.cognitiveservices.azure.com/", "synthetic-test-key", ["idempotency", "WASAPI"]);

    [Fact]
    public async Task MultipartRequestMatchesMaiContractAndContainsValidWaveFile()
    {
        byte[] pcm = Enumerable.Range(0, 32000).Select(i => (byte)(i % 256)).ToArray();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("https://example.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-test-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToArray();
            Assert.Equal(2, parts.Length);
            var audio = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "audio");
            Assert.Equal("audio/wav", audio.Headers.ContentType!.MediaType);
            byte[] wave = await audio.ReadAsByteArrayAsync();
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
            Assert.Equal(16000, BitConverter.ToInt32(wave, 24));
            Assert.Equal(1, BitConverter.ToInt16(wave, 22));
            Assert.Equal(16, BitConverter.ToInt16(wave, 34));
            Assert.Equal(pcm.Length, BitConverter.ToInt32(wave, 40));
            Assert.Equal(pcm, wave[44..]);
            var definition = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "definition");
            using var json = JsonDocument.Parse(await definition.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("en", root.GetProperty("locales")[0].GetString());
            Assert.True(root.GetProperty("diarization").GetProperty("enabled").GetBoolean());
            Assert.Equal("idempotency", root.GetProperty("phraseList").GetProperty("phrases")[0].GetString());
            var enhanced = root.GetProperty("enhancedMode");
            Assert.True(enhanced.GetProperty("enabled").GetBoolean());
            Assert.Equal("mai-transcribe-2", enhanced.GetProperty("model").GetString());
            Assert.Equal("word", enhanced.GetProperty("modelOptions").GetProperty("timestamps").GetString());
            Assert.Equal("verbatim", enhanced.GetProperty("modelOptions").GetProperty("transcribeStyle").GetString());
            return Response("""{"combinedPhrases":[{"text":"We will retry payments."}],"phrases":[]}""");
        }));
        Assert.Equal("We will retry payments.",
            await new MaiTranscriptionClient(http, Settings).TranscribeAsync(pcm, default));
    }

    [Theory]
    [InlineData("http://example.cognitiveservices.azure.com/")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.cognitiveservices.azure.com/not-root")]
    [InlineData("https://example.cognitiveservices.azure.com/?key=x")]
    [InlineData("https://user:secret@example.cognitiveservices.azure.com/")]
    [InlineData("https://example.cognitiveservices.azure.com:8443/")]
    public void RejectsUnsafeEndpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => (Settings with { Endpoint = endpoint }).GetRequestUri());

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"combinedPhrases":null}""")]
    [InlineData("""{"combinedPhrases":[{}]}""")]
    [InlineData("""{"combinedPhrases":[{"text":12}]}""")]
    public void RejectsInvalidSuccessResponses(string body) =>
        Assert.Throws<InvalidDataException>(() => MaiTranscriptionClient.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void SilenceIsEmptyButNotAnInvalidResponse() =>
        Assert.Equal("", MaiTranscriptionClient.Parse("""{"combinedPhrases":[]}"""u8.ToArray()));

    [Fact]
    public async Task ServiceErrorsDoNotDiscloseResponseBodies()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("private transcript") })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new MaiTranscriptionClient(http, Settings).TranscribeAsync(new byte[3200], default));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
    }

    [Fact]
    public async Task CancellationPropagatesToHttp()
    {
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        }));
        using var cancel = new CancellationTokenSource();
        var task = new MaiTranscriptionClient(http, Settings).TranscribeAsync(new byte[3200], cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(192002)]
    public void RejectsInvalidPcm(int length) =>
        Assert.Throws<ArgumentException>(() => MaiTranscriptionClient.CreateWav(new byte[length]));

    [Fact]
    public void SegmenterPreservesEverySampleAndCapsContinuousSpeech()
    {
        byte[] pcm = new byte[500000];
        for (int i = 0; i < pcm.Length; i += 2)
            BitConverter.TryWriteBytes(pcm.AsSpan(i, 2), (short)2000);
        var segmenter = new PcmSegmenter();
        var segments = new List<byte[]>();
        for (int i = 0; i < pcm.Length; i += 1000)
            segmenter.Push(pcm.AsSpan(i, Math.Min(1000, pcm.Length - i)), segments.Add);
        segmenter.Flush(segments.Add);
        Assert.All(segments, segment => Assert.InRange(segment.Length, 2, PcmSegmenter.MaxBytes));
        Assert.Equal(pcm, segments.SelectMany(s => s).ToArray());
        Assert.Equal(PcmSegmenter.MaxBytes, segments[0].Length);
    }

    [Fact]
    public void QuietBoundarySplitsWithoutDroppingQuietAudio()
    {
        var pcm = new byte[96000];
        for (int i = 0; i < 48000; i += 2)
            BitConverter.TryWriteBytes(pcm.AsSpan(i, 2), (short)2000);
        var segments = new List<byte[]>();
        var segmenter = new PcmSegmenter();
        segmenter.Push(pcm, segments.Add);
        Assert.Single(segments);
        Assert.Equal(70400, segments[0].Length);
        segmenter.Flush(segments.Add);
        Assert.Equal(pcm, segments.SelectMany(s => s).ToArray());
    }

    [Fact]
    public async Task PipelinePreservesOrderAndEmitsOnlyFinalResponses()
    {
        var transcriber = new StubTranscriber((pcm, _) => Task.FromResult(pcm[0].ToString()));
        await using var pipeline = new MaiSegmentPipeline(transcriber);
        var values = new List<string>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.Transcribed += value =>
        {
            values.Add(value);
            if (values.Count == 2) complete.SetResult();
        };
        pipeline.Enqueue([1, 0]);
        pipeline.Enqueue([2, 0]);
        pipeline.Start(default);
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["1", "2"], values);
    }

    [Fact]
    public async Task FullQueueRaisesVisibleFaultInsteadOfDroppingAudioSilently()
    {
        await using var pipeline = new MaiSegmentPipeline(new StubTranscriber((_, _) => Task.FromResult("")));
        string? fault = null;
        pipeline.Faulted += message => fault = message;
        pipeline.Enqueue([1, 0]);
        pipeline.Enqueue([2, 0]);
        pipeline.Enqueue([3, 0]);
        Assert.Contains("falling behind", fault);
    }

    [Fact]
    public async Task TwoUploadsRunConcurrentlyButOutOfOrderResultsRemainBoundedAndOrdered()
    {
        var entered = Enumerable.Range(0, 2)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 2)
            .Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var secondReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var transcriber = new StubTranscriber(async (pcm, token) =>
        {
            Interlocked.Increment(ref calls);
            int id = pcm[0];
            if (id > 2) return id.ToString();
            entered[id - 1].SetResult();
            string result = await release[id - 1].Task.WaitAsync(token);
            if (id == 2) secondReturned.SetResult();
            return result;
        });
        await using var pipeline = new MaiSegmentPipeline(transcriber);
        var values = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.Transcribed += text =>
        {
            values.Add(text);
            if (values.Count == 4) done.SetResult();
        };
        byte[][] segments = [[1, 0], [2, 0], [3, 0], [4, 0]];
        pipeline.Enqueue(segments[0]);
        pipeline.Enqueue(segments[1]);
        pipeline.Start(default);
        await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(3));
        release[1].SetResult("2");
        await secondReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pipeline.Enqueue(segments[2]);
        pipeline.Enqueue(segments[3]);
        Assert.Empty(values);
        Assert.Equal(2, Volatile.Read(ref calls));
        release[0].SetResult("1");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["1", "2", "3", "4"], values);
    }

    [Fact]
    public async Task DigitalSilenceDoesNotFillQueueButEvenVeryQuietNonzeroAudioIsUploaded()
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new MaiSegmentPipeline(new StubTranscriber((pcm, _) =>
        {
            done.SetResult(pcm.ToArray());
            return Task.FromResult("");
        }));
        string? fault = null;
        pipeline.Faulted += value => fault = value;
        for (int i = 0; i < 100; i++) pipeline.Enqueue(new byte[64000]);
        Assert.Contains("silence", pipeline.Status);
        pipeline.Enqueue([1, 0, 0, 0]);
        pipeline.Start(default);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, await done.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(fault);
    }

    [Fact]
    public async Task ConcurrentUploadsSustainRequestsSlowerThanOneSegmentInterval()
    {
        int active = 0, maxActive = 0;
        var transcriber = new StubTranscriber(async (pcm, token) =>
        {
            int count = Interlocked.Increment(ref active);
            Assert.InRange(count, 1, 2);
            if (count == 2) Interlocked.Exchange(ref maxActive, 2);
            try
            {
                await Task.Delay(180, token);
                return pcm[0].ToString();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var pipeline = new MaiSegmentPipeline(transcriber);
        var values = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? fault = null;
        pipeline.Faulted += message => fault = message;
        pipeline.Transcribed += text =>
        {
            values.Add(text);
            if (values.Count == 20) done.SetResult();
        };
        pipeline.Start(default);
        for (byte i = 1; i <= 20; i++)
        {
            pipeline.Enqueue([i, 0]);
            await Task.Delay(100);
        }
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fault);
        Assert.Equal(2, maxActive);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => i.ToString()), values);
    }

    [Fact]
    public async Task FailureCancelsTheOtherConcurrentUploadAndClearsAudio()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, published = 0;
        var transcriber = new StubTranscriber(async (pcm, token) =>
        {
            int id = pcm[0];
            if (Interlocked.Increment(ref calls) == 2) bothEntered.SetResult();
            await bothEntered.Task.WaitAsync(token);
            if (id == 1) throw new HttpRequestException("MAI returned HTTP 429.");
            await Task.Delay(Timeout.Infinite, token);
            return "must not publish";
        });
        byte[] first = [1, 0], second = [2, 0];
        await using (var pipeline = new MaiSegmentPipeline(transcriber))
        {
            pipeline.Faulted += error => failed.TrySetResult(error);
            pipeline.Transcribed += _ => Interlocked.Increment(ref published);
            pipeline.Enqueue(first);
            pipeline.Enqueue(second);
            pipeline.Start(default);
            Assert.Contains("429", await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.All(first.Concat(second), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task StopCancelsInflightTranscriptionAndPreventsPublication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcriber = new StubTranscriber(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.SetResult(); throw; }
            return "must not publish";
        });
        await using var pipeline = new MaiSegmentPipeline(transcriber);
        int publications = 0;
        pipeline.Transcribed += _ => publications++;
        pipeline.Start(default);
        pipeline.Enqueue([1, 0]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pipeline.Stop();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, publications);
    }

    private static HttpResponseMessage Response(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class StubTranscriber(Func<byte[], CancellationToken, Task<string>> transcribe) : IAudioTranscriber
    {
        public Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellationToken) => transcribe(pcm, cancellationToken);
    }
}
