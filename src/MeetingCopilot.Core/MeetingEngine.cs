using System.Diagnostics;
using System.Text.Json;

namespace MeetingCopilot.Core;

public sealed class MeetingEngine(IMeetingModel model, string brief, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly List<Utterance> utterances = [];
    private readonly List<string> dismissed = [];
    private readonly List<MeetingMemory> memories = [];
    private string memory = "";
    private long revision;
    private long analyzedRevision;
    private long cueRevision;
    private bool requested;
    private Cue? current;
    private DateTimeOffset cueCreated;
    private DateTimeOffset? provisionalSince;
    private long? inFlightRevision;
    public static readonly TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(45);
    private DateTimeOffset? analysisStarted;
    private DateTimeOffset? lastSucceeded;
    private TimeSpan? lastDuration;
    private DateTimeOffset? retryAt;
    private string? analysisError;
    private bool analyzing;

    public AnalysisProgress GetProgress()
    {
        lock (gate)
            return new(revision, analyzedRevision, analysisStarted, lastSucceeded, lastDuration, retryAt, analysisError, memories.Count);
    }

    public MeetingMemory[] GetMemoryHistory()
    {
        lock (gate) return [.. memories];
    }

    public event Action<AnalysisUpdate>? Updated;
    public event Action<string>? AnalysisStatusChanged;

    public void Add(Utterance utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance.Text))
            return;
        if (utterance.Text.Length > 8000)
            throw new ArgumentException("A recognized utterance exceeded 8000 characters.");
        lock (gate)
        {
            if (utterances.Any(u => u.Id == utterance.Id))
                return;
            // Suppress close identical loopback/microphone echoes, not genuine later repetitions.
            if (utterances.TakeLast(4).Any(u =>
                u.Kind == "speech" && utterance.Kind == "speech" && u.Source != utterance.Source &&
                Math.Abs((utterance.At - u.At).TotalSeconds) < 3 &&
                string.Equals(u.Text.Trim(), utterance.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                return;
            utterances.Add(utterance);
            revision++;
            AnalysisStatusChanged?.Invoke(retryAt is not null
                ? $"Analysis retry pending; {revision - analyzedRevision} finalized segments await analysis. {analysisError}"
                : inFlightRevision is { } checking
                ? $"Analyzing context revision {checking}; {revision - checking} newer finalized segments queued."
                : $"Queued {revision - analyzedRevision} new finalized segments for analysis.");
            if (current is not null)
            {
                if (current.EvidenceIds.Any(id => !utterances.Any(u => u.Id == id)))
                {
                    current = null;
                    provisionalSince = null;
                    cueRevision++;
                    Updated?.Invoke(new(null, "Suggestion evidence left the retained context; checking newer speech."));
                    return;
                }
                provisionalSince ??= clock.GetUtcNow();
                // Publish in state order so a delayed update cannot resurrect a dismissed cue.
                Updated?.Invoke(new(current, "Provisional suggestion - newer speech is being checked.",
                    Evidence: utterances.Where(u => current.EvidenceIds.Contains(u.Id)).ToArray(), Provisional: true));
            }
        }
    }

    public AnalysisRequest Snapshot()
    {
        lock (gate)
            return new(revision, brief, memory, [.. utterances], [.. dismissed], current);
    }

    public void Dismiss(bool alreadyCovered, string? visibleText = null)
    {
        lock (gate)
        {
            var text = visibleText ?? current?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                dismissed.Add((alreadyCovered ? "Already covered: " : "Dismissed: ") + text);
            }
            // Feedback on a history entry must not dismiss a different live question.
            if (visibleText is not null && current?.Text != visibleText) return;
            current = null;
            provisionalSince = null;
            cueRevision++;
            Updated?.Invoke(new(null, alreadyCovered ? "Marked as already covered." : "Suggestion dismissed.",
                RetiredState: alreadyCovered ? CueState.Covered : CueState.Dismissed));
        }
    }

    public void RequestNow()
    {
        lock (gate)
        {
            requested = true;
            AnalysisStatusChanged?.Invoke(utterances.Count == 0
                ? "Suggestion requested; waiting for finalized speech. Provisional words are not analyzed."
                : inFlightRevision is not null
                    ? "Suggestion requested; queued behind the current analysis."
                    : "Suggestion requested; checking the current context next.");
        }
    }

    public void ClearCue(string status)
    {
        lock (gate)
        {
            current = null;
            provisionalSince = null;
            cueRevision++;
            Updated?.Invoke(new(null, status, RetiredState: CueState.Paused));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await AnalyzeOnceAsync(cancellationToken);
    }

    public async Task AnalyzeOnceAsync(CancellationToken cancellationToken)
    {
        AnalysisRequest request;
        long startedCueRevision;
        bool expired;
        lock (gate)
        {
            if (analyzing) return;
            var now = clock.GetUtcNow();
            expired = current is not null && (now - cueCreated >= TimeSpan.FromSeconds(45) ||
                provisionalSince is { } since && now - since >= TimeSpan.FromSeconds(15));
            if (expired)
            {
                current = null;
                provisionalSince = null;
                cueRevision++;
                Updated?.Invoke(new(null, "Suggestion expired; waiting for current discussion.", RetiredState: CueState.Expired));
            }
            if (utterances.Count == 0 || (!requested && revision == analyzedRevision))
            {
                request = new(0, "", "", [], [], null);
                startedCueRevision = 0;
            }
            else
            {
                request = MeetingContextSelection.Create(analyzedRevision, brief, memory,
                    utterances, memories, dismissed.TakeLast(64).ToArray(), current, requested, model.ContextCharacterBudget);
                requested = false;
                startedCueRevision = cueRevision;
                inFlightRevision = request.Revision;
                analyzing = true;
                analysisStarted = clock.GetUtcNow();
                retryAt = null;
                AnalysisStatusChanged?.Invoke($"Analyzing {request.Utterances.Length} retained segments " +
                    $"through context revision {request.Revision}...");
            }
        }
        if (request.Utterances.Length == 0)
            return;

        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(model.RequestTimeout);
        try
        {
            var result = await model.AnalyzeAsync(request, timeout.Token).WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            Cue? next;
            Utterance[] evidence;
            string status;
            lock (gate)
            {
                inFlightRevision = null;
                analysisStarted = null;
                lastSucceeded = clock.GetUtcNow();
                lastDuration = watch.Elapsed;
                analysisError = null;
                memory = result.Memory;
                if (!string.IsNullOrWhiteSpace(memory) && memories.LastOrDefault()?.Text != memory)
                    memories.Add(new(request.Revision, memory));
                analyzedRevision = request.Revision;
                if (cueRevision != startedCueRevision)
                {
                    AnalysisStatusChanged?.Invoke("Analysis completed; result discarded after dismissal or pause.");
                    return; // A dismissal or pause must win against an in-flight request.
                }
                if (result.CurrentCueStatus != "keep")
                    current = result.Suggestion;
                if (current is not null && (dismissed.Any(d =>
                    d.EndsWith(current.Text, StringComparison.OrdinalIgnoreCase)) ||
                    current.EvidenceIds.Any(id => !utterances.Any(u => u.Id == id))))
                    current = null;
                if (current is not null && current != request.CurrentCue)
                {
                    cueCreated = clock.GetUtcNow();
                    provisionalSince = null;
                }
                if (current is null)
                {
                    provisionalSince = null;
                    status = result.CurrentCueStatus == "addressed"
                        ? "Addressed in the discussion."
                        : "No grounded suggestion right now.";
                }
                else if (revision != request.Revision)
                {
                    provisionalSince ??= clock.GetUtcNow();
                    status = "Provisional suggestion - based on a recent snapshot; newer speech is being checked.";
                }
                else
                {
                    provisionalSince = null;
                    status = "Grounded in the current discussion.";
                }
                next = current;
                evidence = current is null ? [] :
                    utterances.Where(u => current.EvidenceIds.Contains(u.Id)).ToArray();
                if (request.Utterances.Length < utterances.Count)
                    status += " Using a bounded batch with earlier context; full meeting retained.";
                string outcome = current is not null
                    ? result.CurrentCueStatus == "keep" ? "Current suggestion remains relevant." : "Suggestion ready."
                    : result.Suggestion is not null
                        ? "Suggestion suppressed: dismissed wording or evidence no longer retained."
                        : "No new suggestion selected. Model explanation: " +
                          (string.IsNullOrWhiteSpace(result.NoSuggestionReason)
                              ? "No explanation returned." : result.NoSuggestionReason);
                AnalysisStatusChanged?.Invoke($"Checked revision {request.Revision} in {watch.Elapsed.TotalSeconds:F1}s. " +
                    outcome + (revision != request.Revision ? " Newer speech is queued." : ""));
                Updated?.Invoke(new(next, status, Latency: watch.Elapsed, Evidence: evidence,
                    RetiredState: result.CurrentCueStatus == "addressed" ? CueState.Addressed : CueState.Superseded,
                    Provisional: provisionalSince is not null));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelAuthenticationException)
        {
            throw; // The session must stop capture; retries cannot correct identity or permissions.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException
                                   or KeyNotFoundException or InvalidOperationException
                                   or OperationCanceledException)
        {
            var error = ex is OperationCanceledException
                ? $"Model took more than {model.RequestTimeout.TotalSeconds:F0} seconds. Retrying; speech capture continues."
                : ex is HttpRequestException ? ex.Message
                : ex is IOException ? "Model response connection was interrupted; retrying."
                : "Model returned an invalid response. Check model compatibility and output-token budget; retrying.";
            lock (gate)
            {
                inFlightRevision = null;
                analysisStarted = null;
                analysisError = error;
                retryAt = clock.GetUtcNow().AddSeconds(8);
                requested |= request.QuestionRequested;
                current = null;
                provisionalSince = null;
                AnalysisStatusChanged?.Invoke("Analysis failed; retrying in 8 seconds. " + error);
                Updated?.Invoke(new(null, "Live analysis unavailable.", error, RetiredState: CueState.Unavailable));
            }
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                inFlightRevision = null;
                analysisStarted = null;
                retryAt = null;
                analyzing = false;
            }
        }
    }
}
