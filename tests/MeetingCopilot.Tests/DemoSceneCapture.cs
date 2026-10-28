using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public partial class DesktopSmokeTests
{
    private static void ExportDemoScenes()
    {
        string? output = Environment.GetEnvironmentVariable("MEETINGCOPILOT_DEMO_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        string settingsPath = Path.Combine(output, "temporary-settings.json");
        string scriptsPath = Path.Combine(output, "temporary-scripts.json");
        var store = new SettingsStore(settingsPath);
        store.Save(new ConnectionSettings
        {
            SpeechProvider = SpeechProvider.AzureStreaming,
            SpeechRegion = "eastus",
            ModelConnection = ModelConnection.ResponsesApiKey,
            ModelEndpoint = "https://your-resource.services.ai.azure.com/openai/v1",
            Deployment = "your-deployment"
        });
        var setup = new MainWindow(store);
        var overlay = new OverlayWindow(null, true, _ => { });
        ScriptLibraryWindow? library = null;
        MinimalPrompterWindow? minimal = null;
        T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Capture(Window window, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(file);
        }
        void ShowModel()
        {
            setup.UpdateLayout();
            var scroll = Find<ScrollViewer>(setup, "ConnectionsScroll");
            var selector = Find<ComboBox>(setup, "ModelConnectionBox");
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + selector.TransformToAncestor(scroll).Transform(new Point()).Y - 26);
            setup.UpdateLayout();
        }
        try
        {
            setup.Show();
            setup.Width = 780;
            setup.Height = 900;
            Find<ComboBox>(setup, "PlaybackBox").ItemsSource = new[] { new AudioDevice("demo-playback", "Meeting playback (demo)") };
            Find<ComboBox>(setup, "PlaybackBox").SelectedIndex = 0;
            Find<ComboBox>(setup, "MicrophoneBox").ItemsSource = new[] { new AudioDevice("demo-microphone", "Presentation microphone (demo)") };
            Find<ComboBox>(setup, "MicrophoneBox").SelectedIndex = 0;
            Find<TextBox>(setup, "BriefBox").Text = "Synthetic design review: improve payment retries while preventing duplicate charges.";
            Capture(setup, "setup");
            Find<TabControl>(setup, "SetupTabs").SelectedIndex = 1;
            var provider = Find<ComboBox>(setup, "ModelConnectionBox");
            foreach (var (index, label) in new[] { (2, "foundry"), (4, "gemini"), (5, "claude"), (6, "grok") })
            {
                provider.SelectedIndex = index;
                if (index >= 3) Find<TextBox>(setup, "ExternalModelBox").Text = "your-model-id";
                ShowModel();
                Capture(setup, label);
            }
            provider.SelectedIndex = 3;
            Find<TextBox>(setup, "ExternalEndpointBox").Text = "http://localhost:11434/v1";
            Find<TextBox>(setup, "ExternalModelBox").Text = "your-local-model";
            Find<ComboBox>(setup, "ExternalAuthBox").SelectedIndex = 1;
            Find<ComboBox>(setup, "ExternalTokenParameterBox").SelectedIndex = 1;
            ShowModel();
            Capture(setup, "local");
            Find<Button>(setup, "TestModelButton").BringIntoView();
            Capture(setup, "connection-test");
            Find<TabControl>(setup, "SetupTabs").SelectedIndex = 2;
            Capture(setup, "privacy");
            setup.Hide();

            overlay.Show();
            overlay.Width = 850;
            overlay.Height = 560;
            Capture(overlay, "question");
            Click(overlay, "CompactButton");
            overlay.Height = 850;
            Find<Expander>(overlay, "WhyPanel").IsExpanded = true;
            Capture(overlay, "question-why");
            Click(overlay, "FindButton");
            Click(overlay, "FindButton");
            Capture(overlay, "recommendation");
            Click(overlay, "CopyButton");
            Capture(overlay, "copy");
            Click(overlay, "BrowseQuestionsButton");
            Capture(overlay, "history");
            Click(overlay, "BrowseQuestionsButton");
            Find<Expander>(overlay, "WhyPanel").IsExpanded = false;
            Find<TabControl>(overlay, "ContextTabs").SelectedIndex = 1;
            Capture(overlay, "context");
            Find<TabControl>(overlay, "ContextTabs").SelectedIndex = 2;
            Capture(overlay, "capture-privacy");
            overlay.Hide();

            var scripts = new ScriptStore(scriptsPath);
            library = new ScriptLibraryWindow(scripts, true);
            library.Show();
            library.Width = 1100;
            library.Height = 760;
            Find<TextBox>(library, "ScriptTitleBox").Text = "Product walkthrough";
            Find<TextBox>(library, "ScriptBodyBox").Text =
                "Welcome to our product walkthrough.\n\n" +
                "Today, we will review the problem, explain our approach, and agree on the next steps.\n\n" +
                "First, our goal is to make retries safe. A timeout does not always mean a payment failed.\n\n" +
                "Our proposal is to reuse the original idempotency key and add capped backoff with jitter.\n\n" +
                "This reduces duplicate charges and spreads requests while a service recovers.\n\n" +
                "Before we close, let us confirm ownership, monitoring, and the rollout plan.";
            Assert.True(library.SaveDraft());
            var script = Assert.Single(scripts.Load());
            script = script with { Options = script.Options with { VoiceFollow = false, Speed = 38 } };
            scripts.Save(script);
            Click(library, "ReloadScriptsButton");
            Capture(library, "library");
            Click(library, "OpenReaderButton");
            var reader = Assert.IsType<TeleprompterWindow>(library.Reader);
            PumpUntil(() => reader.IsLoaded);
            Capture(reader, "reader");
            reader.Close();
            library.Hide();
            minimal = new MinimalPrompterWindow(script) { Width = 1000, Height = 510 };
            Assert.True(minimal.ShowProtected());
            minimal.UpdateLayout();
            double initial = minimal.Position;
            minimal.Run();
            for (int frame = 0; frame < 250; frame++)
            {
                if (frame == 200) minimal.Stop();
                if (frame < 200) minimal.AdvanceFrame(TimeSpan.FromSeconds(1.0 / 25));
                Capture(minimal, $"minimal-{frame:000}");
            }
            Assert.True(minimal.Position > initial);
            Assert.False(minimal.IsRunning);
        }
        finally
        {
            minimal?.Close();
            library?.Close();
            overlay.Close();
            setup.Close();
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(scriptsPath)) File.Delete(scriptsPath);
        }
    }
}
