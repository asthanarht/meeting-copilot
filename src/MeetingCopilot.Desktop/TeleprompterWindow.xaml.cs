using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class TeleprompterWindow : Window
{
    private readonly PrompterPlayback playback = new();
    private readonly Stopwatch clock = new();
    private TimeSpan lastFrame;
    private bool rendering;
    private bool layingOut;
    private bool closed;
    private double[] paragraphOffsets = [];
    private readonly List<(Run Run, TextBlock Paragraph)> scriptWords = [];
    private readonly List<int> paragraphWordStarts = [];
    private readonly ScriptSpeechMatcher matcher;
    private int highlightedWord = -1;
    internal PrompterOptions Options { get; private set; }
    internal bool IsPlaying => playback.IsPlaying;
    internal double Position => playback.Position;

    internal TeleprompterWindow(PresentationScript script, bool excludeFromCapture,
        Func<PrompterVoiceConnection>? voiceConnectionFactory = null)
    {
        script.Options.Validate();
        if (string.IsNullOrWhiteSpace(script.Text) || script.Text.Length > ScriptStore.MaxTextLength)
            throw new ArgumentException("A non-empty script of up to 100,000 characters is required.", nameof(script));
        Options = script.Options;
        this.voiceConnectionFactory = voiceConnectionFactory ?? PrompterVoiceConnection.FromSavedSettings;
        InitializeComponent();
        ScriptTitle.Text = script.Title;
        Title = "Teleprompter - " + script.Title;
        foreach (string paragraph in Regex.Split(script.Text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n[ \t]*\n+"))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            string text = paragraph.Trim();
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            paragraphWordStarts.Add(scriptWords.Count);
            int offset = 0;
            foreach (var word in ScriptSpeechMatcher.Tokenize(text))
            {
                block.Inlines.Add(new Run(text[offset..word.Start]));
                var run = new Run(word.Text);
                int index = scriptWords.Count;
                run.MouseLeftButtonDown += (_, e) => { AnchorAtWord(index); e.Handled = true; };
                block.Inlines.Add(run);
                scriptWords.Add((run, block));
                offset = word.Start + word.Length;
            }
            block.Inlines.Add(new Run(text[offset..]));
            Paragraphs.Children.Add(block);
        }
        matcher = new(scriptWords.Select(w => w.Run.Text));
        ExcludeCaptureBox.IsChecked = excludeFromCapture;
        SourceInitialized += (_, _) => ApplyCaptureProtection();
        Loaded += (_, _) => { ArrangeReading(); if (Options.VoiceFollow) PrepareVoiceConnection(); ReadingArea.Focus(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Pause(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Pause(); };
        Closed += (_, _) => { closed = true; Pause(); voiceConnection = null; readerClosed.TrySetResult(); };
        Closing += Reader_Closing;
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 48));
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 48));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 24;
        ModeTabs.SelectedIndex = Options.VoiceFollow ? 0 : 1;
        ApplyMode();
        RefreshLabels();
    }

    private void ArrangeReading()
    {
        if (!IsLoaded || layingOut || closed) return;
        layingOut = true;
        int anchor = CurrentParagraph;
        double within = paragraphOffsets.Length > 0 ? playback.Position - paragraphOffsets[anchor] : 0;
        double oldHeight = anchor + 1 < paragraphOffsets.Length ? paragraphOffsets[anchor + 1] - paragraphOffsets[anchor]
            : Math.Max(1, playback.End - (paragraphOffsets.Length > 0 ? paragraphOffsets[anchor] : 0));
        try
        {
            Pause();
            ReadingContent.Width = Math.Max(120, Math.Min(Options.ReadingWidth, ReaderScroll.ActualWidth - 76));
            for (int i = 0; i < Paragraphs.Children.Count; i++)
            {
                var paragraph = (TextBlock)Paragraphs.Children[i];
                paragraph.FontSize = Options.FontSize;
                paragraph.LineHeight = Options.FontSize * Options.LineSpacing;
                paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                paragraph.Margin = new Thickness(0, 0, 0, i == Paragraphs.Children.Count - 1 ? 0 : Options.FontSize);
            }
            EndSpace.Height = Math.Max(0, ReaderScroll.ViewportHeight - 48 - Options.FontSize * Options.LineSpacing);
            ReaderScroll.UpdateLayout();
            paragraphOffsets = Paragraphs.Children.Cast<FrameworkElement>()
                .Select(p => p.TranslatePoint(new Point(), Paragraphs).Y).ToArray();
            playback.SetExtent(Math.Max(0, Math.Min(ReaderScroll.ScrollableHeight,
                Paragraphs.ActualHeight - Options.FontSize * Options.LineSpacing)));
            if (paragraphOffsets.Length > 0)
            {
                anchor = Math.Min(anchor, paragraphOffsets.Length - 1);
                double newHeight = anchor + 1 < paragraphOffsets.Length ? paragraphOffsets[anchor + 1] - paragraphOffsets[anchor]
                    : Math.Max(1, playback.End - paragraphOffsets[anchor]);
                playback.Seek(paragraphOffsets[anchor] + within / oldHeight * newHeight);
            }
            ApplyPosition();
            RefreshLabels();
        }
        finally { layingOut = false; }
    }

    private int CurrentParagraph => Math.Max(0, Array.FindLastIndex(paragraphOffsets, offset => offset <= playback.Position + 1));

    private void Reader_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeReading();
    private void ReadingSettings_Expanded(object sender, RoutedEventArgs e)
    {
        Pause();
        Height = Math.Max(Height, Math.Min(800, SystemParameters.WorkArea.Height - 48));
        ReadingSettings.Collapsed -= ReadingSettings_Collapsed;
        ReadingSettings.Collapsed += ReadingSettings_Collapsed;
    }

    private void ReadingSettings_Collapsed(object sender, RoutedEventArgs e) => ArrangeReading();

    private void SetOptions(PrompterOptions options, bool reflow)
    {
        options.Validate();
        Options = options;
        playback.PixelsPerSecond = options.Speed;
        if (reflow) ArrangeReading();
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        playback.PixelsPerSecond = Options.Speed;
        FontLabel.Text = $"Text size: {Options.FontSize}";
        SpacingLabel.Text = $"Line spacing: {Options.LineSpacing:F1}";
        WidthLabel.Text = $"Reading width: {Options.ReadingWidth}";
        SpeedText.Text = $"{Options.Speed} px / second";
        SlowerButton.IsEnabled = Options.Speed > 5;
        FasterButton.IsEnabled = Options.Speed < 120;
        SmallerButton.IsEnabled = Options.FontSize > 20;
        LargerButton.IsEnabled = Options.FontSize < 60;
        TighterButton.IsEnabled = Options.LineSpacing > 1.2;
        LooserButton.IsEnabled = Options.LineSpacing < 2;
        NarrowerButton.IsEnabled = Options.ReadingWidth > 320;
        WiderButton.IsEnabled = Options.ReadingWidth < 960;
        UpdatePlaybackState();
    }

    private void UpdatePlaybackState()
    {
        if (Options.VoiceFollow)
        {
            PlayButton.Content = voiceStarting ? "Connecting..." : voiceActive ? "Pause following" : "Start following";
            PlayButton.IsEnabled = !voiceStarting && (voiceActive || VoiceConsentBox.IsChecked == true &&
                voiceConnection is not null && !voiceCleanupFailed && !matcher.AtEnd && scriptWords.Count > 0);
            PlaybackStatus.Text = voiceActive ? "Following matched speech - no timed scrolling."
                : "Voice following paused. No microphone audio is being sent.";
            if (voiceCleanupFailed) PlaybackStatus.Text = "Audio cleanup reported a problem. See the error above.";
            ProgressText.Text = $"{Math.Max(0, matcher.CurrentWord + 1)} / {matcher.WordCount} words";
            PreviousButton.IsEnabled = matcher.CurrentWord >= 0 || playback.Position > 0;
            NextButton.IsEnabled = CurrentParagraph + 1 < paragraphOffsets.Length;
            return;
        }
        PlayButton.Content = playback.IsPlaying ? "Pause" : "Play";
        PlayButton.IsEnabled = !playback.IsAtEnd;
        PlaybackStatus.Text = playback.IsAtEnd ? "End of script. Restart or return to a previous paragraph."
            : playback.IsPlaying ? "Playing at a fixed pace - pause when you need a moment."
            : "Paused - press Play when you are ready.";
        ProgressText.Text = $"{(playback.End > 0 ? playback.Position / playback.End : 1):P0}";
        PreviousButton.IsEnabled = playback.Position > 0;
        NextButton.IsEnabled = CurrentParagraph + 1 < paragraphOffsets.Length;
    }

    internal void TogglePlayback()
    {
        if (Options.VoiceFollow) return;
        if (playback.IsPlaying) { Pause(); return; }
        if (ReadingSettings.IsExpanded)
        {
            ReadingSettings.IsExpanded = false;
            UpdateLayout();
            ArrangeReading();
        }
        playback.Play();
        if (playback.IsPlaying && !rendering)
        {
            clock.Restart();
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
            rendering = true;
        }
        UpdatePlaybackState();
    }

    internal void Pause()
    {
        _ = StopFollowingAsync();
        playback.Pause();
        if (rendering) CompositionTarget.Rendering -= OnRendering;
        rendering = false;
        clock.Stop();
        if (PlayButton is not null) UpdatePlaybackState();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = clock.Elapsed;
        AdvanceFrame(now - lastFrame);
        lastFrame = now;
    }

    internal void AdvanceFrame(TimeSpan elapsed)
    {
        if (Options.VoiceFollow) return;
        playback.Advance(elapsed);
        ApplyPosition();
        if (!playback.IsPlaying) Pause();
        if (elapsed > TimeSpan.FromSeconds(1) && !playback.IsAtEnd)
            PlaybackStatus.Text = "Paused after an interruption. Your reading position was kept.";
    }

    private void ApplyPosition()
    {
        bool previous = layingOut;
        layingOut = true;
        try
        {
            ReaderScroll.ScrollToVerticalOffset(playback.Position);
            ReaderScroll.UpdateLayout();
        }
        finally { layingOut = previous; }
        UpdatePlaybackState();
        PositionHighlight();
    }

    internal void SeekParagraph(int direction)
    {
        Pause();
        if (paragraphOffsets.Length == 0) return;
        int index = CurrentParagraph;
        if (direction > 0) index = Math.Min(index + 1, paragraphOffsets.Length - 1);
        else if (playback.Position <= paragraphOffsets[index] + 1) index = Math.Max(0, index - 1);
        playback.Seek(paragraphOffsets[index]);
        if (Options.VoiceFollow && index < paragraphWordStarts.Count) ResetVoiceAnchor(paragraphWordStarts[index]);
        ApplyPosition();
    }

    private void Reader_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (layingOut || e.VerticalChange == 0 || playback.IsPlaying) return;
        playback.Seek(ReaderScroll.VerticalOffset);
        if (Options.VoiceFollow) ResetVoiceAnchor(WordAtReadingPosition());
        UpdatePlaybackState();
        PositionHighlight();
    }

    private void Reader_ManualScroll(object sender, MouseWheelEventArgs e) => Pause();
    private void Reader_MouseDown(object sender, MouseButtonEventArgs e) => Pause();

    private async void Reader_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat && e.Key is Key.Space or Key.Escape) { e.Handled = true; return; }
        bool control = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Space && (control || Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not ButtonBase))
            await ToggleReaderAsync();
        else if (e.Key == Key.Escape) Pause();
        else if (control && e.Key == Key.Up) Faster_Click(this, e);
        else if (control && e.Key == Key.Down) Slower_Click(this, e);
        else if (control && e.Key == Key.Home) Restart_Click(this, e);
        else if (e.Key == Key.PageDown) SeekParagraph(1);
        else if (e.Key == Key.PageUp) SeekParagraph(-1);
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            Pause();
            playback.Seek(e.Key switch
            {
                Key.Home => 0,
                Key.End => playback.End,
                Key.Up => playback.Position - Options.FontSize * Options.LineSpacing,
                _ => playback.Position + Options.FontSize * Options.LineSpacing
            });
            if (Options.VoiceFollow) ResetVoiceAnchor(WordAtReadingPosition());
            ApplyPosition();
        }
        else return;
        e.Handled = true;
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await ToggleReaderAsync();
    private void Previous_Click(object sender, RoutedEventArgs e) => SeekParagraph(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => SeekParagraph(1);
    private void Restart_Click(object sender, RoutedEventArgs e) { Pause(); playback.Seek(0); ResetVoiceAnchor(0); ApplyPosition(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Slower_Click(object sender, RoutedEventArgs e) { if (!Options.VoiceFollow) SetOptions(Options with { Speed = Math.Max(5, Options.Speed - 5) }, false); }
    private void Faster_Click(object sender, RoutedEventArgs e) { if (!Options.VoiceFollow) SetOptions(Options with { Speed = Math.Min(120, Options.Speed + 5) }, false); }
    private void Smaller_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { FontSize = Math.Max(20, Options.FontSize - 2) }, true);
    private void Larger_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { FontSize = Math.Min(60, Options.FontSize + 2) }, true);
    private void Tighter_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { LineSpacing = Math.Max(1.2, Math.Round(Options.LineSpacing - 0.1, 1)) }, true);
    private void Looser_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { LineSpacing = Math.Min(2, Math.Round(Options.LineSpacing + 0.1, 1)) }, true);
    private void Narrower_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { ReadingWidth = Math.Max(320, Options.ReadingWidth - 40) }, true);
    private void Wider_Click(object sender, RoutedEventArgs e) => SetOptions(Options with { ReadingWidth = Math.Min(960, Options.ReadingWidth + 40) }, true);

    private void CaptureChanged(object sender, RoutedEventArgs e) => ApplyCaptureProtection();
    private void ApplyCaptureProtection()
    {
        if (CaptureStatus is null || ExcludeCaptureBox is null) return;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        try
        {
            bool exclude = ExcludeCaptureBox.IsChecked == true;
            WindowCaptureProtection.Apply(handle, exclude);
            CaptureStatus.Text = exclude ? "Capture exclusion on - supported tools only; verify your sharing app."
                : "Capture exclusion off - this script may appear in screen sharing.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            CaptureStatus.Text = "Capture exclusion not confirmed. This script may appear in screen sharing; share only an application window.";
        }
    }
}
