using MeetingCopilot.Core;
using Xunit;

namespace MeetingCopilot.Tests;

public class ScriptSpeechMatcherTests
{
    private static ScriptSpeechMatcher Create(string script) =>
        new(ScriptSpeechMatcher.Tokenize(script).Select(w => w.Text));

    [Fact]
    public void PartialWordsAdvanceWithoutWaitingForTheFinalAndNeverUseATimer()
    {
        var matcher = Create("Today we will review the archive design.");
        Assert.Equal(0, matcher.Observe("today", false).WordIndex);
        Assert.Equal(2, matcher.Observe("today we will", false).WordIndex);
        Assert.Equal(2, matcher.Observe("today we will", false).WordIndex);
        Assert.Equal(3, matcher.Observe("today we will review", true).WordIndex);
        Assert.Equal(4, matcher.Observe("the", false).WordIndex);
        Assert.Equal(6, matcher.Observe("the archive design", true).WordIndex);
        Assert.True(matcher.AtEnd);
    }

    [Fact]
    public void FinalAndDuplicateHypothesesDoNotReadTheSameWordsTwice()
    {
        var matcher = Create("We review the design. We review the design again.");
        Assert.Equal(3, matcher.Observe("we review the design", false).WordIndex);
        Assert.Equal(3, matcher.Observe("we review the design", true).WordIndex);
        Assert.Equal(3, matcher.Observe("we review the design", true).WordIndex);
        Assert.Equal(8, matcher.Observe("we review the design again", true).WordIndex);
    }

    [Fact]
    public void RepeatedPhraseCanFinishAfterNewInterimProgressWithoutRepeatingADuplicateFinal()
    {
        var matcher = Create("We review the design. We review the design.");
        Assert.Equal(3, matcher.Observe("we review the design", true).WordIndex);
        Assert.False(matcher.Observe("we review the design", true).Matched);
        Assert.Equal(7, matcher.Observe("we review the design", false).WordIndex);
        var final = matcher.Observe("we review the design", true);
        Assert.True(final.Matched);
        Assert.True(final.AtEnd);
        Assert.False(matcher.Observe("we review the design", true).Matched);
    }

    [Fact]
    public void RecognitionCorrectionsDoNotPushTheReaderBackward()
    {
        var matcher = Create("We review the storage design and its failure modes.");
        Assert.Equal(3, matcher.Observe("we review the storage", false).WordIndex);
        Assert.Equal(3, matcher.Observe("we review", false).WordIndex);
        Assert.Equal(4, matcher.Observe("we review the storage design", true).WordIndex);
    }

    [Fact]
    public void FillersAndASmallSkippedWordStillFindTheNearbyScript()
    {
        var matcher = Create("Today we will review the archive design.");
        var result = matcher.Observe("today um we review the archive design", true);
        Assert.True(result.Matched);
        Assert.Equal(6, result.WordIndex);
    }

    [Fact]
    public void OffScriptSpeechAndUnmatchedTrailingWordsHoldPosition()
    {
        var matcher = Create("We review the design and then discuss recovery.");
        matcher.Observe("we review", true);
        var result = matcher.Observe("what time is lunch", true);
        Assert.False(result.Matched);
        Assert.Equal(1, result.WordIndex);
        result = matcher.Observe("the weather is pleasant outside", false);
        Assert.False(result.Matched);
        Assert.Equal(1, result.WordIndex);
        Assert.Equal(3, matcher.Observe("the design", true).WordIndex);
    }

    [Fact]
    public void AnIsolatedCommonWordCannotJumpAhead()
    {
        var matcher = Create("Welcome everyone. We will review the archive and the retry rules.");
        Assert.False(matcher.Observe("the", false).Matched);
        Assert.Equal(-1, matcher.CurrentWord);
        Assert.True(matcher.Observe("welcome", false).Matched);
    }

    [Fact]
    public void DistantTextNeedsAnExplicitReadingAnchor()
    {
        string script = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"topic{i}")) + " We discuss recovery.";
        var matcher = Create(script);
        Assert.False(matcher.Observe("we discuss recovery", true).Matched);
        matcher.Reset(60);
        Assert.Equal(62, matcher.Observe("we discuss recovery", true).WordIndex);
        Assert.True(matcher.AtEnd);
        matcher.Reset(0);
        Assert.False(matcher.AtEnd);
        Assert.Equal(-1, matcher.CurrentWord);
    }

    [Fact]
    public void PunctuationCasingAndApostrophesDoNotBreakWordLocations()
    {
        const string text = "We’re READY: let's review café storage!";
        var tokens = ScriptSpeechMatcher.Tokenize(text);
        Assert.Equal("We’re", tokens[0].Text);
        Assert.All(tokens, token => Assert.Equal(token.Text, text.Substring(token.Start, token.Length)));
        var matcher = Create(text);
        Assert.True(matcher.Observe("we're ready let's review café storage", true).AtEnd);
    }

    [Fact]
    public void EmptySpeechAndEmptyScriptsCannotInventProgress()
    {
        var matcher = Create("A short presentation.");
        Assert.False(matcher.Observe("", false).Matched);
        Assert.Equal(-1, matcher.CurrentWord);
        Assert.False(Create("...").Observe("hello", true).Matched);
        Assert.Throws<ArgumentOutOfRangeException>(() => matcher.Reset(99));
    }
}
