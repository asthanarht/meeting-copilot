using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class OverlayWindow : Window
{
    private readonly MeetingSession? session;
    private readonly Action<string> copyText;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool busy;
    private bool closed;
    private bool closing;
    private bool compact;
    private int demoIndex;
    private AnalysisUpdate? pending;
    private Cue? displayedCue;
    private readonly QuestionHistory history = new();
    private AnalysisUpdate latestUpdate = new(null, "No suggestion right now.");
    private long? browsingQuestionId;
    private bool updatingHistory;
    private int historyCountAtBrowse;
    private CancellationTokenSource? slideCancellation;
    private Window? slideDialog;
    private DateTimeOffset? latestFinal;
    private double expandedHeight = 860;
    private readonly Dictionary<string, string> provisional = [];
    private long displayedRevision = -1;
    private string displayedBrief = "";
    private string displayedMemory = "";

    public event Action<bool>? CaptureExclusionChanged;

    public OverlayWindow(MeetingSession? session, bool excludeFromCapture = true)
        : this(session, excludeFromCapture, Clipboard.SetText) { }

    internal OverlayWindow(MeetingSession? session, bool excludeFromCapture, Action<string> copyText)
    {
        this.copyText = copyText;
        InitializeComponent();
        QuestionHistoryList.ItemsSource = history.Entries;
        this.session = session;
        CaptureExclusionBox.IsChecked = excludeFromCapture;
        SourceInitialized += (_, _) => ApplyCaptureExclusion();
        CenterWindow();
        Closing += OnClosing;
        timer.Tick += (_, _) =>
        {
            RefreshSessionState();
            RefreshAnalysisTiming();
            if (session?.IsRunning == true && !busy)
                CaptureStatus.Text = session.AudioStatus;
            if (!compact) RefreshContext();
            if (pending is not null && browsingQuestionId is null && !IsReadingCue)
            {
                var update = pending;
                pending = null;
                Apply(update);
            }
        };
        if (session is not null)
        {
            session.Updated += OnUpdate;
            session.AnalysisStatusChanged += OnAnalysisStatus;
            session.Transcript += OnTranscript;
            session.Faulted += OnFault;
            RefreshContext();
            CaptureSlideButton.IsEnabled = false;
            if (!session.SupportsSlides)
                SlideStatusText.Text = "This connection supports text only. For slide images, use a Foundry Responses connection with a vision-capable model.";
        }
        else
        {
            CaptureStatus.Text = "PREVIEW - synthetic discussion; no audio capture or cloud calls";
            AnalysisText.Text = "Preview - no model analysis is running.";
            PauseButton.IsEnabled = false;
            CaptureSlideButton.IsEnabled = false;
            SlideStatusText.Text = "Preview only - slide capture is available in a live session.";
            FindButton.Content = "Next sample";
            ShowDemo();
        }
        Compact_Click(this, new RoutedEventArgs());
        RefreshSessionState();
        timer.Start();
    }

    private void RefreshSessionState() =>
        SessionStateText.Text = session is null ? "Sample preview - not listening" :
            busy ? "Updating session..." : session.IsRunning ? "Listening" : "Not listening";

    private bool IsReadingCue => CueText.IsMouseOver || WhyPanel.IsMouseOver || WhyPanel.IsKeyboardFocusWithin ||
        DismissButton.IsMouseOver || DismissButton.IsKeyboardFocusWithin ||
        CopyButton.IsMouseOver || CopyButton.IsKeyboardFocusWithin ||
        CoveredButton.IsMouseOver || CoveredButton.IsKeyboardFocusWithin;

    private void CaptureExclusionToggleChanged(object sender, RoutedEventArgs e)
    {
        if (CaptureExclusionBox is null || CaptureProtectionStatus is null) return;
        ApplyCaptureExclusion();
        CaptureExclusionChanged?.Invoke(CaptureExclusionBox.IsChecked == true);
    }

    private void ApplyCaptureExclusion()
    {
        bool requested = CaptureExclusionBox.IsChecked == true;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            CaptureProtectionStatus.Text = requested ? "Capture exclusion pending." : "Capture exclusion off - overlay can appear in screen sharing.";
            return;
        }
        try
        {
            WindowCaptureProtection.Apply(handle, requested);
            CaptureProtectionStatus.Text = requested
                ? "Capture exclusion on - supported tools only; verify your sharing app."
                : "Capture exclusion off - overlay can appear in screen sharing.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            string detail = ex is Win32Exception native ? $"Windows error {native.NativeErrorCode}" : ex.GetType().Name;
            CaptureProtectionStatus.Text = $"Capture exclusion not confirmed ({detail}). The overlay may be captured; share only an application window.";
        }
    }

    internal void ReportCapturePreferenceSaveFailure() =>
        CaptureProtectionStatus.Text += " Preference could not be saved; retry Save configuration in setup.";

    public async Task StartLiveAsync()
    {
        if (session is null || busy) return;
        busy = true;
        SetControls(false);
        provisional.Clear();
        ProvisionalText.Text = "Connecting; audio has not started.";
        CaptureStatus.Text = "Verifying model access with synthetic text; audio has not started...";
        AnalysisText.Text = "Checking model access before listening...";
        ErrorText.Text = "";
        try
        {
            await session.StartAsync();
            CaptureStatus.Text = session.AudioStatus;
            PauseButton.Content = "Pause";
            StatusText.Text = session.TranscriptionDescription;
            if (provisional.Count == 0) ProvisionalText.Text = "Waiting for live words.";
            AnalysisText.Text = "Waiting for finalized speech; provisional words are not analyzed.";
        }
        catch (Exception ex)
        {
            CaptureStatus.Text = "Not listening";
            PauseButton.Content = "Resume";
            ClearProvisional();
            ErrorText.Text = SafeError("Could not start", ex);
            AnalysisText.Text = "Analysis unavailable: listening did not start.";
        }
        finally
        {
            busy = false;
            if (!closing) SetControls(true);
        }
    }

    private void Dispatch(Action action)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!closed && !closing) action(); });
    }

    private void OnUpdate(AnalysisUpdate update) => Dispatch(() =>
    {
        if (session?.IsRunning != true) return;
        RefreshContext();
        ReceiveUpdate(update);
    });

    private void ReceiveUpdate(AnalysisUpdate update)
    {
        latestUpdate = update;
        history.Observe(update, DateTimeOffset.UtcNow);
        RefreshHistoryNavigation();
        if (browsingQuestionId is not null)
        {
            RenderHistoryQuestion();
            return;
        }
        // Retractions and errors are immediate. New wording waits while the user is reading.
        if (displayedCue is not null && update.Cue is not null && update.Cue.Text != CueText.Text && IsReadingCue)
        {
            pending = update;
            PendingText.Text = "New suggestion ready; holding the current wording while you read.";
            ShowPendingButton.Visibility = Visibility.Visible;
            StatusText.Text = "Previous suggestion - a newer contribution is waiting.";
            return;
        }
        pending = null;
        Apply(update);
    }

    private void RefreshHistoryNavigation()
    {
        updatingHistory = true;
        try
        {
            QuestionHistoryList.SelectedValue = browsingQuestionId;
        }
        finally { updatingHistory = false; }
        int index = browsingQuestionId is { } id ? history.Entries.ToList().FindIndex(e => e.Id == id) : history.Entries.Count;
        PreviousQuestionButton.IsEnabled = index > 0;
        NextQuestionButton.IsEnabled = browsingQuestionId is not null && index < history.Entries.Count - 1;
        HistoryPositionText.Text = browsingQuestionId is null
            ? $"Live - {history.Entries.Count} {(history.Entries.Count == 1 ? "suggestion" : "suggestions")} this meeting"
            : $"History {index + 1} of {history.Entries.Count} - not live advice";
        int unseen = browsingQuestionId is null ? 0 : history.Entries.Count - historyCountAtBrowse;
        LiveQuestionButton.Content = unseen > 0 ? $"Live ({unseen} new)" : "Live";
        BrowseQuestionsButton.Content = QuestionHistoryList.Visibility == Visibility.Visible
            ? "Hide list" : $"Browse suggestions ({history.Entries.Count})";
    }

    private void BrowseQuestion(long id)
    {
        if (browsingQuestionId is null) historyCountAtBrowse = history.Entries.Count;
        browsingQuestionId = id;
        pending = null;
        RefreshHistoryNavigation();
        RenderHistoryQuestion();
    }

    private void RenderHistoryQuestion()
    {
        var entry = history.Entries.First(e => e.Id == browsingQuestionId);
        Apply(new(entry.Cue, $"History - {entry.State}. Captured at {entry.CreatedAt.ToLocalTime():HH:mm:ss}; may no longer apply.",
            latestUpdate.Error, Evidence: entry.Evidence));
    }

    private void QuestionHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingHistory && QuestionHistoryList.SelectedItem is QuestionEntry entry) BrowseQuestion(entry.Id);
    }

    private void BrowseQuestions_Click(object sender, RoutedEventArgs e)
    {
        QuestionHistoryList.Visibility = QuestionHistoryList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        MinHeight = QuestionHistoryList.Visibility == Visibility.Visible ? 700 : 560;
        if (compact)
            Height = Math.Max(MinHeight, Math.Min(QuestionHistoryList.Visibility == Visibility.Visible ? 720 : 560, SystemParameters.WorkArea.Height - 48));
        RefreshHistoryNavigation();
    }

    private void PreviousQuestion_Click(object sender, RoutedEventArgs e)
    {
        int index = browsingQuestionId is { } id ? history.Entries.ToList().FindIndex(item => item.Id == id) : history.Entries.Count;
        if (index > 0) BrowseQuestion(history.Entries[index - 1].Id);
    }

    private void NextQuestion_Click(object sender, RoutedEventArgs e)
    {
        int index = history.Entries.ToList().FindIndex(item => item.Id == browsingQuestionId);
        if (index >= 0 && index + 1 < history.Entries.Count) BrowseQuestion(history.Entries[index + 1].Id);
    }

    private void LiveQuestion_Click(object sender, RoutedEventArgs e)
    {
        browsingQuestionId = null;
        pending = null;
        RefreshHistoryNavigation();
        Apply(latestUpdate);
    }

    private void Apply(AnalysisUpdate update)
    {
        if (displayedCue?.Text != update.Cue?.Text)
        {
            CueScroll.ScrollToTop();
            CopyStatus.Text = "";
        }
        displayedCue = update.Cue;
        PendingText.Text = "";
        ShowPendingButton.Visibility = Visibility.Collapsed;
        StatusText.Text = update.Status;
        ErrorText.Text = update.Error ?? "";
        CueText.Text = update.Cue?.Text ?? "Stay with the conversation.";
        CueTypeTag.Tag = update.Cue?.Kind;
        EmptyCueHelp.Visibility = update.Cue is null ? Visibility.Visible : Visibility.Collapsed;
        ReasonText.Text = update.Cue is { } cue ? $"{cue.Kind}: {cue.Reason}" : "";
        CaveatText.Text = update.Cue?.Caveat ?? "";
        EvidenceText.Text = string.Join("\n\n", (update.Evidence ?? []).Select(
            u => $"{u.At.ToLocalTime():HH:mm:ss} - {u.Source} ({(u.Kind == "slide" ? "AI-extracted slide, not speech" : "recognized speech")})\n{u.Text}"));
        DismissButton.IsEnabled = CoveredButton.IsEnabled = WhyPanel.IsEnabled = update.Cue is not null;
        CopyButton.IsEnabled = update.Cue is not null;
        WhyPanel.Header = update.Cue?.Kind == "recommendation" ? "Why this recommendation?" : "Why this suggestion?";
        if (update.Cue is null) WhyPanel.IsExpanded = false;
        RefreshAnalysisTiming();
    }

    private void RefreshAnalysisTiming()
    {
        if (session is null) return;
        var progress = session.GetAnalysisProgress();
        var now = DateTimeOffset.UtcNow;
        string speech = latestFinal is { } at ? $"Latest final speech: {(now - at).TotalSeconds:F0}s ago." : "No finalized speech yet.";
        string success = progress.LastSucceededAt is { } completed
            ? $"Last successful analysis: {(now - completed).TotalSeconds:F0}s ago ({progress.LastDuration?.TotalSeconds:F1}s request)."
            : "No successful meeting analysis yet.";
        string state = !session.IsRunning ? "Analysis stopped."
            : progress.StartedAt is { } started ? $"Analyzing: {(now - started).TotalSeconds:F0}s elapsed."
            : progress.RetryAt is { } retry ? $"Retry in {Math.Max(0, (retry - now).TotalSeconds):F0}s."
            : progress.ReceivedSegments > progress.AnalyzedRevision ? "New context queued."
            : "Waiting for new speech.";
        TimingText.Text = $"{speech} {success}\n{state} " +
            $"{progress.ReceivedSegments - progress.AnalyzedRevision} newer segments since that analysis." +
            $" {progress.MemoryVersions} summary versions preserved for this session." +
            (session.IsRunning && progress.Error is { } error ? $"\nLast analysis error: {error}" : "");
    }

    private void OnAnalysisStatus(string status) => Dispatch(() =>
    {
        if (session?.IsRunning == true) AnalysisText.Text = status;
    });

    private void ShowPending_Click(object sender, RoutedEventArgs e)
    {
        if (pending is not { } update) return;
        pending = null;
        Apply(update);
    }

    private async void CaptureSlide_Click(object sender, RoutedEventArgs e)
    {
        if (session?.IsRunning != true || !session.SupportsSlides || slideCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        slideCancellation = cancellation;
        CaptureSlideButton.IsEnabled = false;
        byte[]? png = null;
        try
        {
            Hide();
            var selector = new SlideRegionWindow();
            slideDialog = selector;
            if (selector.ShowDialog() != true || selector.SelectedRegion is not { } region)
            {
                SlideStatusText.Text = "Slide capture canceled; nothing was sent.";
                return;
            }
            slideDialog = null;
            // Let the region selector leave the composed desktop before reading only the selected pixels.
            await Task.Delay(150, cancellation.Token);
            if (closing || !session.IsRunning) throw new OperationCanceledException(cancellation.Token);
            png = ScreenRegionCapture.Capture(region);
            var capturedAt = DateTimeOffset.UtcNow;
            Show();
            var preview = new SlidePreviewWindow(png, CaptureExclusionBox.IsChecked == true) { Owner = this };
            slideDialog = preview;
            if (preview.ShowDialog() != true)
            {
                SlideStatusText.Text = "Slide discarded; nothing was sent.";
                return;
            }
            slideDialog = null;
            CancelSlideButton.Visibility = Visibility.Visible;
            await SendSlideAsync(png, capturedAt, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SlideStatusText.Text = "Slide operation canceled. A request already sent may still be billed; no late context was added.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or
            NotSupportedException or IOException or System.Text.Json.JsonException or
            System.Net.Http.HttpRequestException or ModelAuthenticationException or System.Runtime.InteropServices.COMException)
        {
            SlideStatusText.Text = SafeError("Slide could not be read", ex) + " Speech analysis is unchanged; retry with a clear slide region.";
        }
        finally
        {
            if (png is not null) Array.Clear(png);
            slideDialog = null;
            slideCancellation = null;
            CancelSlideButton.Visibility = Visibility.Collapsed;
            if (!closing)
            {
                Show();
                SetControls(!busy);
            }
        }
    }

    internal async Task SendSlideAsync(byte[] png, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        if (session is null) throw new InvalidOperationException("Slide reading is not available in preview.");
        SlideStatusText.Text = "Reading approved slide - one image request; live speech analysis continues.";
        var result = await session.AddSlideAsync(png, capturedAt, cancellationToken);
        if (closing) return;
        RefreshContext();
        SlideStatusText.Text = $"Slide added at {capturedAt.ToLocalTime():HH:mm:ss}: {result.Title}. AI extraction may be incomplete.";
    }

    private void CancelSlide_Click(object sender, RoutedEventArgs e) => slideCancellation?.Cancel();

    private void OnTranscript(string source, string text, bool final) => Dispatch(() =>
    {
        if (session?.IsRunning != true) return;
        if (final)
        {
            provisional.Remove(source);
            latestFinal = DateTimeOffset.UtcNow;
            RefreshContext();
        }
        else provisional[source] = text;
        ProvisionalText.Text = provisional.Count == 0 ? "Waiting for live words." :
            string.Join("\n\n", provisional.Select(entry => $"{entry.Key} / provisional\n{entry.Value}"));
    });

    private void RefreshContext()
    {
        if (session is not null) RenderContext(session.GetContextSnapshot());
    }

    private void RenderContext(AnalysisRequest snapshot)
    {
        if (displayedRevision != snapshot.Revision)
        {
            displayedRevision = snapshot.Revision;
            double scrollOffset = TranscriptText.VerticalOffset;
            int selectionStart = TranscriptText.SelectionStart;
            int selectionLength = TranscriptText.SelectionLength;
            bool followLatest = !TranscriptText.IsKeyboardFocusWithin && !TranscriptText.IsMouseOver &&
                TranscriptText.VerticalOffset >= TranscriptText.ExtentHeight - TranscriptText.ViewportHeight - 2;
            var speech = snapshot.Utterances.Where(u => u.Kind != "slide").ToArray();
            var slides = snapshot.Utterances.Where(u => u.Kind == "slide").ToArray();
            TranscriptText.Text = speech.Length == 0 ? "No finalized speech yet." :
                string.Join("\n\n", speech.Select(
                    u => $"{u.At.ToLocalTime():HH:mm:ss} - {u.Source}\n{u.Text}"));
            SlideContextText.Text = slides.Length == 0 ? "No analyzed slides retained." :
                string.Join("\n\n", slides.Select(u => $"Captured at {u.At.ToLocalTime():HH:mm:ss} - not necessarily the current slide\n{u.Text}"));
            if (followLatest) TranscriptText.ScrollToEnd();
            else TranscriptText.ScrollToVerticalOffset(scrollOffset);
            if (TranscriptText.IsKeyboardFocusWithin)
            {
                int start = Math.Min(selectionStart, TranscriptText.Text.Length);
                TranscriptText.Select(start, Math.Min(selectionLength, TranscriptText.Text.Length - start));
            }
            ContextStatus.Text = $"{speech.Length} speech {(speech.Length == 1 ? "segment" : "segments")} and " +
                $"{slides.Length} slide {(slides.Length == 1 ? "summary" : "summaries")} retained; " +
                $"{snapshot.Revision} total context items received. " +
                "Full session retained until Stop. Each AI request uses a bounded batch plus relevant earlier context.";
        }
        if (ContextText.Text == "No meeting context yet." ||
            displayedBrief != snapshot.MeetingBrief || displayedMemory != snapshot.Memory)
        {
            displayedBrief = snapshot.MeetingBrief;
            displayedMemory = snapshot.Memory;
            ContextText.Text = "Meeting brief\n" +
                (string.IsNullOrWhiteSpace(snapshot.MeetingBrief) ? "No brief supplied." : snapshot.MeetingBrief) +
                "\n\nModel-generated memory (may be incomplete)\n" +
                (string.IsNullOrWhiteSpace(snapshot.Memory) ? "Not generated yet. Finalized speech above is available for analysis." : snapshot.Memory);
        }
    }

    private void ClearProvisional()
    {
        provisional.Clear();
        ProvisionalText.Text = "Not listening. Finalized speech and context are retained until Stop.";
    }

    private void OnFault(string message) => Dispatch(async () =>
    {
        pending = null;
        ClearProvisional();
        ReceiveUpdate(new(null, "Listening interrupted.", message, RetiredState: CueState.Unavailable));
        AnalysisText.Text = "Analysis stopped because listening was interrupted.";
        CaptureStatus.Text = "Stopping interrupted capture...";
        busy = true;
        slideCancellation?.Cancel();
        slideDialog?.Close();
        SetControls(false);
        try { await session!.PauseAsync(); }
        catch (Exception ex) { ErrorText.Text = message + "\n" + SafeError("Cleanup failed", ex); }
        finally
        {
            CaptureStatus.Text = "Not listening";
            PauseButton.Content = "Resume";
            busy = false;
            SetControls(true);
        }
    });

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (session is null || busy) return;
        if (!session.IsRunning) { await StartLiveAsync(); return; }
        slideCancellation?.Cancel();
        slideDialog?.Close();
        busy = true;
        pending = null;
        ClearProvisional();
        SetControls(false);
        ReceiveUpdate(new(null, "Stopping audio and analysis...", RetiredState: CueState.Paused));
        AnalysisText.Text = "Stopping analysis...";
        try
        {
            await session.PauseAsync();
            Apply(new(null, "Paused. Capture and analysis are stopped; context stays in memory."));
            AnalysisText.Text = "Analysis paused; retained context is not being sent.";
        }
        catch (Exception ex) { ErrorText.Text = SafeError("Pause cleanup failed", ex); }
        finally
        {
            CaptureStatus.Text = "Paused - not listening";
            PauseButton.Content = "Resume";
            busy = false;
            SetControls(true);
        }
    }

    private void SetControls(bool enabled)
    {
        PauseButton.IsEnabled = enabled && session is not null;
        FindButton.IsEnabled = enabled && (session is null || session.IsRunning);
        CaptureSlideButton.IsEnabled = enabled && session?.IsRunning == true && session.SupportsSlides && slideCancellation is null;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (displayedCue is not { } cue) return;
        try
        {
            copyText(cue.Text);
            CopyStatus.Text = "Copied to clipboard. Clipboard history or sync may retain this text.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopyStatus.Text = "Could not copy: the Windows clipboard is unavailable. Try Copy again.";
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        GiveQuestionFeedback(false);
    }
    private void Covered_Click(object sender, RoutedEventArgs e)
    {
        GiveQuestionFeedback(true);
    }

    private void GiveQuestionFeedback(bool covered)
    {
        pending = null;
        string text = CueText.Text;
        var entry = browsingQuestionId is { } id ? history.Entries.First(e => e.Id == id) :
            history.Entries.LastOrDefault(e => e.Cue.Text == text);
        if (entry is not null) history.Mark(entry.Id, covered ? CueState.Covered : CueState.Dismissed);
        if (session is null)
        {
            if (latestUpdate.Cue?.Text == text)
                ReceiveUpdate(new(null, covered ? "Sample marked covered." : "Sample dismissed. Next sample to continue.",
                    RetiredState: covered ? CueState.Covered : CueState.Dismissed));
        }
        else session.Dismiss(covered, text);
        RefreshHistoryNavigation();
        if (browsingQuestionId is not null) RenderHistoryQuestion();
    }
    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (session is null) ShowDemo();
        else session.FindGap();
    }
    private void ShowDemo()
    {
        var examples = new[]
        {
            ("We'll retry the payment when the gateway times out.",
             "If a payment is retried, what prevents a duplicate charge?",
             "A timeout can occur after the first payment succeeds.", "question"),
            ("We will cache customer permissions for 24 hours.",
             "How quickly must revoked permissions take effect?",
             "A cached grant might remain valid after access is removed.", "question"),
            ("Clients retry together after outages and overload the recovering service. We have not chosen a retry policy.",
             "I suggest capped exponential backoff with jitter to spread retries while the service recovers.",
             "Spreading retries reduces synchronized load, at the cost of delaying some attempts.", "recommendation")
        };
        var sample = examples[demoIndex++ % examples.Length];
        var evidence = new Utterance(demoIndex, DateTimeOffset.UtcNow, "Sample speaker", sample.Item1);
        ProvisionalText.Text = "PREVIEW - no live audio or provisional words.";
        RenderContext(new(demoIndex, "Synthetic technical design review.", "Prewritten sample memory: " + sample.Item1,
            [evidence], [], null));
        ReceiveUpdate(new(new(sample.Item4, sample.Item2, sample.Item3,
            "This may already be addressed elsewhere. Confirm before raising it.", [evidence.Id]),
            "Preview only - this suggestion is prewritten.", Evidence: [evidence]));
    }
    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        compact = !compact;
        if (compact)
        {
            expandedHeight = Height;
            Height = Math.Max(MinHeight, Math.Min(QuestionHistoryList.Visibility == Visibility.Visible ? 720 : 560, SystemParameters.WorkArea.Height - 48));
        }
        else Height = Math.Min(expandedHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        DetailsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CueRow.Height = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CueRow.MaxHeight = compact ? double.PositiveInfinity : 200;
        DetailsRow.Height = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CompactButton.Content = compact ? "Show context" : "Focus view";
        if (!compact) RefreshContext();
    }
    private void Larger_Click(object sender, RoutedEventArgs e) => CueText.FontSize = Math.Min(38, CueText.FontSize + 2);
    private void Smaller_Click(object sender, RoutedEventArgs e) => CueText.FontSize = Math.Max(18, CueText.FontSize - 2);
    private void Center_Click(object sender, RoutedEventArgs e) => CenterWindow();
    private void CenterWindow()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 48));
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + 24;
    }
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => Close();

    private void Teleprompter_Click(object sender, RoutedEventArgs e) =>
        ScriptLibraryWindow.Open(this, CaptureExclusionBox.IsChecked == true);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        slideCancellation?.Cancel();
        slideDialog?.Close();
        session?.RequestStop();
        timer.Stop();
        IsEnabled = false;
        CaptureStatus.Text = "Stopping...";
        if (session is not null)
        {
            session.Updated -= OnUpdate;
            session.AnalysisStatusChanged -= OnAnalysisStatus;
            session.Transcript -= OnTranscript;
            session.Faulted -= OnFault;
            try { await session.DisposeAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(SafeError("Capture cleanup failed", ex), "Meeting copilot",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        closed = true;
        browsingQuestionId = null;
        QuestionHistoryList.ItemsSource = null;
        history.Clear();
        // Cleanup can finish synchronously; let the first Closing event unwind.
        _ = Dispatcher.BeginInvoke(Close);
    }

    private static string SafeError(string operation, Exception ex) =>
        ex is ModelAuthenticationException or System.Net.Http.HttpRequestException ? ex.Message :
            $"{operation} ({ex.GetType().Name}). Check network, keys, selected devices and the Windows Speech SDK prerequisites.";
}
