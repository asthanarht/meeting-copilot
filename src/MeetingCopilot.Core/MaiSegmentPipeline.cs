using System.Threading.Channels;
using System.Text.Json;

namespace MeetingCopilot.Core;

public sealed class MaiSegmentPipeline(IAudioTranscriber transcriber) : IAsyncDisposable
{
    private readonly Channel<(long Sequence, byte[] Pcm, DateTimeOffset Queued)> queue =
        Channel.CreateBounded<(long, byte[], DateTimeOffset)>(new BoundedChannelOptions(2)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim publicationSlots = new(2, 2);
    private readonly object orderGate = new();
    private readonly object enqueueGate = new();
    private readonly SortedDictionary<long, (string Text, DateTimeOffset Queued)> completed = [];
    private Task? worker;
    private long sequence;
    private long nextPublication = 1;
    private int faulted;
    private volatile string status = "waiting for an audio segment";
    public string Status => status;
    public event Action<string>? Transcribed;
    public event Action<string>? Faulted;

    public void Start(CancellationToken token)
    {
        if (worker is not null) throw new InvalidOperationException("The transcription pipeline already started.");
        worker = RunAsync(token);
    }

    public void Enqueue(byte[] pcm)
    {
        if (stop.IsCancellationRequested)
        {
            Array.Clear(pcm);
            return;
        }
        // Only discard digital zero, never quiet speech or background noise based on amplitude.
        if (pcm.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            status = "digital silence; no upload";
            return;
        }
        lock (enqueueGate)
        {
            if (stop.IsCancellationRequested)
            {
                Array.Clear(pcm);
                return;
            }
            if (!queue.Writer.TryWrite((++sequence, pcm, DateTimeOffset.UtcNow)))
            {
                Array.Clear(pcm);
                Fail("MAI requests are falling behind even with two concurrent uploads. Capture stopped without silently skipping speech. Check service latency or select Azure streaming in setup.");
            }
        }
    }

    public void Stop()
    {
        stop.Cancel();
        queue.Writer.TryComplete();
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref faulted, 1) != 0) return;
        Stop();
        Faulted?.Invoke(message);
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        try
        {
            await Task.WhenAll(RunWorkerAsync(linked.Token), RunWorkerAsync(linked.Token));
        }
        finally
        {
            Stop();
            while (queue.Reader.TryRead(out var pending)) Array.Clear(pending.Pcm);
            lock (orderGate) completed.Clear();
        }
    }

    private async Task RunWorkerAsync(CancellationToken token)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(token))
            {
                // A later completed result holds its slot until earlier text is published.
                // This bounds both HTTP concurrency and the out-of-order result buffer.
                await publicationSlots.WaitAsync(token);
                if (!queue.Reader.TryRead(out var segment))
                {
                    publicationSlots.Release();
                    continue;
                }
                try
                {
                    if (DateTimeOffset.UtcNow - segment.Queued > TimeSpan.FromSeconds(15))
                        throw new TimeoutException();
                    status = $"transcribing; queue wait {(DateTimeOffset.UtcNow - segment.Queued).TotalSeconds:F1}s";
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    request.CancelAfter(TimeSpan.FromSeconds(15));
                    string text = await transcriber.TranscribeAsync(segment.Pcm, request.Token);
                    token.ThrowIfCancellationRequested();
                    lock (orderGate)
                    {
                        token.ThrowIfCancellationRequested();
                        completed.Add(segment.Sequence, (text, segment.Queued));
                        while (completed.Remove(nextPublication, out var result))
                        {
                            if (DateTimeOffset.UtcNow - result.Queued > TimeSpan.FromSeconds(15))
                                throw new TimeoutException();
                            status = $"last transcript {(DateTimeOffset.UtcNow - result.Queued).TotalSeconds:F1}s after segment";
                            if (!string.IsNullOrWhiteSpace(result.Text))
                                Transcribed?.Invoke(result.Text);
                            nextPublication++;
                            publicationSlots.Release();
                        }
                    }
                }
                finally { Array.Clear(segment.Pcm); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                   or TimeoutException or InvalidDataException or JsonException or IOException)
        {
            Fail(ex switch
            {
                HttpRequestException http => http.Message,
                OperationCanceledException or TimeoutException =>
                    "MAI transcription exceeded the 15-second live latency limit. Check connectivity/service latency or select Azure streaming; capture stopped.",
                _ => "MAI returned invalid transcription data. Capture stopped; verify model availability and resume."
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (worker is not null) await worker;
        else while (queue.Reader.TryRead(out var pending)) Array.Clear(pending.Pcm);
        publicationSlots.Dispose();
        stop.Dispose();
    }
}
