using MeetingCopilot.Desktop;
using NAudio.Wave;
using Xunit;

namespace MeetingCopilot.Tests;

public class AudioTests
{
    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 2)]
    [InlineData(16000, 1)]
    public void ConvertsStreamingAudioTo16KhzMonoWithoutGaps(int rate, int channels)
    {
        var converter = new PcmAudioConverter(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        var samples = new List<short>();
        int frame = 0;
        for (int chunk = 0; chunk < 200; chunk++)
        {
            int frames = rate / 100;
            var bytes = new byte[frames * channels * 4];
            for (int i = 0; i < frames; i++, frame++)
            {
                float value = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * frame / rate));
                for (int ch = 0; ch < channels; ch++)
                    BitConverter.TryWriteBytes(bytes.AsSpan((i * channels + ch) * 4, 4), value);
            }
            converter.Push(bytes, bytes.Length, (output, count) =>
            {
                Assert.Equal(0, count % 2);
                for (int i = 0; i < count; i += 2)
                    samples.Add(BitConverter.ToInt16(output, i));
            });
        }
        Assert.InRange(samples.Count, 27_000, 32_000);
        double rms = Math.Sqrt(samples.Average(s => (double)s * s));
        Assert.InRange(rms, 5300, 6200);
        int crossings = samples.Zip(samples.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        double frequency = crossings * 16000.0 / samples.Count;
        Assert.InRange(frequency, 435, 445);
        converter.Flush((output, count) =>
        {
            for (int i = 0; i < count; i += 2)
                samples.Add(BitConverter.ToInt16(output, i));
        });
        Assert.InRange(samples.Count, 31800, 32000);
        int total = samples.Count;
        converter.Flush((_, count) => Assert.Equal(0, count));
        Assert.Equal(total, samples.Count);
    }

    [Fact]
    public void FlushEmitsTrailingAudioOnceWithoutPaddingAndAllowsFurtherInput()
    {
        var converter = new PcmAudioConverter(new WaveFormat(16000, 16, 1));
        var bytes = new byte[1600];
        for (int i = 0; i < bytes.Length; i += 2)
            BitConverter.TryWriteBytes(bytes.AsSpan(i, 2), (short)4000);
        var result = new List<byte>();
        void Consume(byte[] output, int count) => result.AddRange(output.Take(count));
        converter.Push(bytes, bytes.Length, Consume);
        Assert.Empty(result);
        converter.Flush(Consume);
        Assert.Equal(bytes.Length, result.Count);
        // PCM -> float -> PCM can round by one quantization step.
        for (int i = 0; i < result.Count; i += 2)
            Assert.InRange(BitConverter.ToInt16(result.ToArray(), i), (short)3999, (short)4000);
        converter.Flush(Consume);
        Assert.Equal(bytes.Length, result.Count);
        converter.Push(bytes, bytes.Length, Consume);
        converter.Flush(Consume);
        Assert.Equal(bytes.Length * 2, result.Count);
        for (int i = 0; i < result.Count; i += 2)
            Assert.InRange(BitConverter.ToInt16(result.ToArray(), i), (short)3999, (short)4000);
    }
}
