using MeetingCopilot.Core;
using Xunit;

namespace MeetingCopilot.Tests;

public class QuestionHistoryTests
{
    private static Cue Cue(string text = "What happens if storage is unavailable?") =>
        new("question", text, "Storage was discussed.", "May already be handled.", [1]);

    [Fact]
    public void FreshnessUpdatesDoNotDuplicateTheSameQuestion()
    {
        var history = new QuestionHistory();
        var now = DateTimeOffset.UtcNow;
        history.Observe(new(Cue(), "Ready"), now);
        history.Observe(new(Cue(), "Rechecking", Provisional: true), now.AddSeconds(2));
        var entry = Assert.Single(history.Entries);
        Assert.Equal(now, entry.CreatedAt);
        Assert.Equal(CueState.Provisional, entry.State);
        history.Observe(new(Cue(), "Confirmed"), now.AddSeconds(4));
        Assert.Equal(CueState.Live, history.Entries[0].State);
    }

    [Theory]
    [InlineData(CueState.Addressed)]
    [InlineData(CueState.Expired)]
    [InlineData(CueState.Paused)]
    [InlineData(CueState.Unavailable)]
    public void RetiredQuestionsKeepTheirEvidenceAndStatus(CueState state)
    {
        var history = new QuestionHistory();
        var evidence = new Utterance(1, DateTimeOffset.UtcNow, "Slide", "Files are stored for 30 days.", "slide");
        history.Observe(new(Cue(), "Ready", Evidence: [evidence]), DateTimeOffset.UtcNow);
        history.Observe(new(null, "Retired", RetiredState: state), DateTimeOffset.UtcNow);
        Assert.Equal(state, history.Entries[0].State);
        Assert.Equal(evidence, Assert.Single(history.Entries[0].Evidence));
    }

    [Fact]
    public void AllDistinctQuestionsRemainNavigableAndCanBeClearedAtStop()
    {
        var history = new QuestionHistory();
        for (int i = 0; i < 200; i++)
            history.Observe(new(Cue($"Question {i}?"), "Ready"), DateTimeOffset.UtcNow);
        Assert.Equal(200, history.Entries.Count);
        Assert.Equal(CueState.Superseded, history.Entries[0].State);
        history.Mark(1, CueState.Covered);
        Assert.Equal(CueState.Covered, history.Entries[0].State);
        Assert.Equal(CueState.Live, history.Entries[^1].State);
        history.Clear();
        Assert.Empty(history.Entries);
    }
}
