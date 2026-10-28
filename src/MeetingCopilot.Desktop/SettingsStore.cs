using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

internal sealed record ExternalModelProfile
{
    public string Endpoint { get; init; } = "";
    public string Model { get; init; } = "";
    public string Key { get; init; } = "";
    public CompatibleModelOptions Options { get; init; } = new();

    public static ExternalModelProfile Default(ModelConnection connection) => new()
    {
        Endpoint = connection switch
        {
            ModelConnection.Gemini => "https://generativelanguage.googleapis.com/v1beta",
            ModelConnection.Anthropic => "https://api.anthropic.com/v1",
            ModelConnection.Grok => "https://api.x.ai/v1",
            _ => "https://api.openai.com/v1"
        }
    };
}

internal sealed record ExternalModelProfiles(
    ExternalModelProfile? OpenAICompatible = null, ExternalModelProfile? Gemini = null,
    ExternalModelProfile? Anthropic = null, ExternalModelProfile? Grok = null)
{
    public ExternalModelProfile? Get(ModelConnection connection) => connection switch
    {
        ModelConnection.OpenAICompatible => OpenAICompatible,
        ModelConnection.Gemini => Gemini,
        ModelConnection.Anthropic => Anthropic,
        ModelConnection.Grok => Grok,
        _ => null
    };

    public ExternalModelProfiles With(ModelConnection connection, ExternalModelProfile profile) => connection switch
    {
        ModelConnection.OpenAICompatible => this with { OpenAICompatible = profile },
        ModelConnection.Gemini => this with { Gemini = profile },
        ModelConnection.Anthropic => this with { Anthropic = profile },
        ModelConnection.Grok => this with { Grok = profile },
        _ => throw new ArgumentException("Select an external provider.")
    };

    public ExternalModelProfiles MapKeys(Func<string, string> transform)
    {
        ExternalModelProfile? Map(ExternalModelProfile? profile) => profile is null ? null : profile with { Key = transform(profile.Key) };
        return new(Map(OpenAICompatible), Map(Gemini), Map(Anthropic), Map(Grok));
    }
}

internal sealed record ConnectionSettings
{
    public int Version { get; init; } = 1;
    public SpeechProvider SpeechProvider { get; init; } = SpeechProvider.MaiTranscribe2;
    public string SpeechEndpoint { get; init; } = "";
    public string SpeechRegion { get; init; } = "";
    public string SpeechKey { get; init; } = "";
    public string Locale { get; init; } = "en-US";
    public ModelConnection ModelConnection { get; init; } = ModelConnection.ResponsesEntra;
    public string ModelEndpoint { get; init; } = "";
    public string Deployment { get; init; } = "";
    public string ModelKey { get; init; } = "";
    public string TenantId { get; init; } = "";
    public ExternalModelProfiles ExternalModels { get; init; } = new();
    public string PlaybackDeviceId { get; init; } = "";
    public string MicrophoneDeviceId { get; init; } = "";
    public bool IncludeMicrophone { get; init; }
    public bool ExcludeOverlayFromCapture { get; init; } = true;
}

internal sealed class SettingsStore
{
    private static readonly byte[] Entropy = "MeetingCopilot.Settings.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;

    public SettingsStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingCopilot", "settings.json");
    }

    public ConnectionSettings? Load()
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 128_000)
                throw new InvalidDataException("Saved settings are too large.");
            bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var saved = JsonSerializer.Deserialize<ConnectionSettings>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Saved settings are empty.");
        Validate(saved);
        return saved with
        {
            SpeechKey = Unprotect(saved.SpeechKey), ModelKey = Unprotect(saved.ModelKey),
            ExternalModels = saved.ExternalModels.MapKeys(Unprotect)
        };
    }

    public void Save(ConnectionSettings settings)
    {
        Validate(settings);
        var encrypted = settings with
        {
            SpeechKey = Protect(settings.SpeechKey),
            ModelKey = Protect(settings.ModelKey),
            ExternalModels = settings.ExternalModels.MapKeys(Protect)
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(encrypted, JsonOptions);
        if (json.Length > 128_000)
            throw new InvalidDataException("Configuration exceeded the safe settings-file size.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Delete()
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // Nothing has been saved yet.
    }

    private static void Validate(ConnectionSettings settings)
    {
        if (settings.Version != 1 || !Enum.IsDefined(settings.SpeechProvider) ||
            !Enum.IsDefined(settings.ModelConnection) ||
            settings.Locale is not ("en-US" or "en-GB" or "en-IN" or "en-AU"))
            throw new InvalidDataException("Saved settings use an unsupported format or selection.");
        string[] fields = [settings.SpeechEndpoint, settings.SpeechRegion, settings.SpeechKey,
            settings.ModelEndpoint, settings.Deployment, settings.ModelKey, settings.TenantId,
            settings.PlaybackDeviceId, settings.MicrophoneDeviceId];
        if (fields.Any(value => value is null || value.Length > 16_000))
            throw new InvalidDataException("Saved settings contain an invalid field.");
        if (settings.ExternalModels is null)
            throw new InvalidDataException("Saved external model profiles are invalid.");
        foreach (var connection in new[] { ModelConnection.OpenAICompatible, ModelConnection.Gemini, ModelConnection.Anthropic, ModelConnection.Grok })
        {
            var profile = settings.ExternalModels.Get(connection);
            if (profile is null) continue;
            if (profile.Options is null ||
                new[] { profile.Endpoint, profile.Model, profile.Key }.Any(value => value is null || value.Length > 16_000))
                throw new InvalidDataException("Saved external model profile contains an invalid field.");
            try { profile.Options.Validate(); }
            catch (ArgumentException) { throw new InvalidDataException("Saved external model options are invalid."); }
        }
    }

    private static string Protect(string value)
    {
        if (value.Length == 0) return "";
        byte[] plain = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
