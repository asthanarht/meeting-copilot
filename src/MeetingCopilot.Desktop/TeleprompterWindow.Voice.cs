using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class TeleprompterWindow
{
    private readonly Func<PrompterVoiceConnection> voiceConnectionFactory;
    private PrompterVoiceConnection? voiceConnection;
    private ISpeechAudioInput? voiceInput;
    private CancellationTokenSource? voiceRun;
    private Action<string, string, bool>? voiceTranscript;
    private Action<string>? voiceFault;
    private Task voiceStart = Task.CompletedTask;
    private Task voiceStop = Task.CompletedTask;
    private int voiceGeneration;
    private bool voiceStarting;
    private bool voiceActive;
    private bool voiceCleanupFailed;
    private bool closeRequested;
    private readonly TaskCompletionSource readerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool IsFollowing => voiceActive;
    internal int CurrentSpokenWord => matcher.CurrentWord;

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TimedControls is null || VoiceFeedback is null || e.Source != ModeTabs) return;
        Pause();
        Options = Options with { VoiceFollow = ModeTabs.SelectedIndex == 0 };
        ApplyMode();
        if (IsLoaded && Options.VoiceFollow)
        {
            UpdateLayout();
            ResetVoiceAnchor(WordAtReadingPosition());
            PrepareVoiceConnection();
        }
    }

    private void ApplyMode()
    {
        bool voice = Options.VoiceFollow;
        ModeDescription.Text = voice ? "Voice follow - microphone transcription, no LLM"
            : "Timed teleprompter - offline, no microphone";
        VoiceFeedback.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
        TimedControls.Visibility = voice ? Visibility.Collapsed : Visibility.Visible;
        ActiveWordHighlight.Visibility = Visibility.Collapsed;
        MinHeight = voice ? 860 : 700;
        Height = Math.Max(Height, MinHeight);
        if (!voice) VoiceConsentBox.IsChecked = false;
        RefreshLabels();
    }

    private void PrepareVoiceConnection()
    {
        if (voiceActive || voiceStarting) return;
        voiceConnection = null;
        try
        {
            voiceConnection = voiceConnectionFactory();
            MicrophoneText.Text = $"Microphone: {voiceConnection.MicrophoneName}. Uses saved Azure Speech settings.";
            VoiceError.Text = "";
        }
        catch (Exception ex) when (ScriptStore.IsStorageError(ex) || ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            MicrophoneText.Text = "Microphone connection is not ready.";
            VoiceError.Text = ex is ArgumentException or InvalidOperationException ? ex.Message
                : "Could not load the saved Speech connection or microphone. Check setup, then refresh this connection.";
        }
        UpdatePlaybackState();
    }

    private void RefreshVoice_Click(object sender, RoutedEventArgs e)
    {
        if (voiceActive || voiceStarting)
        {
            VoiceStatus.Text = "Pause voice following before refreshing the saved connection.";
            return;
        }
        VoiceConsentBox.IsChecked = false;
        PrepareVoiceConnection();
    }

    private void VoiceConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (PlayButton is null) return;
        if (VoiceConsentBox.IsChecked != true) Pause();
        UpdatePlaybackState();
    }

    private async Task ToggleReaderAsync()
    {
        if (!Options.VoiceFollow) { TogglePlayback(); return; }
        if (voiceActive || voiceStarting) await StopFollowingAsync();
        else await StartFollowingAsync();
    }

    internal async Task StartFollowingAsync()
    {
        if (!Options.VoiceFollow || voiceStarting || voiceActive || closed) return;
        if (VoiceConsentBox.IsChecked != true || voiceConnection is null)
        {
            VoiceStatus.Text = "Review the microphone and enable Azure Speech permission before starting.";
            return;
        }
        if (matcher.WordCount == 0 || matcher.AtEnd)
        {
            VoiceStatus.Text = "No unread words here. Restart or click the word where you want to begin.";
            return;
        }
        if (ReadingSettings.IsExpanded)
        {
            ReadingSettings.IsExpanded = false;
            UpdateLayout();
        }
        int generation = ++voiceGeneration;
        voiceStarting = true;
        UpdatePlaybackState();
        await voiceStop;
        if (generation != voiceGeneration || closed || !Options.VoiceFollow || VoiceConsentBox.IsChecked != true || voiceCleanupFailed)
        {
            voiceStarting = false;
            UpdatePlaybackState();
            return;
        }
        try
        {
            VoiceError.Text = "";
            VoiceStatus.Text = "Connecting microphone to Azure Speech...";
            matcher.Reset(matcher.CurrentWord + 1);
            voiceRun = new();
            var cancellation = voiceRun.Token;
            voiceInput = voiceConnection.CreateInput();
            voiceTranscript = (_, text, final) => DispatchVoice(generation, () => FollowWords(text, final));
            voiceFault = message => DispatchVoice(generation, () =>
            {
                VoiceError.Text = message;
                VoiceStatus.Text = "Voice following stopped. Check the microphone and Speech connection before restarting.";
                _ = StopFollowingAsync();
            });
            voiceInput.Transcript += voiceTranscript;
            voiceInput.Faulted += voiceFault;
            voiceActive = true;
            voiceStart = voiceInput.StartAsync(cancellation);
            await voiceStart;
            cancellation.ThrowIfCancellationRequested();
            if (generation == voiceGeneration)
            {
                VoiceStatus.Text = "Listening to your microphone. Read from the current position; pauses hold the text.";
                voiceStarting = false;
                UpdatePlaybackState();
            }
        }
        catch (OperationCanceledException) when (generation != voiceGeneration || voiceRun?.IsCancellationRequested == true)
        {
            await StopFollowingAsync();
        }
        catch (Exception ex)
        {
            VoiceError.Text = $"Microphone following could not start ({ex.GetType().Name}). Check the saved Speech key, endpoint, microphone permissions and network.";
            await StopFollowingAsync();
        }
        finally
        {
            voiceStarting = false;
            if (!closed) UpdatePlaybackState();
        }
    }

    private void DispatchVoice(int generation, Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!closed && voiceActive && generation == voiceGeneration && Options.VoiceFollow) action();
        });
    }

    internal Task StopFollowingAsync()
    {
        ++voiceGeneration;
        voiceActive = false;
        voiceStarting = false;
        voiceRun?.Cancel();
        if (!voiceStop.IsCompleted) return voiceStop;
        if (voiceInput is null) return Task.CompletedTask;
        var input = voiceInput;
        input.Transcript -= voiceTranscript;
        input.Faulted -= voiceFault;
        try { input.StopCapture(); }
        catch (Exception ex)
        {
            voiceCleanupFailed = true;
            VoiceError.Text = $"Microphone capture did not stop cleanly ({ex.GetType().Name}). Closing its resources.";
        }
        voiceStop = DisposeVoiceAsync(input, voiceStart, voiceRun);
        return voiceStop;
    }

    private async Task DisposeVoiceAsync(ISpeechAudioInput input, Task startup, CancellationTokenSource? run)
    {
        try
        {
            try { await startup; }
            catch (OperationCanceledException) when (run?.IsCancellationRequested == true) { }
            catch (Exception ex)
            {
                VoiceError.Text = $"Speech startup failed ({ex.GetType().Name}). Check the Speech connection and microphone before retrying.";
            }
            await input.DisposeAsync();
        }
        catch (Exception ex)
        {
            voiceCleanupFailed = true;
            VoiceError.Text = $"Microphone cleanup failed ({ex.GetType().Name}). Close this reader before starting another audio session.";
        }
        finally
        {
            if (ReferenceEquals(voiceInput, input))
            {
                voiceInput = null;
                voiceRun = null;
            }
            run?.Dispose();
            if (!closed) UpdatePlaybackState();
        }
    }

    private void FollowWords(string text, bool final)
    {
        if (text.StartsWith("[Speech could not", StringComparison.Ordinal))
        {
            VoiceStatus.Text = "Speech was unclear. Holding position; repeat the current line or click a word to re-anchor.";
            return;
        }
        HeardText.Text = (final ? "Heard: " : "Hearing: ") + (text.Length > 220 ? text[^220..] : text);
        var result = matcher.Observe(text, final);
        if (!result.Matched)
        {
            VoiceStatus.Text = "Holding position - words do not confidently match nearby script text. Repeat the line or click a word.";
            return;
        }
        highlightedWord = result.WordIndex;
        ReaderScroll.UpdateLayout();
        var (run, paragraph) = scriptWords[highlightedWord];
        var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty)
        {
            double lineTop = paragraph.TranslatePoint(new Point(rect.Left, rect.Top), Paragraphs).Y;
            playback.Seek(Math.Max(0, lineTop));
            ApplyPosition();
        }
        PositionHighlight();
        VoiceStatus.Text = final ? "Matched spoken words. Holding here until you continue."
            : "Following live recognition - provisional words may be revised.";
        if (result.AtEnd && final)
        {
            _ = StopFollowingAsync();
            VoiceStatus.Text = "End of script - microphone stopped.";
        }
        UpdatePlaybackState();
    }

    private void PositionHighlight()
    {
        if (ActiveWordHighlight is null || highlightedWord < 0 || highlightedWord >= scriptWords.Count || !Options.VoiceFollow)
        {
            if (ActiveWordHighlight is not null) ActiveWordHighlight.Visibility = Visibility.Collapsed;
            return;
        }
        var (run, paragraph) = scriptWords[highlightedWord];
        var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) { ActiveWordHighlight.Visibility = Visibility.Collapsed; return; }
        var point = paragraph.TranslatePoint(new Point(rect.X, rect.Y), ReadingArea);
        ActiveWordText.Text = run.Text;
        ActiveWordText.FontSize = Options.FontSize * 1.1;
        Canvas.SetLeft(ActiveWordHighlight, Math.Max(0, point.X - 2));
        Canvas.SetTop(ActiveWordHighlight, point.Y - 2);
        ActiveWordHighlight.Visibility = point.Y >= 0 && point.Y < ReadingArea.ActualHeight
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void AnchorAtWord(int index)
    {
        Pause();
        if (index < 0 || index >= scriptWords.Count) return;
        matcher.Reset(index);
        highlightedWord = index;
        VoiceStatus.Text = "Position selected. Start following and read from the highlighted word.";
        PositionHighlight();
        UpdatePlaybackState();
    }

    private void ResetVoiceAnchor(int nextWord)
    {
        matcher.Reset(Math.Clamp(nextWord, 0, matcher.WordCount));
        highlightedWord = -1;
        if (VoiceStatus is not null) VoiceStatus.Text = "Position changed. Start following from here.";
        PositionHighlight();
    }

    private int WordAtReadingPosition()
    {
        for (int i = 0; i < scriptWords.Count; i++)
        {
            var (run, paragraph) = scriptWords[i];
            var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty && paragraph.TranslatePoint(new Point(rect.X, rect.Y), Paragraphs).Y >= playback.Position - 1)
                return i;
        }
        return Math.Max(0, scriptWords.Count - 1);
    }

    internal async Task CloseReaderAsync()
    {
        if (closeRequested) { await readerClosed.Task; return; }
        closeRequested = true;
        IsEnabled = false;
        Pause();
        await StopFollowingAsync();
        if (voiceCleanupFailed)
            MessageBox.Show(this, "Microphone cleanup reported a problem. Check Windows microphone activity before continuing.",
                "Teleprompter audio cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
        closed = true;
        _ = Dispatcher.BeginInvoke(Close);
        await readerClosed.Task;
    }

    private async void Reader_Closing(object? sender, CancelEventArgs e)
    {
        if (closed || voiceInput is null && voiceStop.IsCompleted) return;
        e.Cancel = true;
        if (closeRequested) return;
        await CloseReaderAsync();
    }
}
