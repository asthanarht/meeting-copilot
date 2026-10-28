namespace MeetingCopilot.Desktop;

internal interface ISpeechAudioInput : IAsyncDisposable
{
    string Source { get; }
    DateTimeOffset? LastPacket { get; }
    string? ProcessingStatus => null;
    event Action<string, string, bool>? Transcript;
    event Action<string>? Faulted;
    Task StartAsync(CancellationToken cancellationToken);
    void StopCapture();
}
