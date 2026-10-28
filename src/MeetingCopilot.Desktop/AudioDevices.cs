using NAudio.CoreAudioApi;

namespace MeetingCopilot.Desktop;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static AudioDevice[] List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var result = new List<AudioDevice>();
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
                result.Add(new(endpoint.ID, endpoint.FriendlyName));
        }
        return [.. result];
    }
}
