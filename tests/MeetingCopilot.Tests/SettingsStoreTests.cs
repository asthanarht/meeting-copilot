using System.Security.Cryptography;
using System.Text.Json;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MeetingCopilot-settings-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");
    private SettingsStore Store => new(FilePath);
    private static ConnectionSettings Example => new()
    {
        SpeechEndpoint = "https://test.cognitiveservices.azure.com/",
        SpeechKey = "synthetic-speech-secret",
        ModelEndpoint = "https://test.services.ai.azure.com/openai/v1",
        ModelKey = "synthetic-model-secret",
        Deployment = "test-deployment",
        TenantId = "00000000-0000-0000-0000-000000000001",
        PlaybackDeviceId = "test-playback",
        MicrophoneDeviceId = "test-microphone",
        IncludeMicrophone = true
    };

    [Fact]
    public void MissingConfigurationReturnsNoSavedValues() => Assert.Null(Store.Load());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureExclusionPreferenceSurvivesReload(bool enabled)
    {
        Store.Save(Example with { ExcludeOverlayFromCapture = enabled });
        Assert.Equal(enabled, Store.Load()!.ExcludeOverlayFromCapture);
    }

    [Fact]
    public void OlderSettingsDefaultToRequestingCaptureExclusion()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, """{"Version":1}""");
        Assert.True(Store.Load()!.ExcludeOverlayFromCapture);
    }

    [Theory]
    [InlineData(ModelConnection.ChatCompletionsApiKey)]
    [InlineData(ModelConnection.ResponsesEntra)]
    [InlineData(ModelConnection.ResponsesApiKey)]
    public void RoundTripEncryptsBothKeysAndRestoresEveryField(ModelConnection connection)
    {
        var example = Example with { ModelConnection = connection };
        Store.Save(example);
        string json = File.ReadAllText(FilePath);
        Assert.DoesNotContain(Example.SpeechKey, json);
        Assert.DoesNotContain(Example.ModelKey, json);
        Assert.Contains(Example.ModelEndpoint, json);
        Assert.Equal(example, Store.Load());
        using var document = JsonDocument.Parse(json);
        Assert.NotEqual(Example.SpeechKey, document.RootElement.GetProperty("SpeechKey").GetString());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void ClearingKeysDoesNotRestorePreviousSecrets()
    {
        Store.Save(Example);
        Store.Save(Example with { SpeechKey = "", ModelKey = "", Deployment = "new-deployment" });
        var restored = Store.Load()!;
        Assert.Equal("", restored.SpeechKey);
        Assert.Equal("", restored.ModelKey);
        Assert.Equal("new-deployment", restored.Deployment);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void EveryProviderHasAnIndependentlyEncryptedProfileWithoutChangingAzureValues()
    {
        var profiles = new ExternalModelProfiles();
        foreach (var provider in new[] { ModelConnection.OpenAICompatible, ModelConnection.Gemini, ModelConnection.Anthropic, ModelConnection.Grok })
            profiles = profiles.With(provider, ExternalModelProfile.Default(provider) with
            {
                Model = provider + "-model", Key = provider + "-synthetic-secret",
                Options = new(TimeoutSeconds: 90, ContextCharacters: 48_000)
            });
        var expected = Example with { ModelConnection = ModelConnection.Gemini, ExternalModels = profiles };
        Store.Save(expected);
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("synthetic-secret", text);
        Assert.DoesNotContain(Example.ModelKey, text);
        Assert.Equal(expected, Store.Load());
        Store.Save(expected with { ExternalModels = profiles.With(ModelConnection.Gemini, profiles.Gemini! with { Key = "" }) });
        var restored = Store.Load()!;
        Assert.Equal("", restored.ExternalModels.Gemini!.Key);
        Assert.Equal(profiles.Anthropic, restored.ExternalModels.Anthropic);
        Assert.Equal(Example.ModelKey, restored.ModelKey);
    }

    [Theory]
    [InlineData("""{"Version":1,"ExternalModels":null}""")]
    [InlineData("""{"Version":1,"ExternalModels":{"Gemini":{"Options":null}}}""")]
    [InlineData("""{"Version":1,"ExternalModels":{"Grok":{"Key":null}}}""")]
    [InlineData("""{"Version":1,"ExternalModels":{"Anthropic":{"Options":{"TimeoutSeconds":0}}}}""")]
    public void MalformedProfilesFailWithoutOverwriting(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, json);
        Assert.Throws<InvalidDataException>(() => Store.Load());
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void NativeProviderKeysCannotBeLoadedAsPlaintext()
    {
        Directory.CreateDirectory(directory);
        var saved = new ConnectionSettings { ExternalModels = new(Gemini: new() { Key = "not-base64!" }) };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        Assert.Throws<FormatException>(() => Store.Load());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{invalid json")]
    [InlineData("""{"Version":99}""")]
    [InlineData("""{"Version":1,"SpeechProvider":999}""")]
    [InlineData("""{"Version":1,"ModelConnection":999}""")]
    [InlineData("""{"Version":1,"Locale":"unsupported"}""")]
    [InlineData("""{"Version":1,"SpeechKey":null}""")]
    public void CorruptOrUnsupportedFileFailsExplicitlyWithoutOverwriting(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, json);
        var error = Record.Exception(() => Store.Load());
        Assert.True(error is JsonException or InvalidDataException);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void NonDpapiSecretIsNotLoadedAsPlainText()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Example with { SpeechKey = "not-base64!" }));
        Assert.Throws<FormatException>(() => Store.Load());
    }

    [Fact]
    public void TamperedCiphertextFailsWithoutSilentlyClearingKeys()
    {
        Store.Save(Example);
        var disk = JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(FilePath))!;
        byte[] damaged = Convert.FromBase64String(disk.SpeechKey);
        damaged[^1] ^= 255;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(disk with { SpeechKey = Convert.ToBase64String(damaged) }));
        Assert.Throws<CryptographicException>(() => Store.Load());
    }

    [Fact]
    public void FailedReplacementKeepsPreviousConfigAndRemovesTemporaryFile()
    {
        Store.Save(Example);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => Store.Save(Example with { Deployment = "must-not-replace" }));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(Example, Store.Load());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void DeleteRemovesOnlySettingsAndCanBeRepeated()
    {
        Store.Save(Example);
        string unrelated = Path.Combine(directory, "keep.txt");
        File.WriteAllText(unrelated, "not settings");
        Store.Delete();
        Store.Delete();
        Assert.Null(Store.Load());
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.GetFiles(directory))
            File.Delete(file);
        Directory.Delete(directory);
    }
}
