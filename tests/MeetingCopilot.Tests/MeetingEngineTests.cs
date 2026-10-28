using MeetingCopilot.Core;
using Xunit;

namespace MeetingCopilot.Tests;

public class MeetingEngineTests
{
    [Theory]
    [InlineData(16_000)]
    [InlineData(24_000)]
    [InlineData(48_000)]
    public async Task HourOfSpeechIsPreservedAndEveryCatchUpRequestIncludesLiveSpeech(int budget)
    {
        var seen = new HashSet<long>();
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new StubModel(request =>
        {
            requests.Add(request);
            seen.UnionWith(request.Utterances.Select(u => u.Id));
            Assert.Contains(request.Utterances, u => u.Id == 720);
            Assert.InRange(request.Utterances.Length, 1, 80);
            Assert.True(request.Utterances.Sum(u => u.Text.Length) <= budget);
            return new($"Summary of catch-up batch {request.Revision}.", "none", Question(720));
        }) { ContextCharacterBudget = budget }, "");
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (int id = 1; id <= 720; id++)
            engine.Add(new(id, start.AddSeconds(id * 5), "Meeting playback",
                $"Synthetic remark {id}: " + new string('x', 380)));
        for (int attempt = 0; attempt < 100 && engine.GetProgress().AnalyzedRevision < 720; attempt++)
        {
            long before = engine.GetProgress().AnalyzedRevision;
            await engine.AnalyzeOnceAsync(default);
            Assert.True(engine.GetProgress().AnalyzedRevision > before);
            Assert.Equal(720, engine.Snapshot().CurrentCue!.EvidenceIds[0]);
        }
        Assert.Equal(720, engine.GetProgress().AnalyzedRevision);
        Assert.Equal(720, engine.Snapshot().Utterances.Length);
        Assert.Equal(720, seen.Count);
        Assert.Equal(requests.Count, engine.GetMemoryHistory().Length);
        Assert.Equal(requests.Count, engine.GetProgress().MemoryVersions);
        Assert.True(requests.Count > 1);
    }

    [Fact]
    public async Task ReturningTopicRetrievesEarlierEvidenceAndPreservedSummary()
    {
        AnalysisRequest? latest = null;
        var engine = new MeetingEngine(new StubModel(request =>
        {
            latest = request;
            return new(request.Revision == 1
                ? "Quasar residency requires Frankfurt hosting."
                : "Current unrelated discussion.", "none", null);
        }), "");
        engine.Add(Speech(1, "Quasar residency requires Frankfurt hosting."));
        await engine.AnalyzeOnceAsync(default);
        for (int id = 2; id < 110; id++)
        {
            engine.Add(Speech(id, $"An unrelated point about delivery schedules {id}."));
            await engine.AnalyzeOnceAsync(default);
        }
        engine.Add(Speech(110, "Returning to Quasar: what residency constraints affect hosting?"));
        await engine.AnalyzeOnceAsync(default);
        Assert.Contains(latest!.Utterances, u => u.Id == 1);
        Assert.Contains("Frankfurt", latest.HistoricalMemory);
        Assert.Contains(engine.GetMemoryHistory(), m => m.ThroughRevision == 1 && m.Text.Contains("Frankfurt"));
    }

    [Fact]
    public async Task LongMeetingKeepsUpdatingBeyondRollingLimitWithSmallerAutomaticRequests()
    {
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new StubModel(request =>
        {
            requests.Add(request);
            long latest = request.Utterances[^1].Id;
            return new($"Summary through {latest}; original constraint retained.", "none", Question(latest));
        }), "");
        for (int i = 1; i <= 200; i++)
        {
            engine.Add(Speech(i));
            await engine.AnalyzeOnceAsync(default);
        }
        Assert.Equal(200, engine.Snapshot().Utterances.Length);
        Assert.Contains("200", engine.Snapshot().Memory);
        Assert.Equal(200, engine.Snapshot().CurrentCue!.EvidenceIds[0]);
        Assert.InRange(requests[^1].Utterances.Length, 12, 24);
        Assert.Contains("199", requests[^1].Memory);
        Assert.Equal(200, engine.GetProgress().AnalyzedRevision);
        engine.RequestNow();
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(80, requests[^1].Utterances.Length);
    }

    [Fact]
    public async Task IncrementalRequestsPreserveUnprocessedSpeechSlidesAndActiveEvidence()
    {
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new StubModel(request =>
        {
            requests.Add(request);
            return request.CurrentCue is null ? Result() : new("Original constraint remains.", "keep", null);
        }), "");
        engine.Add(Speech(1));
        engine.Add(new(2, DateTimeOffset.UtcNow, "Slide", "Synthetic slide constraint", "slide"));
        await engine.AnalyzeOnceAsync(default);
        for (int i = 3; i <= 40; i++) engine.Add(Speech(i));
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(40, requests[^1].Utterances.Length);
        engine.Add(Speech(41));
        await engine.AnalyzeOnceAsync(default);
        Assert.Contains(requests[^1].Utterances, u => u.Id == 1);
        Assert.Contains(requests[^1].Utterances, u => u.Id == 2);
        Assert.All(Enumerable.Range(30, 12), id => Assert.Contains(requests[^1].Utterances, u => u.Id == id));
        Assert.True(requests[^1].Utterances.Length < 41);
    }

    [Fact]
    public async Task IncrementalBatchDoesNotSkipOutOfOrderIdsOrAdvanceOnFailure()
    {
        using var cancelRetry = new CancellationTokenSource();
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new StubModel(request =>
        {
            requests.Add(request);
            if (requests.Count == 2) throw new InvalidDataException("Synthetic response failure.");
            return new("Constraint retained.", "none", null);
        }), "");
        engine.AnalysisStatusChanged += status =>
        {
            if (status.StartsWith("Analysis failed")) cancelRetry.Cancel();
        };
        engine.Add(Speech(100));
        await engine.AnalyzeOnceAsync(default);
        for (int id = 1; id <= 25; id++) engine.Add(Speech(id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.AnalyzeOnceAsync(cancelRetry.Token));
        Assert.Equal(1, engine.GetProgress().AnalyzedRevision);
        Assert.Equal("Constraint retained.", engine.Snapshot().Memory);
        await engine.AnalyzeOnceAsync(default);
        Assert.All(Enumerable.Range(1, 25), id => Assert.Contains(requests[^1].Utterances, u => u.Id == id));
        Assert.Equal(26, engine.GetProgress().AnalyzedRevision);
    }

    [Fact]
    public async Task ModelTakingLongerThanOldDeadlineStillUpdatesMemoryAndSuggestion()
    {
        var engine = new MeetingEngine(new AsyncModel(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(13));
            return Result();
        }), "");
        engine.Add(Speech(1));
        var analysis = engine.AnalyzeOnceAsync(default);
        Assert.NotNull(engine.GetProgress().StartedAt);
        await engine.AnalyzeOnceAsync(default); // Does not start a concurrent request.
        await analysis;
        Assert.Contains("duplicate", engine.Snapshot().Memory);
        Assert.NotNull(engine.Snapshot().CurrentCue);
        Assert.NotNull(engine.GetProgress().LastSucceededAt);
        Assert.True(engine.GetProgress().LastDuration >= TimeSpan.FromSeconds(13));
        Assert.Null(engine.GetProgress().StartedAt);
        Assert.Null(engine.GetProgress().Error);
    }

    [Fact]
    public async Task HungModelTimesOutWithoutLosingNewSpeechAndRecoversOnNextAttempt()
    {
        var hung = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new AsyncModel(request =>
        {
            requests.Add(request);
            return requests.Count == 1 ? hung.Task : Task.FromResult(Result(2));
        }), "");
        using var stopRetry = new CancellationTokenSource();
        AnalysisProgress? failed = null;
        string? retryStatus = null;
        engine.AnalysisStatusChanged += status =>
        {
            if (status.StartsWith("Analysis failed"))
            {
                failed = engine.GetProgress();
                engine.Add(Speech(2));
                stopRetry.Cancel();
            }
            if (status.StartsWith("Analysis retry pending")) retryStatus = status;
        };
        engine.Add(Speech(1));
        engine.RequestNow();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.AnalyzeOnceAsync(stopRetry.Token));
        Assert.NotNull(failed);
        Assert.NotNull(failed.RetryAt);
        Assert.Contains("45 seconds", failed.Error);
        Assert.Contains("Analysis retry pending", retryStatus);
        Assert.Null(failed.LastSucceededAt);
        Assert.Equal(0, failed.AnalyzedRevision);
        await engine.AnalyzeOnceAsync(default);
        Assert.True(requests[1].QuestionRequested);
        Assert.Equal(2, requests[1].Utterances.Length);
        Assert.Equal(2, engine.GetProgress().AnalyzedRevision);
        Assert.Null(engine.GetProgress().Error);
        Assert.Null(engine.GetProgress().RetryAt);
        hung.SetResult(new("Late stale summary", "none", null));
        Assert.DoesNotContain("Late", engine.Snapshot().Memory);
    }

    private static Utterance Speech(long id, string text = "We retry payments after a timeout.") =>
        new(id, DateTimeOffset.UtcNow, "Meeting playback", text);
    private static Cue Question(long id = 1) =>
        new("question", "What prevents duplicate charges?", "A timeout can hide success.", "May be addressed elsewhere.", [id]);
    private static AnalysisResult Result(long id = 1) => new("Retried payments; duplicate handling unresolved.", "none", Question(id));

    [Fact]
    public async Task NewEvidenceCanReplaceAQuestionWithARecommendationThenRetireItWhenAdopted()
    {
        var recommendation = new Cue("recommendation", "Reuse a stable idempotency key across retries.",
            "Repeated successful charges require deduplication.", "Requires gateway support.", [2]);
        int calls = 0;
        var model = new StubModel(request => ++calls switch
        {
            1 => Result(),
            2 => new("Duplicate charges confirmed.", "outdated", recommendation),
            _ => new("Idempotency chosen.", "addressed", null)
        });
        var engine = new MeetingEngine(model, "");
        var history = new QuestionHistory();
        engine.Updated += update => history.Observe(update, DateTimeOffset.UtcNow);
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.Add(Speech(2, "Retries caused duplicate charges; the gateway supports idempotency keys."));
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(recommendation, engine.Snapshot().CurrentCue);
        Assert.Equal(CueState.Superseded, history.Entries[0].State);
        Assert.Equal("recommendation", history.Entries[1].Cue.Kind);
        engine.Add(Speech(3, "We agreed to reuse the same idempotency key across retries."));
        await engine.AnalyzeOnceAsync(default);
        Assert.Null(engine.Snapshot().CurrentCue);
        Assert.Equal(CueState.Addressed, history.Entries[1].State);
    }

    [Fact]
    public async Task ReportsQueuedRunningAndExplainedSilenceWithoutInventingACue()
    {
        var model = new DelayedModel();
        var engine = new MeetingEngine(model, "");
        var statuses = new List<string>();
        engine.AnalysisStatusChanged += statuses.Add;
        engine.Add(Speech(1, "Hello everyone."));
        Assert.Contains("Queued", statuses[^1]);
        var analysis = engine.AnalyzeOnceAsync(default);
        Assert.Contains("Analyzing 1", statuses[^1]);
        engine.Add(Speech(2, "Good morning."));
        Assert.Contains("1 newer finalized segments queued", statuses[^1]);
        model.Complete(new("Greetings only.", "none", null, "No substantive topic has started."));
        await analysis;
        Assert.Null(engine.Snapshot().CurrentCue);
        Assert.Contains("No new suggestion selected", statuses[^1]);
        Assert.Contains("No substantive topic has started", statuses[^1]);
        Assert.Contains("Newer speech is queued", statuses[^1]);
    }

    [Fact]
    public async Task ExplicitQuestionRequestIsSentOnceAndDoesNotRequireNewSpeech()
    {
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new StubModel(request =>
        {
            requests.Add(request);
            return new("", "none", null, "No substantive topic.");
        }), "");
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.RequestNow();
        await engine.AnalyzeOnceAsync(default);
        Assert.False(requests[0].QuestionRequested);
        Assert.True(requests[1].QuestionRequested);
        Assert.Equal(requests[0].Revision, requests[1].Revision);
        engine.Add(Speech(2));
        await engine.AnalyzeOnceAsync(default);
        Assert.False(requests[2].QuestionRequested);
    }

    [Fact]
    public async Task ExplicitQuestionRequestDuringAnalysisIsNotLost()
    {
        var first = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new AsyncModel(request =>
        {
            requests.Add(request);
            return requests.Count == 1 ? first.Task :
                Task.FromResult(new AnalysisResult("", "none", null, "The topic is not clear."));
        }), "");
        engine.Add(Speech(1));
        var analysis = engine.AnalyzeOnceAsync(default);
        engine.RequestNow();
        first.SetResult(new("", "none", null, "The topic is not clear."));
        await analysis;
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(2, requests.Count);
        Assert.True(requests[1].QuestionRequested);
    }

    [Fact]
    public async Task AnalysisFailuresAreDistinguishedFromChoosingSilence()
    {
        using var cancel = new CancellationTokenSource();
        var requests = new List<AnalysisRequest>();
        var engine = new MeetingEngine(new AsyncModel(request =>
        {
            requests.Add(request);
            return requests.Count == 1
                ? Task.FromException<AnalysisResult>(new InvalidDataException("Invalid synthetic response."))
                : Task.FromResult(new AnalysisResult("", "none", null, "No substantive topic."));
        }), "");
        string? last = null;
        engine.AnalysisStatusChanged += status =>
        {
            last = status;
            if (status.Contains("Analysis failed")) cancel.Cancel();
        };
        engine.Add(Speech(1));
        engine.RequestNow();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.AnalyzeOnceAsync(cancel.Token));
        Assert.Contains("Analysis failed", last);
        Assert.DoesNotContain("No new suggestion selected", last);
        await engine.AnalyzeOnceAsync(default);
        Assert.True(requests[1].QuestionRequested);
    }

    [Fact]
    public async Task DismissingAHistoricalQuestionDoesNotClearTheLiveQuestion()
    {
        var engine = new MeetingEngine(new StubModel(_ => Result()), "");
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.Dismiss(true, "An earlier question?");
        Assert.Equal(Question().Text, engine.Snapshot().CurrentCue!.Text);
        Assert.Contains("Already covered: An earlier question?", engine.Snapshot().Dismissed);
        engine.Dismiss(false, Question().Text);
        Assert.Null(engine.Snapshot().CurrentCue);
    }

    [Fact]
    public async Task FinalizedSpeechProducesGroundedCueAndRetainsMemory()
    {
        var engine = new MeetingEngine(new StubModel(_ => Result()), "Review payment design");
        AnalysisUpdate? last = null;
        engine.Updated += u => last = u;
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(Question().Text, last!.Cue!.Text);
        Assert.Equal(Question().EvidenceIds, last.Cue.EvidenceIds);
        Assert.Equal(1, Assert.Single(last.Evidence!).Id);
        Assert.Contains("duplicate", engine.Snapshot().Memory);
    }

    [Fact]
    public async Task NewSpeechDuringRequestProducesExplicitlyProvisionalCueThenRechecks()
    {
        var model = new DelayedModel();
        var engine = new MeetingEngine(model, "");
        AnalysisUpdate? last = null;
        engine.Updated += u => last = u;
        engine.Add(Speech(1));
        var task = engine.AnalyzeOnceAsync(default);
        engine.Add(Speech(2, "Idempotency keys already prevent duplicate charges."));
        model.Complete(Result());
        await task;
        Assert.NotNull(last!.Cue);
        Assert.Contains("Provisional", last.Status);
        Assert.Contains("newer speech", last.Status);
        Assert.Equal(2, engine.Snapshot().Revision);
        Assert.Equal(Question().Text, engine.Snapshot().CurrentCue!.Text);
    }

    [Fact]
    public async Task ContinuedSpeechDoesNotStarveSuggestionsAndAnsweredCueIsRetracted()
    {
        var first = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var model = new AsyncModel(_ => ++calls == 1 ? first.Task :
            Task.FromResult(new AnalysisResult("Duplicate handling resolved.", "addressed", null)));
        var engine = new MeetingEngine(model, "");
        AnalysisUpdate? update = null;
        engine.Updated += value => update = value;
        engine.Add(Speech(1));
        var analysis = engine.AnalyzeOnceAsync(default);
        for (int i = 2; i <= 6; i++) engine.Add(Speech(i, "The design discussion continues."));
        first.SetResult(Result());
        await analysis;
        Assert.NotNull(update!.Cue);
        Assert.Contains("Provisional", update.Status);
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(2, calls);
        Assert.Null(update.Cue);
        Assert.Contains("Addressed", update.Status);
    }

    [Fact]
    public async Task NewSpeechMarksVisibleCueProvisionalAndItExpiresAtFifteenSeconds()
    {
        var clock = new TestClock();
        int calls = 0;
        var model = new StubModel(_ => ++calls == 1 ? Result() : new("", "none", null));
        var engine = new MeetingEngine(model, "", clock);
        var updates = new List<AnalysisUpdate>();
        engine.Updated += updates.Add;
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.Add(Speech(2, "A different condition may apply."));
        Assert.Contains("Provisional", updates[^1].Status);
        clock.Advance(15);
        await engine.AnalyzeOnceAsync(default);
        Assert.Contains(updates, value => value.Status.Contains("expired") && value.Cue is null);
        Assert.Null(engine.Snapshot().CurrentCue);
    }

    [Fact]
    public async Task UpdatedSnapshotCanConfirmAProvisionalCue()
    {
        var engine = new MeetingEngine(new StubModel(request => request.CurrentCue is null
            ? Result() : new("", "keep", null)), "");
        AnalysisUpdate? update = null;
        engine.Updated += value => update = value;
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.Add(Speech(2, "The retry policy is still undecided."));
        Assert.Contains("Provisional", update!.Status);
        await engine.AnalyzeOnceAsync(default);
        Assert.NotNull(update.Cue);
        Assert.DoesNotContain("Provisional", update.Status);
    }

    [Fact]
    public async Task EarlierEvidenceRemainsAvailableWhenMoreThanEightySegmentsArriveDuringAnalysis()
    {
        var model = new DelayedModel();
        var engine = new MeetingEngine(model, "");
        engine.Add(Speech(1));
        var analysis = engine.AnalyzeOnceAsync(default);
        for (int i = 2; i <= 100; i++) engine.Add(Speech(i));
        model.Complete(Result());
        await analysis;
        Assert.NotNull(engine.Snapshot().CurrentCue);
        Assert.Equal(100, engine.Snapshot().Utterances.Length);
    }

    [Fact]
    public async Task DismissalWinsOverInflightResult()
    {
        var model = new DelayedModel();
        var engine = new MeetingEngine(model, "");
        engine.Add(Speech(1));
        var task = engine.AnalyzeOnceAsync(default);
        engine.Dismiss(true);
        model.Complete(Result());
        await task;
        Assert.Null(engine.Snapshot().CurrentCue);
    }

    [Fact]
    public async Task CanceledRequestCannotPublish()
    {
        var model = new DelayedModel();
        var engine = new MeetingEngine(model, "");
        int updates = 0;
        engine.Updated += _ => updates++;
        engine.Add(Speech(1));
        using var cancel = new CancellationTokenSource();
        var task = engine.AnalyzeOnceAsync(cancel.Token);
        cancel.Cancel();
        model.Complete(Result());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task IdleDiscussionDoesNotRepeatModelCalls()
    {
        int calls = 0;
        var engine = new MeetingEngine(new StubModel(_ => { calls++; return new("", "none", null); }), "");
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(0, calls);
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(1, calls);
        engine.RequestNow();
        await engine.AnalyzeOnceAsync(default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AlreadyCoveredQuestionIsSuppressed()
    {
        var engine = new MeetingEngine(new StubModel(_ => Result()), "");
        engine.Add(Speech(1));
        await engine.AnalyzeOnceAsync(default);
        engine.Dismiss(true);
        engine.Add(Speech(2));
        await engine.AnalyzeOnceAsync(default);
        Assert.Null(engine.Snapshot().CurrentCue);
        Assert.Contains("Already covered:", Assert.Single(engine.Snapshot().Dismissed));
    }

    [Fact]
    public void FullContextIsRetainedAndOnlyNearIdenticalCrossInputEchoIsSuppressed()
    {
        var engine = new MeetingEngine(new StubModel(_ => Result()), "");
        var first = Speech(1);
        engine.Add(first);
        engine.Add(first with { Id = 2, Source = "Your microphone" });
        engine.Add(first with { Id = 3, At = first.At.AddSeconds(10), Source = "Your microphone" });
        Assert.Equal(2, engine.Snapshot().Utterances.Length);
        for (int i = 4; i < 200; i++) engine.Add(Speech(i, new string('x', 400)));
        var snapshot = engine.Snapshot();
        Assert.Equal(198, snapshot.Utterances.Length);
        Assert.True(snapshot.Utterances.Sum(u => u.Text.Length) > 24_000);
        Assert.Equal(1, snapshot.Utterances[0].Id);
        Assert.Equal(199, snapshot.Utterances[^1].Id);
    }

    private sealed class StubModel(Func<AnalysisRequest, AnalysisResult> result) : IMeetingModel
    {
        public int ContextCharacterBudget { get; init; } = 24_000;
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result(request));
    }
    private sealed class DelayedModel : IMeetingModel
    {
        private readonly TaskCompletionSource<AnalysisResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) => completion.Task;
        public void Complete(AnalysisResult result) => completion.SetResult(result);
    }

    private sealed class AsyncModel(Func<AnalysisRequest, Task<AnalysisResult>> analyze) : IMeetingModel
    {
        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) => analyze(request);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
