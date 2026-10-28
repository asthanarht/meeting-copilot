using System.Collections.ObjectModel;

namespace MeetingCopilot.Core;

public sealed record QuestionEntry(long Id, DateTimeOffset CreatedAt, Cue Cue, Utterance[] Evidence, CueState State)
{
    public string Label => $"{CreatedAt.ToLocalTime():HH:mm:ss}  |  {State}  |  {Cue.Text}";
}

// Owned by the overlay dispatcher; retained independently of the bounded model context.
public sealed class QuestionHistory
{
    private readonly ObservableCollection<QuestionEntry> entries = [];
    private int? activeIndex;
    public ReadOnlyObservableCollection<QuestionEntry> Entries { get; }

    public QuestionHistory() => Entries = new(entries);

    public void Observe(AnalysisUpdate update, DateTimeOffset now)
    {
        if (activeIndex is { } index && entries[index].Cue.Text != update.Cue?.Text)
        {
            entries[index] = entries[index] with { State = update.RetiredState };
            activeIndex = null;
        }
        if (update.Cue is not { } cue) return;
        var state = update.Provisional ? CueState.Provisional : CueState.Live;
        if (activeIndex is { } existing)
            entries[existing] = entries[existing] with { Cue = cue, Evidence = update.Evidence ?? entries[existing].Evidence, State = state };
        else
        {
            entries.Add(new(entries.Count + 1L, now, cue, update.Evidence ?? [], state));
            activeIndex = entries.Count - 1;
        }
    }

    public void Mark(long id, CueState state)
    {
        int index = entries.ToList().FindIndex(entry => entry.Id == id);
        if (index < 0) throw new ArgumentException("Question does not exist in this meeting.", nameof(id));
        if (state is not (CueState.Dismissed or CueState.Covered))
            throw new ArgumentException("Only dismissal feedback can be applied manually.", nameof(state));
        entries[index] = entries[index] with { State = state };
        if (activeIndex == index) activeIndex = null;
    }

    public void Clear()
    {
        entries.Clear();
        activeIndex = null;
    }
}
