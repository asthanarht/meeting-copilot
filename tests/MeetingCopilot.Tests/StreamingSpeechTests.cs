using MeetingCopilot.Desktop;
using Microsoft.CognitiveServices.Speech;
using NAudio.Wave;
using Xunit;

namespace MeetingCopilot.Tests;

public class StreamingSpeechTests
{
    [Fact]
    public void ResourceEndpointWorksWithoutKnowingTheRegion()
    {
        var config = StreamingSpeechConfiguration.Create("synthetic-key", "", "en-US",
            "https://example.cognitiveservices.azure.com/");
        Assert.Equal("wss://example.cognitiveservices.azure.com/",
            config.GetProperty(PropertyId.SpeechServiceConnection_Endpoint));
        Assert.Equal("en-US", config.SpeechRecognitionLanguage);
        Assert.Equal("650", config.GetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs));
        Assert.Equal("Time", config.GetProperty("Speech_SegmentationStrategy"));
        Assert.Equal("10000", config.GetProperty("Speech_SegmentationMaximumTimeMs"));
    }

    [Fact]
    public void ExplicitRegionPreservesExistingRegionalConnections()
    {
        var config = StreamingSpeechConfiguration.Create("synthetic-key", "eastus", "en-IN", "");
        Assert.Equal("eastus", config.GetProperty(PropertyId.SpeechServiceConnection_Region));
        Assert.Equal("en-IN", config.SpeechRecognitionLanguage);
    }

    [Theory]
    [InlineData("http://example.cognitiveservices.azure.com/")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.cognitiveservices.azure.com/path")]
    [InlineData("https://example.cognitiveservices.azure.com/?secret=x")]
    [InlineData("https://example.cognitiveservices.azure.com:8443/")]
    public void StreamingRejectsUnsafeEndpointDestinations(string endpoint) =>
        Assert.Throws<ArgumentException>(() => StreamingSpeechConfiguration.Validate("synthetic-key", "", endpoint));

    [Fact]
    public void MissingKeysAndInvalidRegionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => StreamingSpeechConfiguration.Validate("", "eastus", ""));
        Assert.Throws<ArgumentException>(() => StreamingSpeechConfiguration.Validate("synthetic-key", "../invalid", ""));
    }

    [Fact]
    public void IdleLoopbackFlushesTrailingSpeechBeforeAddingSilenceAndCanResume()
    {
        var clock = new TestClock();
        var output = new List<byte[]>();
        var feed = new StreamingPcmFeed(new WaveFormat(16000, 16, 1),
            (data, count) => output.Add(data[..count]), clock);
        var speech = new byte[1600];
        for (int i = 0; i < speech.Length; i += 2)
            BitConverter.TryWriteBytes(speech.AsSpan(i, 2), (short)4000);
        feed.Push(speech, speech.Length);
        clock.Advance(199);
        feed.PumpIdle();
        Assert.Empty(output);
        clock.Advance(1);
        feed.PumpIdle();
        Assert.Equal(2, output.Count);
        Assert.Equal(speech.Length, output[0].Length);
        for (int i = 0; i < speech.Length; i += 2)
            Assert.InRange(BitConverter.ToInt16(output[0], i), (short)3999, (short)4000);
        Assert.All(output[1], value => Assert.Equal(0, value));
        feed.PumpIdle();
        Assert.Equal(2, output.Count);
        clock.Advance(100);
        feed.PumpIdle();
        Assert.Equal(3, output.Count);
        Assert.Equal(3200, output[2].Length);
        feed.Push(speech, speech.Length);
        feed.PumpIdle();
        Assert.Equal(3, output.Count);
        clock.Advance(200);
        feed.PumpIdle();
        Assert.Equal(5, output.Count);
        Assert.Equal(speech.Length, output[3].Length);
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(int milliseconds) => timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
