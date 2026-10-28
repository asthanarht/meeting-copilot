using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeetingCopilot.Core;
using MeetingCopilot.Desktop;
using Xunit;

namespace MeetingCopilot.Tests;

public partial class DesktopSmokeTests
{
    private static void CheckExternalProviderSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"meeting-copilot-provider-ui-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        store.Save(new ConnectionSettings { ModelKey = "synthetic-azure-only", ModelEndpoint = "https://test.services.ai.azure.com/openai/v1" });
        TaskCompletionSource? pending = null;
        CancellationToken observedToken = default;
        ModelSettings? testedSettings = null;
        var window = new MainWindow(store, (settings, token) =>
        {
            testedSettings = settings;
            observedToken = token;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return pending.Task;
        });
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            window.Show();
            Find<TabControl>("SetupTabs").SelectedIndex = 1;
            var selection = Find<ComboBox>("ModelConnectionBox");
            var endpoint = Find<TextBox>("ExternalEndpointBox");
            var model = Find<TextBox>("ExternalModelBox");
            var key = Find<PasswordBox>("ExternalKeyBox");
            var status = Find<TextBlock>("ModelTestStatus");
            var test = Find<Button>("TestModelButton");
            var connections = new[] { ModelConnection.OpenAICompatible, ModelConnection.Gemini, ModelConnection.Anthropic, ModelConnection.Grok };
            for (int i = 0; i < connections.Length; i++)
            {
                selection.SelectedIndex = i + 3;
                Assert.Equal(ExternalModelProfile.Default(connections[i]).Endpoint, endpoint.Text);
                Assert.Equal("", key.Password);
                Assert.Equal(Visibility.Collapsed, Find<StackPanel>("AzureModelPanel").Visibility);
                model.Text = "synthetic-" + connections[i];
                key.Password = "key-for-" + connections[i];
                var read = window.ReadModelSettings();
                Assert.Equal(connections[i], read.Connection);
                Assert.Equal(key.Password, read.ApiKey);
                Assert.Equal("", read.EntraTenantId);
                Assert.Equal(i == 0 ? Visibility.Visible : Visibility.Collapsed, Find<StackPanel>("CompatibleFormatPanel").Visibility);
            }
            selection.SelectedIndex = 4;
            Assert.Equal("key-for-Gemini", key.Password);
            endpoint.Text = "https://gemini.example/v1beta";
            Assert.Equal("", key.Password);
            Assert.Contains("gemini.example", Find<TextBlock>("ModelDestinationText").Text);
            key.Password = "gemini-new-destination-key";
            Find<ComboBox>("ExternalTimeoutBox").SelectedIndex = 2;
            Find<ComboBox>("ExternalContextBox").SelectedIndex = 2;
            Click("SaveSettingsButton");
            Assert.DoesNotContain("gemini-new-destination-key", File.ReadAllText(path));
            Assert.DoesNotContain("key-for-", File.ReadAllText(path));
            Assert.Equal("synthetic-azure-only", store.Load()!.ModelKey);
            var reopened = new MainWindow(store);
            Assert.Equal(4, ((ComboBox)reopened.FindName("ModelConnectionBox")).SelectedIndex);
            Assert.Equal(window.ReadModelSettings(), reopened.ReadModelSettings());
            reopened.Close();

            window.Width = 780;
            window.Height = 900;
            window.UpdateLayout();
            var scroll = Find<ScrollViewer>("ConnectionsScroll");
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + selection.TransformToAncestor(scroll).Transform(new Point()).Y - 26);
            window.UpdateLayout();
            SaveImage(window, "multi-provider-gemini-setup.png");
            Click("TestModelButton");
            Assert.NotNull(testedSettings);
            Assert.Equal(ModelConnection.Gemini, testedSettings.Connection);
            Assert.False(test.IsEnabled);
            Assert.Contains("No audio capture", status.Text);
            var obsolete = pending!;
            model.Text = "synthetic-replacement";
            Assert.True(observedToken.IsCancellationRequested);
            PumpUntil(() => test.IsEnabled);
            obsolete.SetResult();
            Assert.Contains("Connection changed", status.Text);
            Click("TestModelButton");
            pending!.SetResult();
            PumpUntil(() => test.IsEnabled);
            Assert.Contains("Structured response accepted", status.Text);
            Assert.Contains("slide images are not enabled", status.Text);
            Click("TestModelButton");
            Click("CancelModelTestButton");
            PumpUntil(() => test.IsEnabled);
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Contains("canceled", status.Text);
            pending!.SetResult();

            Click("TestModelButton");
            selection.SelectedIndex = 5;
            Assert.Equal("key-for-Anthropic", key.Password);
            Assert.True(observedToken.IsCancellationRequested);
            PumpUntil(() => test.IsEnabled);
            pending!.SetResult();
            Assert.Contains("Connection changed", status.Text);
            Click("TestModelButton");
            pending!.SetException(new HttpRequestException("private server detail"));
            PumpUntil(() => test.IsEnabled);
            Assert.Contains("Connection test failed", status.Text);
            Assert.DoesNotContain("private server detail", status.Text);
            selection.SelectedIndex = 3;
            endpoint.Text = "http://localhost:11434/v1";
            Find<ComboBox>("ExternalAuthBox").SelectedIndex = 1;
            Assert.Equal("", key.Password);
            Assert.Equal(Visibility.Collapsed, Find<StackPanel>("ExternalKeyPanel").Visibility);
            Find<ComboBox>("ExternalTokenParameterBox").SelectedIndex = 1;
            Find<Expander>("ExternalAdvanced").IsExpanded = true;
            Assert.Equal(CompatibleAuthentication.None, window.ReadModelSettings().Compatible!.Authentication);
            window.Width = 560;
            window.Height = 620;
            window.UpdateLayout();
            Find<Expander>("ExternalAdvanced").BringIntoView();
            window.UpdateLayout();
            AssertWithinWindow(Find<Button>("StartButton"), window);
            SaveImage(window, "multi-provider-local-narrow.png");

            Click("ForgetSettingsButton");
            Assert.Null(store.Load());
            foreach (int index in new[] { 3, 4, 5, 6 })
            {
                selection.SelectedIndex = index;
                Assert.Equal("", key.Password);
            }
            selection.SelectedIndex = 2;
            Assert.Equal("", Find<PasswordBox>("ModelKeyBox").Password);
            selection.SelectedIndex = 6;
            key.Password = "synthetic-last-key";
            Click("TestModelButton");
            window.Close();
            Assert.True(observedToken.IsCancellationRequested);
            PumpUntil(() => test.IsEnabled);
            pending!.SetResult();
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
