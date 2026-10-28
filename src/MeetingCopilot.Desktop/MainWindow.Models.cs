using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class MainWindow
{
    private readonly Func<ModelSettings, CancellationToken, Task> modelProbe;
    private ExternalModelProfiles externalProfiles = new();
    private ModelConnection? displayedExternal;
    private bool modelFieldsReady;
    private bool changingModelFields;
    private string lastExternalEndpoint = "";
    private CancellationTokenSource? modelTest;
    private long modelEditVersion;

    private void ConnectModelEvents()
    {
        modelFieldsReady = true;
        foreach (var text in new[] { ExternalEndpointBox, ExternalModelBox, EndpointBox, DeploymentBox, EntraTenantBox })
            text.TextChanged += (_, _) =>
            {
                if (changingModelFields) return;
                if (ReferenceEquals(text, ExternalEndpointBox) && ExternalEndpointBox.Text.Trim() != lastExternalEndpoint)
                {
                    lastExternalEndpoint = ExternalEndpointBox.Text.Trim();
                    ExternalKeyBox.Clear();
                }
                ModelFieldsChanged();
            };
        foreach (var key in new[] { ExternalKeyBox, ModelKeyBox })
            key.PasswordChanged += (_, _) => { if (!changingModelFields) ModelFieldsChanged(); };
        foreach (var combo in new[] { ExternalAuthBox, ExternalFormatBox, ExternalTokenParameterBox,
            ExternalOutputBox, ExternalTimeoutBox, ExternalContextBox })
            combo.SelectionChanged += (_, _) =>
            {
                if (changingModelFields) return;
                if (ReferenceEquals(combo, ExternalAuthBox) && ExternalAuthBox.SelectedIndex == 1) ExternalKeyBox.Clear();
                ModelFieldsChanged();
            };
    }

    private void ModelFieldsChanged()
    {
        InvalidateModelTest();
        UpdateDestination();
        ScheduleSave();
    }

    private void InvalidateModelTest()
    {
        modelEditVersion++;
        modelTest?.Cancel();
        ModelTestStatus.Text = "Connection changed. Run Test model connection to check these settings.";
    }

    private void UpdateExternalModelUi()
    {
        if (ExternalModelPanel is null || AzureModelPanel is null) return;
        bool external = ModelConnectionBox.SelectedIndex >= 3;
        AzureModelPanel.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        ExternalModelPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        displayedExternal = external ? SelectedModelConnection : null;
        if (!external) return;
        var profile = externalProfiles.Get(SelectedModelConnection) ?? ExternalModelProfile.Default(SelectedModelConnection);
        changingModelFields = true;
        try
        {
            ExternalEndpointBox.Text = lastExternalEndpoint = profile.Endpoint;
            ExternalModelBox.Text = profile.Model;
            ExternalKeyBox.Password = profile.Key;
            ExternalAuthBox.SelectedIndex = (int)profile.Options.Authentication;
            ExternalFormatBox.SelectedIndex = (int)profile.Options.OutputFormat;
            ExternalTokenParameterBox.SelectedIndex = (int)profile.Options.TokenParameter;
            SelectNumber(ExternalOutputBox, profile.Options.MaxOutputTokens);
            SelectNumber(ExternalTimeoutBox, profile.Options.TimeoutSeconds);
            SelectNumber(ExternalContextBox, profile.Options.ContextCharacters);
        }
        finally { changingModelFields = false; }
        bool compatible = SelectedModelConnection == ModelConnection.OpenAICompatible;
        ExternalAuthPanel.Visibility = CompatibleFormatPanel.Visibility = compatible ? Visibility.Visible : Visibility.Collapsed;
        ExternalEndpointHelp.Text = SelectedModelConnection switch
        {
            ModelConnection.Gemini => "Gemini Developer API base URL ends in /v1beta. Enter the bare model ID, without models/. Uses this provider's x-goog-api-key header.",
            ModelConnection.Anthropic => "Claude Messages API base URL ends in /v1. Uses x-api-key and structured tool output; no tools are executed.",
            ModelConnection.Grok => "Grok Responses API base URL ends in /v1. Response storage is disabled in requests.",
            _ => "API base URL, usually ending in /v1. Examples: https://api.openai.com/v1, http://localhost:11434/v1 (Ollama), http://localhost:1234/v1 (LM Studio)."
        };
        UpdateDestination();
    }

    private void UpdateDestination()
    {
        if (ExternalKeyPanel is null) return;
        bool noKey = displayedExternal == ModelConnection.OpenAICompatible && ExternalAuthBox.SelectedIndex == 1;
        ExternalKeyPanel.Visibility = noKey ? Visibility.Collapsed : Visibility.Visible;
        ModelDestinationText.Text = Uri.TryCreate(ExternalEndpointBox.Text.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? $"Meeting text destination: {uri.GetLeftPart(UriPartial.Authority)}. Verify this is the service you intend to use."
            : "Enter a valid API base URL without embedded credentials.";
    }

    private ExternalModelProfiles ReadExternalProfiles()
    {
        if (displayedExternal is not { } connection) return externalProfiles;
        var authentication = connection == ModelConnection.OpenAICompatible
            ? (CompatibleAuthentication)ExternalAuthBox.SelectedIndex : CompatibleAuthentication.ApiKey;
        var profile = new ExternalModelProfile
        {
            Endpoint = ExternalEndpointBox.Text.Trim(), Model = ExternalModelBox.Text.Trim(),
            Key = authentication == CompatibleAuthentication.None ? "" : ExternalKeyBox.Password,
            Options = new(authentication, (CompatibleOutputFormat)ExternalFormatBox.SelectedIndex,
                (CompatibleTokenParameter)ExternalTokenParameterBox.SelectedIndex,
                SelectedNumber(ExternalOutputBox), SelectedNumber(ExternalTimeoutBox), SelectedNumber(ExternalContextBox))
        };
        return externalProfiles.With(connection, profile);
    }

    internal ModelSettings ReadModelSettings()
    {
        var connection = SelectedModelConnection;
        if (ModelConnectionBox.SelectedIndex >= 3)
        {
            var profile = ReadExternalProfiles().Get(connection)!;
            return new(profile.Endpoint, profile.Model, profile.Key, connection, Compatible: profile.Options);
        }
        return new(EndpointBox.Text.Trim(), DeploymentBox.Text.Trim(),
            connection == ModelConnection.ResponsesEntra ? "" : ModelKeyBox.Password, connection,
            connection == ModelConnection.ResponsesEntra ? EntraTenantBox.Text.Trim() : "");
    }

    private static int SelectedNumber(ComboBox combo) =>
        int.Parse((string)((ComboBoxItem)combo.SelectedItem).Tag, CultureInfo.InvariantCulture);

    private static void SelectNumber(ComboBox combo, int value) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == value.ToString(CultureInfo.InvariantCulture));

    private static async Task ProbeModelAsync(ModelSettings settings, CancellationToken cancellationToken)
    {
        using var http = MeetingModelFactory.CreateHttpClient();
        await MeetingModelFactory.TestAsync(MeetingModelFactory.Create(http, settings), cancellationToken);
    }

    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        if (modelTest is not null) return;
        long editVersion = modelEditVersion;
        using var cancel = new CancellationTokenSource();
        try
        {
            var settings = ReadModelSettings();
            var uri = settings.GetRequestUri();
            modelTest = cancel;
            cancel.CancelAfter(settings.Compatible is { } options ? TimeSpan.FromSeconds(options.TimeoutSeconds) : MeetingEngine.AnalysisTimeout);
            TestModelButton.IsEnabled = false;
            CancelModelTestButton.Visibility = Visibility.Visible;
            ModelTestStatus.Text = $"Testing synthetic text against {uri.GetLeftPart(UriPartial.Authority)}. No audio capture.";
            var watch = Stopwatch.StartNew();
            await modelProbe(settings, cancel.Token).WaitAsync(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (editVersion != modelEditVersion) return;
            ModelTestStatus.Text = $"Structured response accepted in {watch.Elapsed.TotalSeconds:F1}s. " +
                (settings.Connection is ModelConnection.ResponsesApiKey or ModelConnection.ResponsesEntra
                    ? "Slide-image capability was not tested; it requires a vision-capable deployment."
                    : "Text suggestions supported; slide images are not enabled on this connection.") +
                (watch.Elapsed.TotalSeconds > 15 ? " This model may be slow for live meetings." : "");
        }
        catch (OperationCanceledException)
        {
            if (editVersion == modelEditVersion) ModelTestStatus.Text = "Test canceled or timed out. No meeting audio was captured.";
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or ModelAuthenticationException or
            InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (editVersion == modelEditVersion)
                ModelTestStatus.Text = ex is ArgumentException or ModelAuthenticationException
                    ? ex.Message
                    : ex is HttpRequestException http && http.StatusCode is { } code
                        ? $"Provider returned HTTP {(int)code}. Check the URL, model ID, credentials, compatibility options and quota."
                        : "Connection test failed. Check server availability, output format and token budget. The model must return valid meeting-analysis JSON.";
        }
        finally
        {
            modelTest = null;
            TestModelButton.IsEnabled = true;
            CancelModelTestButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelModelTest_Click(object sender, RoutedEventArgs e) => modelTest?.Cancel();
}
