namespace MeetingCopilot.Core;

public sealed class PrompterPlayback
{
    public double Position { get; private set; }
    public double End { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsAtEnd => Position >= End;
    public double PixelsPerSecond { get; set; } = 32;

    public void SetExtent(double end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        if (!double.IsFinite(end)) throw new ArgumentOutOfRangeException(nameof(end));
        End = end;
        Position = Math.Min(Position, end);
        if (IsAtEnd) Pause();
    }

    public void Play()
    {
        if (!IsAtEnd) IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Seek(double position)
    {
        if (!double.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        Pause();
        Position = Math.Clamp(position, 0, End);
    }

    public void Advance(TimeSpan elapsed)
    {
        if (!IsPlaying) return;
        if (!double.IsFinite(PixelsPerSecond) || PixelsPerSecond is < 5 or > 120)
            throw new InvalidOperationException("Choose a scrolling speed between 5 and 120 pixels per second.");
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        // A stalled UI or machine waking from sleep must not skip unread text.
        if (elapsed > TimeSpan.FromSeconds(1)) { Pause(); return; }
        Position = Math.Min(End, Position + PixelsPerSecond * elapsed.TotalSeconds);
        if (IsAtEnd) Pause();
    }
}
