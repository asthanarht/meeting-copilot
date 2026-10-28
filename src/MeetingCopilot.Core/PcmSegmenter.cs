namespace MeetingCopilot.Core;

/// <summary>Splits PCM16 at quiet boundaries when possible, without discarding quiet samples.</summary>
public sealed class PcmSegmenter
{
    public const int MaxBytes = 16000 * 2 * 6;
    private readonly byte[] buffer = new byte[MaxBytes];
    private int length;
    private int quietSamples;

    public void Push(ReadOnlySpan<byte> pcm, Action<byte[]> emit)
    {
        if (pcm.Length % 2 != 0)
            throw new ArgumentException("PCM16 data must contain complete samples.");
        for (int i = 0; i < pcm.Length; i += 2)
        {
            buffer[length++] = pcm[i];
            buffer[length++] = pcm[i + 1];
            short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            quietSamples = Math.Abs((int)sample) < 160 ? quietSamples + 1 : 0;
            if (length == MaxBytes || (length >= 64_000 && quietSamples >= 11_200))
                Flush(emit);
        }
    }

    public void Flush(Action<byte[]> emit)
    {
        if (length == 0) return;
        var chunk = buffer.AsSpan(0, length).ToArray();
        Clear();
        emit(chunk);
    }

    public void Clear()
    {
        buffer.AsSpan(0, length).Clear();
        length = 0;
        quietSamples = 0;
    }
}
