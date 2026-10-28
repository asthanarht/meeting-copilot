using System.IO;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public sealed class PrompterTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"meeting-script-tests-{Guid.NewGuid():N}.json");
    private ScriptStore Store => new(path);
    private static PresentationScript Example(string title = "Invented presentation") => new()
    {
        Title = title,
        Text = "Welcome to our synthetic rehearsal.\n\nToday we will explain a made-up archive design."
    };

    [Fact]
    public void ScriptsAndOptionsPersistEncryptedAndDeleteIndependently()
    {
        Assert.Empty(Store.Load());
        var one = Store.Save(Example() with { Options = new() { Speed = 47, FontSize = 38, LineSpacing = 1.8, ReadingWidth = 440 } });
        var two = Store.Save(Example("Second presentation"));
        Assert.NotEqual(Guid.Empty, one.Revision);
        string file = File.ReadAllText(path);
        Assert.DoesNotContain("Invented presentation", file);
        Assert.DoesNotContain("synthetic rehearsal", file);
        Assert.Equal(one, Assert.Single(Store.Load(), s => s.Id == one.Id));
        Store.Delete(one);
        Assert.Equal(two, Assert.Single(Store.Load()));
        Store.Delete(two);
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void StaleWritesAndDeletesCannotDestroyAnotherWindowsEdits()
    {
        var original = Store.Save(Example());
        var updated = Store.Save(original with { Text = "New synthetic version." });
        Assert.Throws<IOException>(() => Store.Save(original with { Text = "Stale draft." }));
        Assert.Throws<IOException>(() => Store.Delete(original));
        Assert.Equal(updated, Assert.Single(Store.Load()));
        Store.Delete(updated);
        Assert.Throws<IOException>(() => Store.Save(updated));
    }

    [Fact]
    public void SavingAnotherScriptMergesRatherThanReplacingTheLibrary()
    {
        var first = Store.Save(Example("First"));
        var second = Store.Save(Example("Second"));
        var changed = Store.Save(first with { Title = "Updated first" });
        Assert.Equal(2, Store.Load().Count);
        Assert.Contains(changed, Store.Load());
        Assert.Contains(second, Store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Version\":9,\"Data\":\"\"}")]
    [InlineData("{\"Version\":1,\"Data\":\"invalid-base64\"}")]
    public void CorruptedLibrariesAreNeverOverwritten(string content)
    {
        File.WriteAllText(path, content);
        Assert.ThrowsAny<Exception>(() => Store.Load());
        Assert.ThrowsAny<Exception>(() => Store.Save(Example()));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void LockedLibraryAndInvalidDraftsLeaveExistingContentIntact()
    {
        var original = Store.Save(Example());
        using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => Store.Save(original with { Title = "Conflict" }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Title = " " }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Title = new string('x', 121) }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Text = new string('x', ScriptStore.MaxTextLength + 1) }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Text = " " }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Options = new() { Speed = 0 } }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Options = new() { FontSize = 61 } }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Options = new() { LineSpacing = double.NaN } }));
        Assert.Throws<InvalidDataException>(() => Store.Save(original with { Options = new() { ReadingWidth = 100 } }));
        Assert.Equal(original, Assert.Single(Store.Load()));
    }

    [Fact]
    public void PlaybackIsTimedPausedByDefaultAndNeverLoops()
    {
        var playback = new PrompterPlayback { PixelsPerSecond = 40 };
        playback.SetExtent(100);
        playback.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(0, playback.Position);
        playback.Play();
        playback.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(20, playback.Position);
        playback.Pause();
        playback.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(20, playback.Position);
        playback.Play();
        playback.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(60, playback.Position);
        playback.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, playback.Position);
        Assert.False(playback.IsPlaying);
        playback.Play();
        Assert.False(playback.IsPlaying);
        playback.Seek(0);
        Assert.False(playback.IsPlaying);
        playback.Play();
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public void FrameRateDoesNotChangeTheReadingSpeed()
    {
        var frequent = new PrompterPlayback();
        var sparse = new PrompterPlayback();
        frequent.SetExtent(1000);
        sparse.SetExtent(1000);
        frequent.Play();
        sparse.Play();
        for (int i = 0; i < 100; i++) frequent.Advance(TimeSpan.FromMilliseconds(10));
        sparse.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(sparse.Position, frequent.Position, 6);
    }

    [Fact]
    public void SleepOrLongStallPausesWithoutSkippingAndSeekingClamps()
    {
        var playback = new PrompterPlayback();
        playback.SetExtent(100);
        playback.Seek(30);
        playback.Play();
        playback.Advance(TimeSpan.FromMinutes(5));
        Assert.False(playback.IsPlaying);
        Assert.Equal(30, playback.Position);
        playback.Seek(200);
        Assert.Equal(100, playback.Position);
        playback.Seek(-10);
        Assert.Equal(0, playback.Position);
        playback.Seek(90);
        playback.SetExtent(50);
        Assert.Equal(50, playback.Position);
        Assert.False(playback.IsPlaying);
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.SetExtent(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Seek(double.PositiveInfinity));
    }

    public void Dispose()
    {
        File.Delete(path);
        File.Delete(path + ".lock");
    }
}
