using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using MeetingCopilot.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public partial class DesktopSmokeTests
{
    [Fact]
    public void CaptureExclusionRejectsAnUncreatedWindow()
    {
        Assert.Throws<ArgumentException>(() => WindowCaptureProtection.Apply(0, true));
        Assert.Throws<ArgumentException>(() => WindowCaptureProtection.Read(0));
    }

    [Fact]
    public void NativeSpeechRuntimeLoadsWithoutNetworkOrAudioCapture()
    {
        var config = SpeechConfig.FromSubscription("offline-placeholder-not-a-real-key", "eastus");
        using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        using var stream = AudioInputStream.CreatePushStream(format);
        using var audio = AudioConfig.FromStreamInput(stream);
        using var recognizer = new SpeechRecognizer(config, audio);
        Assert.NotNull(recognizer);
    }

    [Fact]
    public void WpfSetupAndPreviewRenderAndControlsWorkWithoutCredentials()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            string settingsPath = Path.Combine(Path.GetTempPath(), $"meeting-copilot-ui-{Guid.NewGuid():N}.json");
            try
            {
                var app = new App();
                app.InitializeComponent();
                var store = new SettingsStore(settingsPath);
                store.Save(new ConnectionSettings
                {
                    SpeechEndpoint = "https://test.cognitiveservices.azure.com/",
                    ModelEndpoint = "https://test.services.ai.azure.com/openai/v1",
                    Deployment = "test-deployment"
                });
                var main = new MainWindow(store);
                main.Show();
                main.UpdateLayout();
                Assert.Equal("Meeting copilot - setup", main.Title);
                Assert.Equal(Color.FromRgb(20, 27, 36), ((SolidColorBrush)main.Background).Color);
                AssertWindowAppearance(main);
                VerifyAppIcon();
                var prompterButton = (Button)main.FindName("TeleprompterButton");
                Assert.NotNull(ButtonIcon.GetData(prompterButton));
                Assert.Equal("Open teleprompter", new ButtonAutomationPeer(prompterButton).GetName());
                Assert.True(((Viewbox)prompterButton.Template.FindName("ButtonGlyph", prompterButton)).IsVisible);
                var previewButton = (Button)main.FindName("PreviewButton");
                Assert.Equal(Visibility.Collapsed, ((Viewbox)previewButton.Template.FindName("ButtonGlyph", previewButton)).Visibility);
                var setupTabs = (TabControl)main.FindName("SetupTabs");
                Assert.Equal(0, setupTabs.SelectedIndex);
                SaveImage(main, "professional-setup.png");
                SaveNativeWindowImage(main, "themed-titlebar-setup.png");
                main.Width = 560;
                main.Height = 600;
                main.UpdateLayout();
                AssertWithinWindow((Button)main.FindName("StartButton"), main);
                Assert.True(((ScrollViewer)main.FindName("MeetingScroll")).ScrollableHeight > 0);
                SaveImage(main, "professional-setup-narrow.png");
                main.Width = 780;
                main.Height = 900;
                setupTabs.SelectedIndex = 1;
                main.UpdateLayout();
                var provider = (ComboBox)main.FindName("SpeechProviderBox");
                PumpUntil(() => provider.IsLoaded);
                provider.Focus();
                provider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, 0, Key.F4)
                { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.True(provider.IsDropDownOpen);
                main.UpdateLayout();
                Assert.NotNull(provider.Template.FindName("PART_Popup", provider));
                provider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.False(provider.IsDropDownOpen);
                ((Button)main.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, setupTabs.SelectedIndex);
                Assert.Contains("consent", ((TextBlock)main.FindName("ErrorText")).Text);
                setupTabs.SelectedIndex = 1;
                ((TextBlock)main.FindName("ErrorText")).Text = "";
                Assert.Equal(0, provider.SelectedIndex);
                Assert.Equal(Visibility.Visible, ((StackPanel)main.FindName("MaiSettingsPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)main.FindName("StreamingSettingsPanel")).Visibility);
                provider.SelectedIndex = 1;
                Assert.Equal(Visibility.Collapsed, ((StackPanel)main.FindName("MaiSettingsPanel")).Visibility);
                Assert.Equal(Visibility.Visible, ((StackPanel)main.FindName("StreamingSettingsPanel")).Visibility);
                ((TextBox)main.FindName("SpeechEndpointBox")).BringIntoView();
                main.UpdateLayout();
                Assert.True(((TextBox)main.FindName("SpeechEndpointBox")).IsVisible);
                Assert.InRange(((TextBox)main.FindName("SpeechEndpointBox")).ActualHeight, 38, 42);
                SaveImage(main, "streaming-setup.png");
                provider.SelectedIndex = 0;
                var modelConnection = (ComboBox)main.FindName("ModelConnectionBox");
                Assert.Equal(0, modelConnection.SelectedIndex);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)main.FindName("ModelKeyPanel")).Visibility);
                modelConnection.SelectedIndex = 1;
                Assert.Equal(Visibility.Visible, ((StackPanel)main.FindName("ModelKeyPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EntraHelp")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)main.FindName("EntraTenantPanel")).Visibility);
                modelConnection.SelectedIndex = 0;
                Assert.Equal(Visibility.Visible, ((StackPanel)main.FindName("EntraTenantPanel")).Visibility);
                modelConnection.SelectedIndex = 2;
                Assert.Equal(Visibility.Visible, ((StackPanel)main.FindName("ModelKeyPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)main.FindName("EntraTenantPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EntraHelp")).Visibility);
                Assert.Contains("/openai/v1", ((TextBlock)main.FindName("ModelEndpointLabel")).Text);
                SaveImage(main, "setup.png");
                ((StackPanel)main.FindName("ModelKeyPanel")).BringIntoView();
                main.UpdateLayout();
                SaveImage(main, "responses-key-setup.png");
                AssertTextContrast(app, "Foreground", "Surface");
                AssertTextContrast(app, "Secondary", "Raised");
                AssertTextContrast(app, "AccentInk", "Accent");
                AssertTextContrast(app, "SelectedText", "Selection");
                CheckExternalProviderSettings();
                ExportDemoScenes();

                ((PasswordBox)main.FindName("SpeechKeyBox")).Password = "synthetic-ui-speech-key";
                ((PasswordBox)main.FindName("ModelKeyBox")).Password = "synthetic-ui-model-key";
                ((TextBox)main.FindName("DeploymentBox")).Text = "remember-this-deployment";
                ((TextBox)main.FindName("BriefBox")).Text = "private-meeting-brief-not-for-config";
                ((TextBox)main.FindName("PhrasesBox")).Text = "meeting-specific-vocabulary";
                ((CheckBox)main.FindName("ConsentBox")).IsChecked = true;
                ((CheckBox)main.FindName("ExcludeCaptureBox")).IsChecked = false;
                ((Button)main.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                string persisted = File.ReadAllText(settingsPath);
                Assert.DoesNotContain("synthetic-ui-speech-key", persisted);
                Assert.DoesNotContain("private-meeting-brief", persisted);
                Assert.DoesNotContain("meeting-specific-vocabulary", persisted);
                Assert.DoesNotContain("Consent", persisted);
                var restored = new MainWindow(store);
                Assert.Equal(2, ((ComboBox)restored.FindName("ModelConnectionBox")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((StackPanel)restored.FindName("ModelKeyPanel")).Visibility);
                Assert.Equal("synthetic-ui-speech-key", ((PasswordBox)restored.FindName("SpeechKeyBox")).Password);
                Assert.Equal("synthetic-ui-model-key", ((PasswordBox)restored.FindName("ModelKeyBox")).Password);
                Assert.Equal("remember-this-deployment", ((TextBox)restored.FindName("DeploymentBox")).Text);
                Assert.Equal("", ((TextBox)restored.FindName("BriefBox")).Text);
                Assert.Equal(false, ((CheckBox)restored.FindName("ConsentBox")).IsChecked);
                Assert.Equal(false, ((CheckBox)restored.FindName("ExcludeCaptureBox")).IsChecked);
                ((TextBox)restored.FindName("DeploymentBox")).Text = "saved-on-close";
                restored.Close();
                Assert.Equal("saved-on-close", store.Load()!.Deployment);
                restored = new MainWindow(store);
                ((Button)restored.FindName("ForgetSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(File.Exists(settingsPath));
                Assert.Equal("", ((PasswordBox)restored.FindName("SpeechKeyBox")).Password);
                restored.Close();
                var fresh = new MainWindow(store);
                Assert.Equal(2, ((ComboBox)fresh.FindName("ModelConnectionBox")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((StackPanel)fresh.FindName("ModelKeyPanel")).Visibility);
                Assert.Equal(1, ((ComboBox)fresh.FindName("SpeechProviderBox")).SelectedIndex);
                Assert.Equal(Visibility.Visible, ((StackPanel)fresh.FindName("StreamingSettingsPanel")).Visibility);
                Assert.Equal(true, ((CheckBox)fresh.FindName("ExcludeCaptureBox")).IsChecked);
                fresh.Close();

                var copied = new List<string>();
                bool clipboardUnavailable = false;
                var overlay = new OverlayWindow(null, true, text =>
                {
                    if (clipboardUnavailable) throw new ExternalException("Synthetic clipboard busy");
                    copied.Add(text);
                });
                overlay.Show();
                overlay.UpdateLayout();
                nint handle = new WindowInteropHelper(overlay).Handle;
                Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(handle));
                Assert.Contains("Capture exclusion on", ((TextBlock)overlay.FindName("CaptureProtectionStatus")).Text);
                ((CheckBox)overlay.FindName("CaptureExclusionBox")).IsChecked = false;
                Assert.Equal(0u, WindowCaptureProtection.Read(handle));
                Assert.Contains("Capture exclusion off", ((TextBlock)overlay.FindName("CaptureProtectionStatus")).Text);
                ((CheckBox)overlay.FindName("CaptureExclusionBox")).IsChecked = true;
                Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(handle));
                Assert.Equal(Color.FromRgb(20, 27, 36), ((SolidColorBrush)overlay.Background).Color);
                var cue = (TextBlock)overlay.FindName("CueText");
                var typeTag = (Label)overlay.FindName("CueTypeTag");
                Assert.Equal("Question", typeTag.Content);
                Assert.True(typeTag.IsVisible);
                Assert.Equal("Question", new LabelAutomationPeer(typeTag).GetName());
                foreach (var (kind, label) in new[] { ("question", "Question"), ("recommendation", "Improvement"),
                    ("gap", "Potential gap"), ("alternative", "Alternative") })
                {
                    var tag = new Label { Style = (Style)overlay.FindResource("SuggestionTypeTag"), Tag = kind };
                    Assert.Equal(label, tag.Content);
                    Assert.Equal(Visibility.Visible, tag.Visibility);
                }
                Assert.Equal(Visibility.Collapsed, ((ScrollViewer)overlay.FindName("Body")).Visibility);
                Assert.True(cue.IsVisible);
                var copy = (Button)overlay.FindName("CopyButton");
                var copyStatus = (TextBlock)overlay.FindName("CopyStatus");
                Assert.True(copy.IsEnabled);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(cue.Text, Assert.Single(copied));
                Assert.Contains("Copied to clipboard", copyStatus.Text);
                clipboardUnavailable = true;
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Try Copy again", copyStatus.Text);
                Assert.Single(copied);
                clipboardUnavailable = false;
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, copied.Count);
                Assert.Contains("Copied to clipboard", copyStatus.Text);
                SaveImage(overlay, "professional-focus.png");
                overlay.Width = 520;
                overlay.Height = 500;
                overlay.UpdateLayout();
                AssertWithinWindow((Button)overlay.FindName("FindButton"), overlay);
                AssertWithinWindow(copy, overlay);
                AssertWithinWindow(cue, overlay);
                AssertWithinWindow(typeTag, overlay);
                Assert.Equal(560, overlay.ActualHeight);
                var cueViewport = (ScrollViewer)overlay.FindName("CueScroll");
                Assert.True(cueViewport.ViewportHeight >= typeTag.ActualHeight + cue.ActualHeight + 28);
                SaveImage(overlay, "professional-focus-narrow.png");
                overlay.Width = 660;
                ((Button)overlay.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                overlay.UpdateLayout();
                Assert.Contains("duplicate charge", cue.Text);
                Assert.Contains("PREVIEW", ((TextBlock)overlay.FindName("CaptureStatus")).Text);
                var transcript = (TextBox)overlay.FindName("TranscriptText");
                var context = (TextBox)overlay.FindName("ContextText");
                Assert.True(transcript.IsReadOnly);
                Assert.True(context.IsReadOnly);
                Assert.True(transcript.IsVisible);
                var contextTabs = (TabControl)overlay.FindName("ContextTabs");
                Assert.False(context.IsVisible);
                contextTabs.SelectedIndex = 1;
                overlay.UpdateLayout();
                Assert.True(context.IsVisible);
                SaveImage(overlay, "professional-context.png");
                contextTabs.SelectedIndex = 0;
                overlay.UpdateLayout();
                Assert.Contains("retry the payment", transcript.Text);
                Assert.Contains("Synthetic technical design review", context.Text);
                Assert.Contains("Prewritten sample memory", context.Text);
                double cueTop = cue.TranslatePoint(new Point(0, 0), overlay).Y;
                ((ScrollViewer)overlay.FindName("Body")).ScrollToEnd();
                overlay.UpdateLayout();
                Assert.Equal(cueTop, cue.TranslatePoint(new Point(0, 0), overlay).Y);
                Assert.True(cue.IsVisible);
                ((ScrollViewer)overlay.FindName("Body")).ScrollToTop();
                overlay.UpdateLayout();
                SaveImage(overlay, "expanded-context.png");
                ((Expander)overlay.FindName("WhyPanel")).IsExpanded = true;
                overlay.Height = 610;
                overlay.UpdateLayout();
                SaveImage(overlay, "overlay.png");
                ((Button)overlay.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Stay with the conversation.", cue.Text);
                Assert.Equal(Visibility.Collapsed, typeTag.Visibility);
                Assert.False(copy.IsEnabled);
                Assert.Empty(copyStatus.Text);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, copied.Count);
                ((Button)overlay.FindName("FindButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("revoked permissions", cue.Text);
                Assert.Contains("cache customer permissions", transcript.Text);
                string beforeCompact = context.Text;
                contextTabs.SelectedIndex = 1;
                ((Button)overlay.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, ((ScrollViewer)overlay.FindName("Body")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((StackPanel)overlay.FindName("DetailsPanel")).Visibility);
                Assert.True(cue.IsVisible);
                Assert.True(((Button)overlay.FindName("FindButton")).IsVisible);
                Assert.False(transcript.IsVisible);
                Assert.False(context.IsVisible);
                Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(handle));
                overlay.UpdateLayout();
                SaveImage(overlay, "compact.png");
                ((Button)overlay.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(context.IsVisible);
                Assert.Equal(beforeCompact, context.Text);
                app.ApplyContrastPalette(true);
                overlay.UpdateLayout();
                AssertWindowAppearance(overlay, highContrast: true);
                AssertWindowAppearance(main, highContrast: true);
                Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)overlay.Background).Color);
                Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)app.Resources["AccentInk"]).Color);
                Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)typeTag.Foreground).Color);
                Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)typeTag.Background).Color);
                app.ApplyContrastPalette(false);
                overlay.UpdateLayout();
                AssertWindowAppearance(overlay);
                AssertWindowAppearance(main);
                Assert.Equal(Color.FromRgb(20, 27, 36), ((SolidColorBrush)overlay.Background).Color);
                ((Button)overlay.FindName("FindButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("backoff", cue.Text);
                Assert.Equal("Improvement", typeTag.Content);
                Assert.Equal("Why this recommendation?", ((Expander)overlay.FindName("WhyPanel")).Header);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(cue.Text, copied[^1]);
                overlay.Height = 860;
                overlay.UpdateLayout();
                SaveImage(overlay, "recommendation-copy.png");
                ((Button)overlay.FindName("BrowseQuestionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                overlay.UpdateLayout();
                var historyList = (ListBox)overlay.FindName("QuestionHistoryList");
                var historyTags = VisualLabels(historyList).Where(label => label.Name == "HistoryTypeTag").ToArray();
                Assert.Contains(historyTags, label => Equals(label.Content, "Question"));
                Assert.Contains(historyTags, label => Equals(label.Content, "Improvement"));
                Assert.All(historyTags, label =>
                {
                    var entry = Assert.IsType<QuestionEntry>(label.DataContext);
                    Assert.Equal(entry.Cue.Kind, label.Tag);
                    Assert.True(label.IsVisible);
                });
                SaveImage(overlay, "suggestion-type-history.png");
                ((Button)overlay.FindName("BrowseQuestionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)overlay.FindName("PreviousQuestionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)overlay.FindName("PreviousQuestionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("revoked permissions", cue.Text);
                Assert.Equal("Question", typeTag.Content);
                Assert.Empty(copyStatus.Text);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(cue.Text, copied[^1]);
                overlay.Close();
                var optedOut = new OverlayWindow(null, false);
                optedOut.Show();
                Assert.Equal(0u, WindowCaptureProtection.Read(new WindowInteropHelper(optedOut).Handle));
                Assert.Contains("Capture exclusion off", ((TextBlock)optedOut.FindName("CaptureProtectionStatus")).Text);
                optedOut.Close();
                VerifySlidePreviewAndCapture();
                VerifyLiveContextWithoutDevices();
                VerifyTeleprompterWithoutServices();
                VerifyVoiceFollowingWithoutDevices();
                VerifySingleScreenNavigation(main);
                main.Close();
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
            finally { File.Delete(settingsPath); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF smoke test timed out.");
        Assert.Null(failure);
    }

    private static void VerifyLiveContextWithoutDevices()
    {
        var playback = new ContextInput("Meeting playback");
        var microphone = new ContextInput("Your microphone");
        var settings = new SessionSettings("synthetic-key", "eastus", "en-US", "playback", "microphone",
            [], "Review payment retries.", new("https://test.openai.azure.com", "test", "synthetic-key"));
        var model = new ContextModel();
        var session = new MeetingSession(settings, (_, loopback) => loopback ? playback : microphone, model);
        var copied = new List<string>();
        var window = new OverlayWindow(session, true, copied.Add);
        try
        {
            window.Show();
            ((Button)window.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TabControl)window.FindName("ContextTabs")).SelectedIndex = 1;
            window.UpdateLayout();
            var start = window.StartLiveAsync();
            PumpUntil(() => start.IsCompleted);
            start.GetAwaiter().GetResult();
            var partial = (TextBox)window.FindName("ProvisionalText");
            var transcript = (TextBox)window.FindName("TranscriptText");
            var context = (TextBox)window.FindName("ContextText");
            Assert.Contains("Review payment retries.", context.Text);
            Assert.Contains("Not generated yet", context.Text);
            Assert.Contains("Waiting for finalized speech", ((TextBlock)window.FindName("AnalysisText")).Text);
            Assert.True(((Button)window.FindName("CaptureSlideButton")).IsEnabled);
            string navigationPath = Path.Combine(Path.GetTempPath(), $"meeting-navigation-{Guid.NewGuid():N}.json");
            var scripts = ScriptLibraryWindow.Open(window, true, new ScriptStore(navigationPath));
            Assert.False(window.IsVisible);
            Assert.True(scripts.IsVisible);
            Assert.True(session.IsRunning);
            playback.Publish("The meeting continues while I look at my scripts.", true);
            PumpUntil(() => session.GetContextSnapshot().Utterances.Any(u => u.Text.Contains("look at my scripts")));
            scripts.Close();
            Assert.True(window.IsVisible);
            Assert.True(session.IsRunning);
            window.Activate();
            Assert.True(context.Focus());
            Assert.True(context.IsKeyboardFocusWithin);

            playback.Publish("We could retry...", false);
            microphone.Publish("What about duplicates...", false);
            PumpUntil(() => partial.Text.Contains("What about duplicates"));
            Assert.Contains("We could retry", partial.Text);
            Assert.DoesNotContain("We could retry", transcript.Text);
            Assert.DoesNotContain(session.GetContextSnapshot().Utterances, u => u.Text.Contains("We could retry"));

            playback.Publish("We retry payments after a timeout.", true);
            PumpUntil(() => transcript.Text.Contains("We retry payments"));
            Assert.DoesNotContain("We could retry", partial.Text);
            Assert.Contains("What about duplicates", partial.Text);
            microphone.Publish("We have not decided how to prevent duplicate charges.", true);
            PumpUntil(() => transcript.Text.Contains("duplicate charges"));
            Assert.Contains("Meeting playback", transcript.Text);
            Assert.Contains("Your microphone", transcript.Text);
            PumpUntil(() => context.Text.Contains("Synthetic model memory"));
            PumpUntil(() => ((TextBlock)window.FindName("CueText")).Text.Contains("duplicate charges"));
            Assert.Contains("Suggestion ready", ((TextBlock)window.FindName("AnalysisText")).Text);
            Assert.DoesNotContain("synthetic-key", context.Text);
            var timing = (TextBlock)window.FindName("TimingText");
            PumpUntil(() => timing.Text.Contains("Last successful analysis:") && timing.Text.Contains("Waiting for new speech."));
            var successfulRevision = session.GetAnalysisProgress().AnalyzedRevision;
            string initialTiming = timing.Text;
            PumpUntil(() => timing.Text != initialTiming);
            Assert.Equal(successfulRevision, session.GetAnalysisProgress().AnalyzedRevision);
            Assert.Contains("Latest final speech:", timing.Text);
            Assert.Contains("total context items received", ((TextBlock)window.FindName("ContextStatus")).Text);

            var cue = (TextBlock)window.FindName("CueText");
            var covered = (Button)window.FindName("CoveredButton");
            var copy = (Button)window.FindName("CopyButton");
            Assert.True(copy.Focus());
            model.QuestionText = "How many retry attempts should we allow?";
            playback.Publish("We also need to decide the maximum retry count.", true);
            PumpUntil(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("Provisional"));
            Assert.Contains("duplicate charges", cue.Text);
            PumpUntil(() => !string.IsNullOrEmpty(((TextBlock)window.FindName("PendingText")).Text));
            Assert.Contains("duplicate charges", cue.Text);
            Assert.True(((Button)window.FindName("ShowPendingButton")).IsVisible);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(cue.Text, Assert.Single(copied));
            Assert.NotEqual(model.QuestionText, copied[0]);
            ((Button)window.FindName("ShowPendingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(model.QuestionText, cue.Text);
            Assert.True(covered.Focus());
            Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("ShowPendingButton")).Visibility);

            model.QuestionText = "What delay should we use between retries?";
            playback.Publish("We also need to choose the retry delay.", true);
            PumpUntil(() => !string.IsNullOrEmpty(((TextBlock)window.FindName("PendingText")).Text));
            Assert.True(context.Focus());
            PumpUntil(() => cue.Text == model.QuestionText);

            ((Button)window.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(cue.IsVisible);
            Assert.False(context.IsVisible);
            Assert.True(((Button)window.FindName("FindButton")).Focus());
            model.QuestionText = "Should retry delays include jitter?";
            playback.Publish("Many clients may retry at the same time.", true);
            PumpUntil(() => cue.Text == model.QuestionText);
            Assert.True(cue.IsVisible);

            Assert.True(covered.Focus());
            model.Addressed = true;
            playback.Publish("The retry count is now decided: at most three attempts.", true);
            PumpUntil(() => cue.Text == "Stay with the conversation.");
            Assert.Contains("Addressed", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal("", ((TextBlock)window.FindName("PendingText")).Text);

            model.Addressed = false;
            model.SilenceReason = "Only greetings remain; the technical questions have been answered.";
            ((Button)window.FindName("FindButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((TextBlock)window.FindName("AnalysisText")).Text.Contains(model.SilenceReason));
            Assert.True(model.LastQuestionRequested);
            Assert.Equal("Stay with the conversation.", cue.Text);

            var historyBox = (ListBox)window.FindName("QuestionHistoryList");
            ((Button)window.FindName("BrowseQuestionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(historyBox.Items.Count >= 4);
            historyBox.SelectedIndex = 0;
            string historicalText = cue.Text;
            Assert.Contains("History", ((TextBlock)window.FindName("StatusText")).Text);
            model.SilenceReason = null;
            model.QuestionText = "Who monitors exhausted retries?";
            int previousCount = historyBox.Items.Count;
            playback.Publish("We have not assigned retry monitoring.", true);
            PumpUntil(() => historyBox.Items.Count > previousCount);
            Assert.Equal(historicalText, cue.Text);
            Assert.Contains("new", ((Button)window.FindName("LiveQuestionButton")).Content.ToString());
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(historicalText, copied[^1]);
            Assert.NotEqual(model.QuestionText, copied[^1]);
            covered.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(model.QuestionText, session.GetContextSnapshot().CurrentCue!.Text);
            Assert.Contains("Covered", ((TextBlock)window.FindName("StatusText")).Text);
            ((Button)window.FindName("LiveQuestionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(model.QuestionText, cue.Text);
            ((Button)window.FindName("PreviousQuestionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("History", ((TextBlock)window.FindName("HistoryPositionText")).Text);
            ((Button)window.FindName("LiveQuestionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var slide = window.SendSlideAsync(SyntheticSlide.Png(), DateTimeOffset.UtcNow, default);
            PumpUntil(() => slide.IsCompleted);
            slide.GetAwaiter().GetResult();
            Assert.Contains("30 days", ((TextBox)window.FindName("SlideContextText")).Text);
            Assert.DoesNotContain("30 days", transcript.Text);
            Assert.Contains("Slide added", ((TextBlock)window.FindName("SlideStatusText")).Text);
            Assert.Single(session.GetContextSnapshot().Utterances, u => u.Kind == "slide");
            ((Button)window.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            SaveImage(window, "slides-question-history.png");

            string beforePause = transcript.Text;
            ((Button)window.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !session.IsRunning && (string)((Button)window.FindName("PauseButton")).Content == "Resume");
            Assert.Contains("Not listening", partial.Text);
            Assert.Equal(beforePause, transcript.Text);
            Assert.Contains("Synthetic model memory", context.Text);
            Assert.False(((Button)window.FindName("CaptureSlideButton")).IsEnabled);
            PumpUntil(() => timing.Text.Contains("Analysis stopped."));
            historyBox.SelectedIndex = 0;
            Assert.Equal(historicalText, cue.Text);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(historicalText, copied[^1]);
            playback.Publish("Late speech must not appear.", true);
            PumpUntil(() => true);
            Assert.DoesNotContain("Late speech", transcript.Text);
        }
        finally
        {
            window.Close();
            var disposal = session.DisposeAsync().AsTask();
            PumpUntil(() => disposal.IsCompleted);
            disposal.GetAwaiter().GetResult();
        }
    }

    private static void VerifyTeleprompterWithoutServices()
    {
        string path = Path.Combine(Path.GetTempPath(), $"meeting-prompter-ui-{Guid.NewGuid():N}.json");
        var store = new ScriptStore(path);
        var library = new ScriptLibraryWindow(store, true);
        try
        {
            library.Show();
            library.UpdateLayout();
            Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(new WindowInteropHelper(library).Handle));
            Assert.Empty(store.Load());
            Assert.False(library.SaveDraft());
            Assert.Contains("Add a title", ((TextBlock)library.FindName("LibraryError")).Text);
            ((TextBox)library.FindName("ScriptTitleBox")).Text = "Synthetic launch briefing";
            string paragraph = "This is a synthetic rehearsal, not a real meeting. Our sample team is preparing an archive service. " +
                "We will walk through the design, explain the trade-offs, and finish with the next steps.";
            ((TextBox)library.FindName("ScriptBodyBox")).Text = string.Join("\n\n",
                Enumerable.Range(1, 6).Select(i => $"Part {i}. {paragraph}"));
            Assert.True(library.HasUnsavedChanges);
            Assert.True(library.SaveDraft());
            Assert.False(library.HasUnsavedChanges);
            Assert.Single(store.Load());
            var timedScript = Assert.Single(store.Load());
            store.Save(timedScript with { Options = timedScript.Options with { VoiceFollow = false } });
            ((Button)library.FindName("ReloadScriptsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            library.UpdateLayout();
            SaveImage(library, "teleprompter-library.png");
            library.Width = 680;
            library.Height = 640;
            library.UpdateLayout();
            AssertWithinWindow((Button)library.FindName("OpenReaderButton"), library);
            AssertWithinWindow((TextBox)library.FindName("ScriptBodyBox"), library);
            SaveImage(library, "teleprompter-library-narrow.png");
            ((Button)library.FindName("OpenReaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reader = Assert.IsType<TeleprompterWindow>(library.Reader);
            PumpUntil(() => reader.IsLoaded);
            AssertWindowAppearance(library);
            AssertWindowAppearance(reader);
            Assert.False(library.IsVisible);
            reader.UpdateLayout();
            Assert.False(reader.IsPlaying);
            Assert.Equal(0, reader.Position);
            Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(new WindowInteropHelper(reader).Handle));
            Assert.False(((Grid)library.FindName("LibraryWorkspace")).IsEnabled);
            SaveImage(reader, "teleprompter-reader.png");
            reader.Activate();
            ((Grid)reader.FindName("ReadingArea")).Focus();
            reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reader)!, 0, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.True(reader.IsPlaying);
            reader.AdvanceFrame(TimeSpan.FromMilliseconds(500));
            Assert.Equal(16, reader.Position, 3);
            var scroll = (ScrollViewer)reader.FindName("ReaderScroll");
            Assert.Equal(reader.Position, scroll.VerticalOffset, 3);
            reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reader)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.False(reader.IsPlaying);
            reader.AdvanceFrame(TimeSpan.FromMilliseconds(500));
            Assert.Equal(16, reader.Position, 3);
            reader.SeekParagraph(1);
            Assert.True(reader.Position > 16);
            Assert.False(reader.IsPlaying);
            reader.SeekParagraph(-1);
            Assert.Equal(0, reader.Position);
            reader.TogglePlayback();
            PumpUntil(() => reader.Position > 3);
            reader.Pause();
            Assert.True(reader.Position > 3);
            ((Button)reader.FindName("RestartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reader.TogglePlayback();
            reader.AdvanceFrame(TimeSpan.FromSeconds(5));
            Assert.False(reader.IsPlaying);
            Assert.Equal(0, reader.Position);
            ((Button)reader.FindName("FasterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(37, reader.Options.Speed);
            ((Expander)reader.FindName("ReadingSettings")).IsExpanded = true;
            reader.UpdateLayout();
            ((Button)reader.FindName("LargerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)reader.FindName("LooserButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)reader.FindName("NarrowerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(34, reader.Options.FontSize);
            Assert.Equal(1.6, reader.Options.LineSpacing);
            Assert.Equal(560, reader.Options.ReadingWidth);
            Assert.Equal(0, reader.Position);
            reader.UpdateLayout();
            SaveImage(reader, "teleprompter-settings.png");
            ((Expander)reader.FindName("ReadingSettings")).IsExpanded = false;
            reader.Width = 540;
            reader.Height = 580;
            reader.UpdateLayout();
            AssertWithinWindow((Button)reader.FindName("PlayButton"), reader);
            AssertWithinWindow((Grid)reader.FindName("ReadingArea"), reader);
            Assert.True(((Grid)reader.FindName("ReadingArea")).ActualHeight >= 200);
            SaveImage(reader, "teleprompter-reader-narrow.png");
            reader.TogglePlayback();
            for (int i = 0; reader.IsPlaying && i < 1000; i++) reader.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert.False(reader.IsPlaying);
            Assert.Contains("End of script", ((TextBlock)reader.FindName("PlaybackStatus")).Text);
            Assert.False(((Button)reader.FindName("PlayButton")).IsEnabled);
            ((Button)reader.FindName("RestartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, reader.Position);
            Assert.False(reader.IsPlaying);
            reader.TogglePlayback();
            reader.WindowState = WindowState.Minimized;
            Assert.False(reader.IsPlaying);
            reader.Close();
            Assert.Null(library.Reader);
            Assert.True(library.IsVisible);
            Assert.Equal(37, Assert.Single(store.Load()).Options.Speed);
            Assert.False(library.HasUnsavedChanges);
            Assert.True(((Grid)library.FindName("LibraryWorkspace")).IsEnabled);
            VerifyMinimalOverlayWithoutServices(library, store);
            ((Button)library.FindName("NewScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)library.FindName("ScriptTitleBox")).Text = "Another synthetic talk";
            ((TextBox)library.FindName("ScriptBodyBox")).Text = "A second script kept independently.";
            Assert.True(library.SaveDraft());
            var savedList = (ListBox)library.FindName("ScriptsList");
            savedList.SelectedIndex = 0;
            Assert.Equal("Synthetic launch briefing", ((TextBox)library.FindName("ScriptTitleBox")).Text);
            Assert.Equal(37, store.Load().Single(s => s.Title == "Synthetic launch briefing").Options.Speed);
            store.Delete(store.Load().Single(s => s.Title == "Another synthetic talk"));
            ((Button)library.FindName("OpenMinimalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var closingOverlay = Assert.IsType<MinimalPrompterWindow>(library.MinimalOverlay);
            closingOverlay.Run();
            ((Button)library.FindName("ToggleMinimalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(closingOverlay.IsVisible);
            library.Close();
            Assert.False(closingOverlay.IsVisible);
            Assert.False(closingOverlay.IsRunning);
            library = new ScriptLibraryWindow(store, true);
            library.Show();
            Assert.Equal("Synthetic launch briefing", ((TextBox)library.FindName("ScriptTitleBox")).Text);
            Assert.Single(((ListBox)library.FindName("ScriptsList")).Items.Cast<PresentationScript>());
            library.Close();
            File.WriteAllText(path, "damaged synthetic library");
            library = new ScriptLibraryWindow(store, true);
            library.Show();
            Assert.False(((Button)library.FindName("SaveScriptButton")).IsEnabled);
            Assert.False(((Button)library.FindName("OpenReaderButton")).IsEnabled);
            Assert.False(((Button)library.FindName("OpenMinimalButton")).IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((Button)library.FindName("ToggleMinimalButton")).Visibility);
            Assert.Contains("not been overwritten", ((TextBlock)library.FindName("LibraryError")).Text);
            Assert.Equal("damaged synthetic library", File.ReadAllText(path));
        }
        finally
        {
            library.Reader?.Close();
            library.Close();
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }
    private static void VerifyMinimalOverlayWithoutServices(ScriptLibraryWindow library, ScriptStore store)
    {
        var previous = Assert.Single(store.Load());
        var saved = store.Save(previous with { Options = previous.Options with { VoiceFollow = true } });
        ((Button)library.FindName("ReloadScriptsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var open = (Button)library.FindName("OpenMinimalButton");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var overlay = Assert.IsType<MinimalPrompterWindow>(library.MinimalOverlay);
        try
        {
            PumpUntil(() => overlay.IsLoaded);
            Assert.False(library.IsVisible);
            overlay.UpdateLayout();
            Assert.Null(overlay.Owner);
            Assert.True(overlay.AllowsTransparency);
            Assert.Equal(WindowStyle.None, overlay.WindowStyle);
            Assert.True(overlay.Topmost);
            Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(new WindowInteropHelper(overlay).Handle));
            Assert.Equal(Visibility.Visible, ((Border)overlay.FindName("ProtectedContent")).Visibility);
            Assert.False(overlay.IsRunning);
            Assert.Equal(0, overlay.Position);
            Assert.False(((Button)library.FindName("OpenReaderButton")).IsEnabled);
            Assert.False(((Grid)library.FindName("LibraryWorkspace")).IsEnabled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(overlay, library.MinimalOverlay);
            library.WindowState = WindowState.Minimized;
            PumpUntil(() => library.WindowState == WindowState.Minimized);
            Assert.True(overlay.IsVisible);
            Assert.Equal(WindowState.Normal, overlay.WindowState);
            var text = (TextBlock)overlay.FindName("ScriptText");
            Assert.Equal(saved.Text, text.Text);
            Assert.Equal(saved.Options.FontSize, text.FontSize);
            var scroll = (ScrollViewer)overlay.FindName("ScriptScroll");
            var run = (Button)overlay.FindName("RunButton");
            var stop = (Button)overlay.FindName("StopButton");
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(overlay.IsRunning);
            overlay.AdvanceFrame(TimeSpan.FromSeconds(0.5));
            Assert.Equal(saved.Options.Speed * 0.5, overlay.Position, 3);
            Assert.Equal(overlay.Position, scroll.VerticalOffset, 3);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double position = overlay.Position;
            overlay.AdvanceFrame(TimeSpan.FromSeconds(0.5));
            Assert.False(overlay.IsRunning);
            Assert.Equal(position, overlay.Position);
            var toggle = (Button)library.FindName("ToggleMinimalButton");
            Assert.Equal("Hide overlay", toggle.Content);
            overlay.Run();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsVisible);
            Assert.False(overlay.IsRunning);
            Assert.Equal("Show overlay", toggle.Content);
            Assert.True(library.IsVisible);
            Assert.Same(overlay, library.MinimalOverlay);
            overlay.Run();
            overlay.AdvanceFrame(TimeSpan.FromSeconds(0.5));
            Assert.False(overlay.IsRunning);
            Assert.Equal(position, overlay.Position);
            WindowCaptureProtection.Apply(new WindowInteropHelper(overlay).Handle, false);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => overlay.IsVisible);
            Assert.False(library.IsVisible);
            Assert.False(overlay.IsRunning);
            Assert.Equal(position, overlay.Position, 3);
            Assert.Equal("Hide overlay", toggle.Content);
            Assert.Equal(WindowCaptureProtection.ExcludeFromCapture, WindowCaptureProtection.Read(new WindowInteropHelper(overlay).Handle));
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            library.WindowState = WindowState.Normal;
            library.UpdateLayout();
            AssertWithinWindow(toggle, library);
            SaveImage(library, "minimal-overlay-hidden.png");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => overlay.IsVisible);
            Assert.Same(overlay, library.MinimalOverlay);
            Assert.Equal(position, overlay.Position, 3);
            library.WindowState = WindowState.Minimized;
            overlay.Run();
            PumpUntil(() => overlay.Position > position + 2);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Assert.False(overlay.IsRunning);
            scroll.ScrollToVerticalOffset(150);
            scroll.UpdateLayout();
            PumpUntil(() => Math.Abs(overlay.Position - 150) < 0.01);
            overlay.Run();
            overlay.AdvanceFrame(TimeSpan.FromSeconds(0.5));
            Assert.Equal(150 + saved.Options.Speed * 0.5, overlay.Position, 3);
            overlay.AdvanceFrame(TimeSpan.FromSeconds(5));
            Assert.False(overlay.IsRunning);
            overlay.Seek(0);
            overlay.Run();
            overlay.Width = 420;
            overlay.Height = 240;
            overlay.UpdateLayout();
            Assert.False(overlay.IsRunning);
            AssertWithinWindow(run, overlay);
            AssertWithinWindow(stop, overlay);
            var close = (Button)overlay.FindName("CloseButton");
            AssertWithinWindow(close, overlay);
            Assert.Equal("Close prompter", System.Windows.Automation.AutomationProperties.GetName(close));
            Assert.True(scroll.ActualHeight >= 120);
            SaveImage(overlay, "minimal-prompter-narrow.png");
            overlay.Width = 720;
            overlay.Height = 360;
            overlay.UpdateLayout();
            overlay.Seek(0);
            SaveImage(overlay, "minimal-prompter.png");
            var surface = (SolidColorBrush)((Border)overlay.FindName("ProtectedContent")).Background;
            Assert.InRange(surface.Color.A, (byte)100, (byte)220);
            var pixels = new RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            pixels.Render(overlay);
            byte[] transparentPixel = new byte[4];
            pixels.CopyPixels(new Int32Rect(10, 100, 1, 1), transparentPixel, 4, 0);
            Assert.Equal(surface.Color.A, transparentPixel[3]);
            VerifyMinimalOverlayCapture(overlay);
            var app = Assert.IsType<App>(Application.Current);
            app.ApplyContrastPalette(true);
            Assert.Equal(SystemColors.WindowBrush, ((Border)overlay.FindName("ProtectedContent")).Background);
            app.ApplyContrastPalette(false);
            Assert.Equal(surface.Color, ((SolidColorBrush)((Border)overlay.FindName("ProtectedContent")).Background).Color);
            overlay.Run();
            overlay.WindowState = WindowState.Minimized;
            Assert.False(overlay.IsRunning);
            Assert.Equal("Show overlay", toggle.Content);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WindowState.Normal, overlay.WindowState);
            Assert.Equal("Hide overlay", toggle.Content);
            overlay.UpdateLayout();
            overlay.Run();
            for (int i = 0; overlay.IsRunning && i < 1000; i++) overlay.AdvanceFrame(TimeSpan.FromSeconds(1));
            Assert.False(overlay.IsRunning);
            Assert.Contains("End", ((TextBlock)overlay.FindName("PlaybackStatus")).Text);
            overlay.Run();
            Assert.Equal(0, overlay.Position);
            Assert.True(overlay.IsRunning);
            overlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.False(overlay.IsRunning);
            overlay.Run();
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsRunning);
            Assert.False(overlay.IsVisible);
            Assert.Null(library.MinimalOverlay);
            Assert.True(library.IsVisible);
            Assert.Equal(Visibility.Collapsed, toggle.Visibility);
            Assert.True(((Button)library.FindName("OpenReaderButton")).IsEnabled);
            Assert.True(((Grid)library.FindName("LibraryWorkspace")).IsEnabled);
            Assert.Equal(saved, Assert.Single(store.Load()));

            var rejected = new MinimalPrompterWindow(saved, _ => throw new InvalidOperationException("Synthetic capture failure"));
            Assert.False(rejected.ShowProtected());
            Assert.False(rejected.IsVisible);
            Assert.False(rejected.IsRunning);
            Assert.Equal(Visibility.Hidden, ((Border)rejected.FindName("ProtectedContent")).Visibility);
            Assert.Contains("without showing your script", rejected.CaptureError);
            int checks = 0;
            var failedReshow = new MinimalPrompterWindow(saved, handle =>
            {
                if (++checks > 1) throw new InvalidOperationException("Synthetic recheck failure");
                WindowCaptureProtection.Apply(handle, true);
            });
            try
            {
                Assert.True(failedReshow.ShowProtected());
                failedReshow.Hide();
                Assert.False(failedReshow.ShowProtected());
                Assert.Equal(2, checks);
                Assert.False(failedReshow.IsVisible);
                Assert.Equal(Visibility.Hidden, ((Border)failedReshow.FindName("ProtectedContent")).Visibility);
                Assert.NotEmpty(failedReshow.CaptureError);
            }
            finally { failedReshow.Close(); }
        }
        finally
        {
            overlay.Close();
            library.WindowState = WindowState.Normal;
        }
    }

    private static void VerifySingleScreenNavigation(MainWindow setup)
    {
        string path = Path.Combine(Path.GetTempPath(), $"single-screen-{Guid.NewGuid():N}.json");
        var store = new ScriptStore(path);
        store.Save(new PresentationScript
        {
            Title = "Synthetic navigation script",
            Text = string.Join("\n\n", Enumerable.Repeat("Read this invented script while the previous screen stays hidden.", 12)),
            Options = new() { VoiceFollow = false }
        });
        ScriptLibraryWindow? library = null;
        try
        {
            library = ScriptLibraryWindow.Open(setup, true, store);
            Assert.False(setup.IsVisible);
            Assert.True(library.IsVisible);
            ((Button)library.FindName("OpenReaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reader = Assert.IsType<TeleprompterWindow>(library.Reader);
            PumpUntil(() => reader.IsLoaded);
            Assert.False(setup.IsVisible);
            Assert.False(library.IsVisible);
            Assert.True(reader.IsVisible);
            Assert.Null(reader.Owner);
            Assert.Same(library, ScriptLibraryWindow.Open(setup, true, store));
            Assert.False(library.IsVisible);
            Assert.True(reader.IsVisible);
            reader.Close();
            Assert.True(library.IsVisible);
            Assert.False(setup.IsVisible);

            ((Button)library.FindName("OpenMinimalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var minimal = Assert.IsType<MinimalPrompterWindow>(library.MinimalOverlay);
            PumpUntil(() => minimal.IsLoaded);
            Assert.False(library.IsVisible);
            Assert.False(setup.IsVisible);
            minimal.Seek(80);
            minimal.Run();
            ((Button)minimal.FindName("LibraryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(minimal.IsVisible);
            Assert.False(minimal.IsRunning);
            Assert.True(library.IsVisible);
            Assert.False(setup.IsVisible);
            Assert.Equal(80, minimal.Position, 3);
            ((Button)library.FindName("ToggleMinimalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(minimal.IsVisible);
            Assert.False(library.IsVisible);
            Assert.False(minimal.IsRunning);
            Assert.Equal(80, minimal.Position, 3);
            ((Button)minimal.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(library.IsVisible);
            Assert.False(setup.IsVisible);
            ((Button)library.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(setup.IsVisible);
            Assert.False(library.IsVisible);

            library = ScriptLibraryWindow.Open(setup, true, store);
            Assert.False(setup.IsVisible);
            ((Button)library.FindName("OpenReaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var closingReader = Assert.IsType<TeleprompterWindow>(library.Reader);
            library.Close();
            PumpUntil(() => setup.IsVisible);
            Assert.False(closingReader.IsVisible);
            Assert.False(library.IsVisible);

            var origin = new Window { Title = "Synthetic navigation origin" };
            origin.Show();
            library = ScriptLibraryWindow.Open(origin, true, store);
            ((Button)library.FindName("OpenMinimalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var abandonedOverlay = Assert.IsType<MinimalPrompterWindow>(library.MinimalOverlay);
            origin.Close();
            Assert.False(library.IsVisible);
            Assert.False(abandonedOverlay.IsVisible);
            Assert.Null(library.MinimalOverlay);
        }
        finally
        {
            library?.Close();
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    private static void VerifyMinimalOverlayCapture(MinimalPrompterWindow overlay)
    {
        overlay.Left = 80;
        overlay.Top = 100;
        var backdrop = new Window
        {
            Title = "Synthetic slide backdrop",
            WindowStyle = WindowStyle.None, Background = Brushes.White, Topmost = true,
            Left = 50, Top = 70, Width = 800, Height = 430
        };
        try
        {
            backdrop.Show();
            backdrop.UpdateLayout();
            var first = overlay.PointToScreen(new Point(30, 35));
            var last = overlay.PointToScreen(new Point(130, 135));
            nint backdropHandle = new WindowInteropHelper(backdrop).Handle;
            foreach (var point in new[] { first, last, new Point(first.X, last.Y), new Point(last.X, first.Y) })
                Assert.Equal(backdropHandle, WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
            overlay.Topmost = false;
            overlay.Topmost = true;
            overlay.Activate();
            PumpUntil(() => WindowFromPoint(new NativePoint { X = (int)first.X, Y = (int)first.Y }) ==
                new WindowInteropHelper(overlay).Handle);
            byte[] captured = ScreenRegionCapture.Capture(ScreenRegionCapture.Selection(first, last));
            using var stream = new MemoryStream(captured);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var rgb = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            byte[] pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 3];
            rgb.CopyPixels(pixels, rgb.PixelWidth * 3, 0);
            Assert.All(pixels, value => Assert.Equal((byte)255, value));
        }
        finally { backdrop.Close(); }
    }

    private static void VerifyVoiceFollowingWithoutDevices()
    {
        const string text = "Today we will review the archive design and explain how workers handle incoming files safely.\n\n" +
            "Next we discuss the retry policy and ownership of failed uploads.";
        var inputs = new List<PrompterTestInput>();
        bool delayStartup = false;
        var reader = new TeleprompterWindow(new PresentationScript
        {
            Title = "Synthetic voice-follow rehearsal", Text = text
        }, true, () => new("Synthetic microphone (no device)", () =>
        {
            var input = new PrompterTestInput { DelayStartup = delayStartup };
            inputs.Add(input);
            return input;
        }));
        try
        {
            reader.Show();
            PumpUntil(() => reader.IsLoaded);
            reader.UpdateLayout();
            Assert.Empty(inputs);
            Assert.False(reader.IsFollowing);
            Assert.False(((Button)reader.FindName("PlayButton")).IsEnabled);
            var consent = (CheckBox)reader.FindName("VoiceConsentBox");
            consent.IsChecked = true;
            var start = reader.StartFollowingAsync();
            PumpUntil(() => start.IsCompleted);
            start.GetAwaiter().GetResult();
            Assert.True(reader.IsFollowing);
            Assert.False(reader.IsPlaying);
            var input = Assert.Single(inputs);
            input.Publish("today", false);
            PumpUntil(() => reader.CurrentSpokenWord == 0);
            var highlight = (TextBlock)reader.FindName("ActiveWordText");
            Assert.Equal("Today", highlight.Text);
            Assert.True(highlight.FontSize > reader.Options.FontSize);
            Assert.Equal(Visibility.Visible, ((Border)reader.FindName("ActiveWordHighlight")).Visibility);
            input.Publish("today we will review the archive design and explain how workers handle incoming files safely", false);
            PumpUntil(() => reader.CurrentSpokenWord == 14);
            Assert.True(reader.Position > 0);
            double position = reader.Position;
            reader.AdvanceFrame(TimeSpan.FromSeconds(30));
            Assert.Equal(position, reader.Position);
            Assert.True(reader.IsFollowing);
            input.Publish("today we will review the archive design and explain how workers handle incoming files safely", true);
            PumpUntil(() => ((TextBlock)reader.FindName("HeardText")).Text.StartsWith("Heard:", StringComparison.Ordinal));
            Assert.Equal(14, reader.CurrentSpokenWord);
            Assert.Equal(position, reader.Position);
            input.Publish("what time is lunch", true);
            PumpUntil(() => ((TextBlock)reader.FindName("VoiceStatus")).Text.StartsWith("Holding position", StringComparison.Ordinal));
            Assert.Equal(position, reader.Position);
            reader.UpdateLayout();
            SaveImage(reader, "voice-follow-reader.png");
            var lateTranscript = input.CaptureTranscript();
            reader.Pause();
            Assert.False(reader.IsFollowing);
            Assert.True(input.Stopped);
            PumpUntil(() => input.Disposed);
            lateTranscript?.Invoke("Synthetic microphone", "next we discuss the retry policy", true);
            PumpUntil(() => true);
            Assert.Equal(14, reader.CurrentSpokenWord);
            var resume = reader.StartFollowingAsync();
            PumpUntil(() => resume.IsCompleted);
            Assert.True(reader.IsFollowing);
            Assert.Equal(2, inputs.Count);
            lateTranscript?.Invoke("Synthetic microphone", "next we discuss the retry policy and ownership of failed uploads", true);
            PumpUntil(() => true);
            Assert.Equal(14, reader.CurrentSpokenWord);
            inputs[1].Publish("next we discuss the retry policy and ownership of failed uploads", true);
            PumpUntil(() => !reader.IsFollowing);
            Assert.True(inputs[1].Stopped);
            Assert.Contains("End of script", ((TextBlock)reader.FindName("VoiceStatus")).Text);

            reader.AnchorAtWord(3);
            Assert.Equal(2, reader.CurrentSpokenWord);
            var restart = reader.StartFollowingAsync();
            PumpUntil(() => restart.IsCompleted);
            Assert.True(reader.IsFollowing);
            inputs[2].Publish("review the archive design", false);
            PumpUntil(() => reader.CurrentSpokenWord == 6);
            inputs[2].Fail("Synthetic microphone disconnected. Resume after checking the device.");
            PumpUntil(() => !reader.IsFollowing);
            Assert.Contains("disconnected", ((TextBlock)reader.FindName("VoiceError")).Text);
            Assert.True(inputs[2].Disposed);

            reader.AnchorAtWord(0);
            delayStartup = true;
            var delayedStart = reader.StartFollowingAsync();
            Assert.False(delayedStart.IsCompleted);
            consent.IsChecked = false;
            Assert.False(reader.IsFollowing);
            PumpUntil(() => delayedStart.IsCompleted);
            Assert.True(inputs[3].Disposed);
            Assert.Equal(-1, reader.CurrentSpokenWord);
            Assert.False(reader.IsPlaying);
            delayStartup = false;
            consent.IsChecked = true;
            var finalStart = reader.StartFollowingAsync();
            PumpUntil(() => finalStart.IsCompleted);
            Assert.True(reader.IsFollowing);
            reader.WindowState = WindowState.Minimized;
            PumpUntil(() => !reader.IsFollowing);
            Assert.True(inputs[^1].Stopped);
            reader.WindowState = WindowState.Normal;
            var modes = (TabControl)reader.FindName("ModeTabs");
            modes.SelectedIndex = 1;
            Assert.False(consent.IsChecked);
            reader.SeekParagraph(1);
            modes.SelectedIndex = 0;
            Assert.Equal(14, reader.CurrentSpokenWord);
            Assert.False(reader.IsFollowing);
            reader.Width = 540;
            reader.UpdateLayout();
            AssertWithinWindow((Button)reader.FindName("PlayButton"), reader);
            Assert.True(((Grid)reader.FindName("ReadingArea")).ActualHeight > 200);
            SaveImage(reader, "voice-follow-reader-narrow.png");
            consent.IsChecked = true;
            var closeStart = reader.StartFollowingAsync();
            PumpUntil(() => closeStart.IsCompleted);
            Assert.True(reader.IsFollowing);
            var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            inputs[^1].FinishDisposal = disposal;
            var close = reader.CloseReaderAsync();
            Assert.True(inputs[^1].Stopped);
            Assert.False(close.IsCompleted);
            Assert.True(reader.IsVisible);
            disposal.SetResult();
            PumpUntil(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            Assert.All(inputs, item => Assert.True(item.Disposed));
            Assert.False(reader.IsFollowing);
        }
        finally
        {
            foreach (var input in inputs) input.FinishDisposal?.TrySetResult();
            if (reader.IsVisible)
            {
                var close = reader.CloseReaderAsync();
                PumpUntil(() => close.IsCompleted);
            }
        }
    }

    private sealed class PrompterTestInput : ISpeechAudioInput
    {
        public string Source => "Synthetic microphone";
        public DateTimeOffset? LastPacket => null;
        public bool DelayStartup { get; init; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource? FinishDisposal { get; set; }
        public event Action<string, string, bool>? Transcript;
        public event Action<string>? Faulted;
        public Task StartAsync(CancellationToken cancellationToken) =>
            DelayStartup ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
        public void StopCapture() => Stopped = true;
        public async ValueTask DisposeAsync()
        {
            if (FinishDisposal is not null) await FinishDisposal.Task;
            Disposed = true;
        }
        public void Publish(string text, bool final) => Transcript?.Invoke(Source, text, final);
        public void Fail(string message) => Faulted?.Invoke(message);
        public Action<string, string, bool>? CaptureTranscript() => Transcript;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || watch.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "UI state did not update before the deadline.");
    }

    private static void AssertWithinWindow(FrameworkElement element, Window window)
    {
        Point origin = element.TranslatePoint(new Point(), window);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        Assert.InRange(origin.X, 0, window.ActualWidth);
        Assert.InRange(origin.Y, 0, window.ActualHeight);
        Assert.True(origin.X + element.ActualWidth <= window.ActualWidth);
        Assert.True(origin.Y + element.ActualHeight <= window.ActualHeight);
    }

    private static void AssertTextContrast(App app, string foregroundKey, string backgroundKey)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                double channel = value / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double foreground = Luminance(((SolidColorBrush)app.FindResource(foregroundKey)).Color);
        double background = Luminance(((SolidColorBrush)app.FindResource(backgroundKey)).Color);
        double contrast = (Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05);
        Assert.True(contrast >= 4.5, $"{foregroundKey} on {backgroundKey} has contrast {contrast:F2}, expected at least 4.5.");
    }

    private sealed class ContextInput(string source) : ISpeechAudioInput
    {
        public string Source => source;
        public DateTimeOffset? LastPacket => null;
        public event Action<string, string, bool>? Transcript;
        public event Action<string>? Faulted { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void StopCapture() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(string text, bool final) => Transcript?.Invoke(Source, text, final);
    }

    private sealed class ContextModel : IMeetingModel, ISlideModel
    {
        public string QuestionText { get; set; } = "What prevents duplicate charges?";
        public bool Addressed { get; set; }
        public string? SilenceReason { get; set; }
        public bool LastQuestionRequested { get; private set; }

        public Task<SlideAnalysis> ReadSlideAsync(byte[] png, CancellationToken cancellationToken) =>
            Task.FromResult(new SlideAnalysis("Archive ingestion", "Retention: 30 days.", "Workers write to storage.", "Synthetic test."));

        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            LastQuestionRequested = request.QuestionRequested;
            return Task.FromResult(new AnalysisResult("Synthetic model memory: payment retries need duplicate-charge protection.",
                Addressed ? "addressed" : request.CurrentCue is null ? "none" :
                    request.CurrentCue.Text == QuestionText ? "keep" : "outdated",
                !Addressed && SilenceReason is null && request.CurrentCue?.Text != QuestionText ? new("question", QuestionText,
                    "Payment retries were mentioned.", "Synthetic test.", [request.Utterances[0].Id]) : null, SilenceReason));
        }
    }

    private static void VerifySlidePreviewAndCapture()
    {
        Assert.Equal(new Int32Rect(-300, 20, 200, 80),
            ScreenRegionCapture.Selection(new Point(-100, 100), new Point(-300, 20)));
        Assert.Throws<ArgumentException>(() => ScreenRegionCapture.Capture(new Int32Rect(0, 0, 1, 1)));
        var selector = new SlideRegionWindow();
        selector.ContentRendered += (_, _) =>
        {
            Assert.True(GetWindowRect(new WindowInteropHelper(selector).Handle, out var bounds));
            Assert.Equal(ScreenRegionCapture.DesktopBounds, new Int32Rect(bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            selector.DialogResult = false;
        };
        Assert.False(selector.ShowDialog());
        Assert.Null(selector.SelectedRegion);
        var backdrop = new Window
        {
            Width = 400,
            Height = 300,
            Left = 50,
            Top = 50,
            WindowStyle = WindowStyle.None,
            Background = Brushes.White,
            Topmost = true,
            Title = "Synthetic capture test"
        };
        try
        {
            backdrop.Show();
            backdrop.Activate();
            backdrop.UpdateLayout();
            PumpUntil(() => backdrop.IsVisible && backdrop.ActualWidth > 0);
            var first = backdrop.PointToScreen(new Point(30, 30));
            var last = backdrop.PointToScreen(new Point(130, 130));
            nint handle = new WindowInteropHelper(backdrop).Handle;
            foreach (var point in new[] { first, last, new Point(first.X, last.Y), new Point(last.X, first.Y) })
                Assert.Equal(handle, WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
            byte[] captured = ScreenRegionCapture.Capture(ScreenRegionCapture.Selection(first, last));
            SlideImage.Validate(captured);
            using var stream = new MemoryStream(captured);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.InRange(bitmap.PixelWidth, 16, 1600);
            var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            byte[] pixel = new byte[3];
            rgb.CopyPixels(new Int32Rect(rgb.PixelWidth / 2, rgb.PixelHeight / 2, 1, 1), pixel, 3, 0);
            Assert.All(pixel, value => Assert.Equal((byte)255, value));
        }
        finally { backdrop.Close(); }

        byte[] image = SyntheticSlide.Png();
        var preview = new SlidePreviewWindow(image, true);
        preview.ContentRendered += (_, _) =>
        {
            Assert.NotNull(((Image)preview.FindName("SlideImagePreview")).Source);
            Assert.Equal(WindowCaptureProtection.ExcludeFromCapture,
                WindowCaptureProtection.Read(new WindowInteropHelper(preview).Handle));
            SaveImage(preview, "slide-preview.png");
            ((Button)preview.FindName("SendSlideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        Assert.True(preview.ShowDialog());
        Assert.Null(((Image)preview.FindName("SlideImagePreview")).Source);
        var canceled = new SlidePreviewWindow(image, false);
        canceled.ContentRendered += (_, _) => canceled.Close();
        Assert.False(canceled.ShowDialog());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);

    private static void AssertWindowAppearance(Window window, bool highContrast = false)
    {
        Assert.True(WindowAppearance.GetEnabled(window));
        Assert.NotNull(window.Icon);
        if (window.WindowStyle == WindowStyle.None) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Equal(0, DwmGetWindowAttribute(handle, 20, out int dark, sizeof(int)));
            Assert.Equal(highContrast ? 0 : 1, dark);
        }
        Assert.True(WindowAppearance.Apply(window, highContrast));
    }

    private static void VerifyAppIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/MeetingCopilot.Desktop;component/Assets/MeetingCopilot.ico"));
        Assert.NotNull(resource);
        using var stream = resource.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, decoder.Frames.Select(f => f.PixelWidth));
        foreach (var frame in decoder.Frames)
        {
            Assert.Equal(frame.PixelWidth, frame.PixelHeight);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
            Assert.Equal(0, pixels[3]);
            Assert.Equal(255, pixels[(frame.PixelHeight / 2 * frame.PixelWidth + frame.PixelWidth / 2) * 4 + 3]);
        }
    }

    private static void SaveNativeWindowImage(Window window, string filename)
    {
        // Print only this synthetic test window, never the desktop or another application's pixels.
        nint handle = new WindowInteropHelper(window).Handle;
        Assert.True(GetWindowRect(handle, out var rect));
        nint dc = GetDC(handle), destination = 0, bitmap = 0, previous = 0;
        Assert.NotEqual(0, dc);
        try
        {
            destination = CreateCompatibleDC(dc);
            Assert.NotEqual(0, destination);
            bitmap = CreateCompatibleBitmap(dc, rect.Right - rect.Left, rect.Bottom - rect.Top);
            Assert.NotEqual(0, bitmap);
            previous = SelectObject(destination, bitmap);
            Assert.NotEqual(0, previous);
            Assert.NotEqual(new nint(-1), previous);
            Assert.True(PrintWindow(handle, destination, 2));
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var rgb = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !SystemParameters.HighContrast)
            {
                var pixel = new byte[3];
                int captionY = (int)(16 * VisualTreeHelper.GetDpi(window).DpiScaleY);
                rgb.CopyPixels(new Int32Rect(rgb.PixelWidth / 2, captionY, 1, 1), pixel, 3, 0);
                Assert.Equal(new byte[] { 36, 27, 20 }, pixel);
            }
            SaveBitmap(rgb, filename);
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1)) SelectObject(destination, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (destination != 0) DeleteDC(destination);
            ReleaseDC(handle, dc);
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

    private static void SaveImage(Window window, string filename)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        SaveBitmap(bitmap, filename);
    }

    private static IEnumerable<Label> VisualLabels(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Label label) yield return label;
            foreach (var descendant in VisualLabels(child)) yield return descendant;
        }
    }

    private static void SaveBitmap(BitmapSource bitmap, string filename)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MeetingCopilot.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        string folder = Path.Combine(root.FullName, "artifacts", "screenshots");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, filename));
        encoder.Save(output);
    }
}
