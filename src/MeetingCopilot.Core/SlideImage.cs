using System.Buffers.Binary;

namespace MeetingCopilot.Core;

public static class SlideImage
{
    public const int MaxDimension = 1600;
    public const int MaxBytes = 6_000_000;

    public static void Validate(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 33 || png.Length > MaxBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new ArgumentException("The slide must be a PNG image no larger than 6 MB.", nameof(png));
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension)
            throw new ArgumentException("The slide dimensions must be between 1 and 1600 pixels.", nameof(png));
    }
}
