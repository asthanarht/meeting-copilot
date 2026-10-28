using NAudio.Wave;

namespace MeetingCopilot.Desktop;

internal sealed class StreamingPcmFeed(WaveFormat format, Action<byte[], int> write, TimeProvider? timeProvider = null)
{
    private readonly PcmAudioConverter converter = new(format);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly byte[] silence = new byte[3200];
    private long lastInput = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private long? lastSilence;

    public void Push(byte[] bytes, int count)
    {
        if (count == 0) return;
        converter.Push(bytes, count, write);
        lastInput = clock.GetTimestamp();
        lastSilence = null;
    }

    public void PumpIdle()
    {
        long now = clock.GetTimestamp();
        if (clock.GetElapsedTime(lastInput, now) < TimeSpan.FromMilliseconds(200) ||
            (lastSilence is { } last && clock.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(100)))
            return;
        // WASAPI loopback stops sending packets in silence. Flush the last speech samples
        // and supply silence so recognition can finalize without waiting for more speech.
        converter.Flush(write);
        write(silence, silence.Length);
        lastSilence = now;
    }
}
