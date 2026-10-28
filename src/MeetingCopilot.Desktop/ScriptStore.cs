using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeetingCopilot.Desktop;

internal sealed record PrompterOptions
{
    public bool VoiceFollow { get; init; } = true;
    public int Speed { get; init; } = 32;
    public int FontSize { get; init; } = 32;
    public double LineSpacing { get; init; } = 1.5;
    public int ReadingWidth { get; init; } = 600;

    public void Validate()
    {
        if (Speed is < 5 or > 120 || FontSize is < 20 or > 60 ||
            !double.IsFinite(LineSpacing) || LineSpacing is < 1.2 or > 2 ||
            ReadingWidth is < 320 or > 960)
            throw new InvalidDataException("Reading settings are outside supported limits.");
    }
}

internal sealed record PresentationScript
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid Revision { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public PrompterOptions Options { get; init; } = new();
}

internal sealed class ScriptStore
{
    internal const int MaxTextLength = 100_000;
    private const int MaxFileBytes = 16_000_000;
    private static readonly byte[] Entropy = "MeetingCopilot.Scripts.v1"u8.ToArray();
    private readonly string path;
    private sealed record Envelope(int Version, string Data);

    public ScriptStore(string? path = null) =>
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetingCopilot", "scripts.json");

    public IReadOnlyList<PresentationScript> Load()
    {
        byte[] json;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxFileBytes) throw new InvalidDataException("The script library is too large.");
            json = new byte[(int)file.Length];
            file.ReadExactly(json);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        var envelope = JsonSerializer.Deserialize<Envelope>(json);
        if (envelope is not { Version: 1, Data: not null })
            throw new InvalidDataException("The script library format is not supported.");
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Data), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var scripts = JsonSerializer.Deserialize<List<PresentationScript>>(plain)
                ?? throw new InvalidDataException("The script library is empty or damaged.");
            if (scripts.Count > 100 || scripts.Select(s => s?.Id).Distinct().Count() != scripts.Count)
                throw new InvalidDataException("The script library contains invalid entries.");
            foreach (var script in scripts)
            {
                Validate(script);
                if (script.Revision == Guid.Empty) throw new InvalidDataException("A saved script has no revision.");
            }
            return scripts.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public PresentationScript Save(PresentationScript script)
    {
        Validate(script);
        using var lease = AcquireWriteLock();
        var scripts = Load().ToList();
        int index = scripts.FindIndex(s => s.Id == script.Id);
        if (index < 0 && script.Revision != Guid.Empty ||
            index >= 0 && scripts[index].Revision != script.Revision)
            throw new IOException("This script changed in another window. Your draft is intact; copy it before reloading the library.");
        if (index < 0 && scripts.Count >= 100)
            throw new InvalidDataException("The library is full (100 scripts). Delete an unused script first.");
        var saved = script with { Revision = Guid.NewGuid(), Title = script.Title.Trim() };
        if (index < 0) scripts.Add(saved);
        else scripts[index] = saved;
        Write(scripts);
        return saved;
    }

    public void Delete(PresentationScript script)
    {
        using var lease = AcquireWriteLock();
        var scripts = Load().ToList();
        var existing = scripts.SingleOrDefault(s => s.Id == script.Id);
        if (existing is null || existing.Revision != script.Revision)
            throw new IOException("This script changed in another window. Reload the library before deleting it.");
        scripts.Remove(existing);
        Write(scripts);
    }

    private FileStream AcquireWriteLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Write(List<PresentationScript> scripts)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(scripts);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(encrypted)));
        if (json.Length > MaxFileBytes) throw new InvalidDataException("The script library exceeds its 16 MB storage limit.");
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".scripts-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Validate(PresentationScript? script)
    {
        if (script is null || script.Id == Guid.Empty || string.IsNullOrWhiteSpace(script.Title) ||
            script.Title.Length > 120 || string.IsNullOrWhiteSpace(script.Text) ||
            script.Text.Length > MaxTextLength || script.Options is null)
            throw new InvalidDataException("Add a title (up to 120 characters) and presentation text (up to 100,000 characters).");
        script.Options.Validate();
    }

    internal static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or CryptographicException or JsonException or FormatException or System.Security.SecurityException or NotSupportedException;
}
