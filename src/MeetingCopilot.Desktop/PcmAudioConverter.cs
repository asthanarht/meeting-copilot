using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingCopilot.Desktop;

public sealed class PcmAudioConverter
{
    private readonly BufferedWaveProvider input;
    private readonly IWaveProvider output;
    private readonly byte[] converted = new byte[3200];
    private readonly int threshold;

    public PcmAudioConverter(WaveFormat format)
    {
        input = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };
        ISampleProvider samples = input.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1)
            samples = new MonoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != 16000)
            samples = new WdlResamplingSampleProvider(samples, 16000);
        output = new SampleToWaveProvider16(samples);
        threshold = format.AverageBytesPerSecond / 5;
    }

    public void Push(byte[] bytes, int count, Action<byte[], int> consume)
    {
        input.AddSamples(bytes, 0, count);
        // Leave one chunk of lookahead for the streaming resampler; never pad a partial read.
        while (input.BufferedBytes >= threshold)
        {
            var read = output.Read(converted, 0, converted.Length);
            if (read == 0)
                throw new InvalidOperationException("Audio conversion stalled with buffered input.");
            consume(converted, read);
        }
    }

    public void Flush(Action<byte[], int> consume)
    {
        int read;
        while ((read = output.Read(converted, 0, converted.Length)) > 0)
            consume(converted, read);
    }

    private sealed class MonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] buffer = [];
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] destination, int offset, int count)
        {
            int channels = source.WaveFormat.Channels;
            if (buffer.Length < count * channels)
                buffer = new float[count * channels];
            int read = source.Read(buffer, 0, count * channels);
            for (int frame = 0; frame < read / channels; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                    sum += buffer[frame * channels + channel];
                destination[offset + frame] = sum / channels;
            }
            return read / channels;
        }
    }
}
