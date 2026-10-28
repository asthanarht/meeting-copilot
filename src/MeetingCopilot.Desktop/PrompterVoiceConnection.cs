using NAudio.CoreAudioApi;

namespace MeetingCopilot.Desktop;

internal sealed record PrompterVoiceConnection(string MicrophoneName, Func<ISpeechAudioInput> CreateInput)
{
    internal static PrompterVoiceConnection FromSavedSettings()
    {
        var saved = new SettingsStore().Load()
            ?? throw new InvalidOperationException("Save an Azure Speech connection and microphone in setup first.");
        StreamingSpeechConfiguration.Validate(saved.SpeechKey, saved.SpeechRegion, saved.SpeechEndpoint);
        var microphone = AudioDevices.List(DataFlow.Capture).SingleOrDefault(d => d.Id == saved.MicrophoneDeviceId)
            ?? throw new InvalidOperationException("The saved microphone is unavailable. Select a microphone in setup > Meeting, save, then refresh this connection.");
        return new(microphone.Name, () => new SpeechAudioInput(saved.SpeechKey, saved.SpeechRegion, saved.Locale,
            microphone.Id, loopback: false, phrases: [], saved.SpeechEndpoint));
    }
}
