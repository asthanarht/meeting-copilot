using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetingCopilot.Core;
using NAudio.CoreAudioApi;

namespace MeetingCopilot.Desktop;

public partial class MainWindow : Window
{
    private OverlayWindow? overlay;
    private readonly SettingsStore settingsStore;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool suppressSave;
    private bool pendingSave;
    private bool loadFailed;
    private string preferredPlayback = "";
    private string preferredMicrophone = "";

    public MainWindow() : this(new SettingsStore()) { }

    internal MainWindow(SettingsStore settingsStore, Func<ModelSettings, CancellationToken, Task>? modelProbe = null)
    {
        this.settingsStore = settingsStore;
        this.modelProbe = modelProbe ?? ProbeModelAsync;
        InitializeComponent();
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 40));
        RegionBox.Text = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "";
        SpeechEndpointBox.Text = Environment.GetEnvironmentVariable("AZURE_SPEECH_ENDPOINT")
            ?? "";
        SpeechKeyBox.Password = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? "";
        EndpointBox.Text = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? "";
        DeploymentBox.Text = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "";
        ModelKeyBox.Password = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "";
        EntraTenantBox.Text = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";
        LoadSettings();
        UpdateModelConnectionUi();
        UpdateSpeechProviderUi();
        RefreshDevices();
        ConnectSettingsEvents();
        ConnectModelEvents();
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SaveSettings();
        };
        Closing += OnClosing;
        Closed += (_, _) => { saveTimer.Stop(); modelTest?.Cancel(); };
    }

    private void LoadSettings()
    {
        try
        {
            var saved = settingsStore.Load();
            if (saved is null) return;
            externalProfiles = saved.ExternalModels;
            // Saved choices win over environment-prefilled defaults, including explicitly cleared keys.
            SpeechProviderBox.SelectedIndex = saved.SpeechProvider == SpeechProvider.MaiTranscribe2 ? 0 : 1;
            SpeechEndpointBox.Text = saved.SpeechEndpoint;
            RegionBox.Text = saved.SpeechRegion;
            SpeechKeyBox.Password = saved.SpeechKey;
            LocaleBox.SelectedItem = LocaleBox.Items.Cast<ComboBoxItem>().Single(i => (string)i.Content == saved.Locale);
            ModelConnectionBox.SelectedIndex = saved.ModelConnection switch
            {
                ModelConnection.ResponsesEntra => 0,
                ModelConnection.ChatCompletionsApiKey => 1,
                ModelConnection.ResponsesApiKey => 2,
                ModelConnection.OpenAICompatible => 3,
                ModelConnection.Gemini => 4,
                ModelConnection.Anthropic => 5,
                ModelConnection.Grok => 6,
                _ => throw new InvalidDataException("Unsupported model connection.")
            };
            EndpointBox.Text = saved.ModelEndpoint;
            DeploymentBox.Text = saved.Deployment;
            ModelKeyBox.Password = saved.ModelKey;
            EntraTenantBox.Text = saved.TenantId;
            preferredPlayback = saved.PlaybackDeviceId;
            preferredMicrophone = saved.MicrophoneDeviceId;
            IncludeMic.IsChecked = saved.IncludeMicrophone;
            ExcludeCaptureBox.IsChecked = saved.ExcludeOverlayFromCapture;
            SettingsStatus.Text = "Saved configuration loaded. Keys are protected for your Windows account.";
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            loadFailed = true;
            SettingsStatus.Text = "Saved settings could not be read or decrypted. They were not overwritten. Use Forget saved settings to reset.";
        }
    }

    private void ConnectSettingsEvents()
    {
        foreach (var text in new[] { SpeechEndpointBox, RegionBox, EndpointBox, DeploymentBox, EntraTenantBox })
            text.TextChanged += (_, _) => ScheduleSave();
        foreach (var key in new[] { SpeechKeyBox, ModelKeyBox })
            key.PasswordChanged += (_, _) => ScheduleSave();
        foreach (var combo in new[] { SpeechProviderBox, LocaleBox, ModelConnectionBox, PlaybackBox, MicrophoneBox })
            combo.SelectionChanged += (_, _) =>
            {
                if (!suppressSave)
                {
                    preferredPlayback = (PlaybackBox.SelectedItem as AudioDevice)?.Id ?? preferredPlayback;
                    preferredMicrophone = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? preferredMicrophone;
                }
                ScheduleSave();
            };
        IncludeMic.Checked += (_, _) => ScheduleSave();
        IncludeMic.Unchecked += (_, _) => ScheduleSave();
        ExcludeCaptureBox.Checked += (_, _) => ScheduleSave();
        ExcludeCaptureBox.Unchecked += (_, _) => ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (suppressSave || loadFailed) return;
        pendingSave = true;
        SettingsStatus.Text = "Saving configuration...";
        saveTimer.Stop();
        saveTimer.Start();
    }

    private ConnectionSettings ReadSettings() => new()
    {
        SpeechProvider = SpeechProviderBox.SelectedIndex == 0 ? SpeechProvider.MaiTranscribe2 : SpeechProvider.AzureStreaming,
        SpeechEndpoint = SpeechEndpointBox.Text.Trim(),
        SpeechRegion = RegionBox.Text.Trim(),
        SpeechKey = SpeechKeyBox.Password,
        Locale = (string)((ComboBoxItem)LocaleBox.SelectedItem).Content,
        ModelConnection = SelectedModelConnection,
        ModelEndpoint = EndpointBox.Text.Trim(),
        Deployment = DeploymentBox.Text.Trim(),
        ModelKey = ModelKeyBox.Password,
        TenantId = EntraTenantBox.Text.Trim(),
        ExternalModels = ReadExternalProfiles(),
        PlaybackDeviceId = preferredPlayback,
        MicrophoneDeviceId = preferredMicrophone,
        IncludeMicrophone = IncludeMic.IsChecked == true,
        ExcludeOverlayFromCapture = ExcludeCaptureBox.IsChecked == true
    };

    private bool SaveSettings()
    {
        saveTimer.Stop();
        if (loadFailed) return false;
        try
        {
            settingsStore.Save(ReadSettings());
            pendingSave = false;
            SettingsStatus.Text = "Configuration saved. API keys are encrypted for your Windows account.";
            return true;
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            SettingsStatus.Text = "Configuration could not be saved. Check access to LocalAppData\\MeetingCopilot and retry Save configuration.";
            return false;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void ForgetSettings_Click(object sender, RoutedEventArgs e)
    {
        saveTimer.Stop();
        try
        {
            settingsStore.Delete();
            suppressSave = true;
            SpeechKeyBox.Clear();
            ModelKeyBox.Clear();
            ExternalKeyBox.Clear();
            externalProfiles = externalProfiles.MapKeys(_ => "");
            InvalidateModelTest();
            pendingSave = false;
            loadFailed = false;
            SettingsStatus.Text = "Saved settings removed and key fields cleared. Further edits or starting live mode will save configuration again.";
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            SettingsStatus.Text = "Saved settings could not be removed. Check file permissions and try again.";
        }
        finally { suppressSave = false; }
    }

    private static bool IsSettingsError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException
            or FormatException or System.Security.SecurityException or NotSupportedException;

    private void RefreshDevices()
    {
        suppressSave = true;
        try
        {
            var playbackId = (PlaybackBox.SelectedItem as AudioDevice)?.Id ?? preferredPlayback;
            var micId = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? preferredMicrophone;
            PlaybackBox.ItemsSource = AudioDevices.List(DataFlow.Render);
            MicrophoneBox.ItemsSource = AudioDevices.List(DataFlow.Capture);
            PlaybackBox.SelectedItem = PlaybackBox.Items.Cast<AudioDevice>().FirstOrDefault(d => d.Id == playbackId)
                ?? (string.IsNullOrEmpty(playbackId) ? PlaybackBox.Items.Cast<AudioDevice>().FirstOrDefault() : null);
            MicrophoneBox.SelectedItem = MicrophoneBox.Items.Cast<AudioDevice>().FirstOrDefault(d => d.Id == micId)
                ?? (string.IsNullOrEmpty(micId) ? MicrophoneBox.Items.Cast<AudioDevice>().FirstOrDefault() : null);
            preferredPlayback = (PlaybackBox.SelectedItem as AudioDevice)?.Id ?? playbackId;
            preferredMicrophone = (MicrophoneBox.SelectedItem as AudioDevice)?.Id ?? micId;
            ErrorText.Text = PlaybackBox.Items.Count == 0 ? "No active playback device was found."
                : PlaybackBox.SelectedItem is null ? "Saved playback device is unavailable. Select the device used by Teams." : "";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ErrorText.Text = "Could not enumerate audio devices. Check Windows audio settings and refresh.";
        }
        finally { suppressSave = false; }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private ModelConnection SelectedModelConnection => ModelConnectionBox.SelectedIndex switch
    {
        0 => ModelConnection.ResponsesEntra,
        1 => ModelConnection.ChatCompletionsApiKey,
        2 => ModelConnection.ResponsesApiKey,
        3 => ModelConnection.OpenAICompatible,
        4 => ModelConnection.Gemini,
        5 => ModelConnection.Anthropic,
        6 => ModelConnection.Grok,
        _ => throw new ArgumentException("Select a model connection.")
    };

    private void ModelConnectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (modelFieldsReady) externalProfiles = ReadExternalProfiles();
        UpdateModelConnectionUi();
        if (modelFieldsReady) InvalidateModelTest();
    }

    private void UpdateModelConnectionUi()
    {
        if (ModelKeyPanel is null || EntraHelp is null || ModelEndpointLabel is null || EntraTenantPanel is null) return;
        bool entra = ModelConnectionBox.SelectedIndex == 0;
        ModelKeyPanel.Visibility = entra ? Visibility.Collapsed : Visibility.Visible;
        EntraHelp.Visibility = entra ? Visibility.Visible : Visibility.Collapsed;
        EntraTenantPanel.Visibility = entra ? Visibility.Visible : Visibility.Collapsed;
        ModelEndpointLabel.Text = SelectedModelConnection == ModelConnection.ChatCompletionsApiKey
            ? "Azure OpenAI resource root URL (no /openai/v1 path)"
            : "Foundry endpoint ending in /openai/v1";
        UpdateExternalModelUi();
    }
    private void SpeechProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateSpeechProviderUi();

    private void UpdateSpeechProviderUi()
    {
        if (MaiSettingsPanel is null || StreamingSettingsPanel is null) return;
        bool mai = SpeechProviderBox.SelectedIndex == 0;
        MaiSettingsPanel.Visibility = mai ? Visibility.Visible : Visibility.Collapsed;
        StreamingSettingsPanel.Visibility = mai ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        int validationTab = 0;
        try
        {
            ErrorText.Text = "";
            if (overlay is not null) { overlay.Activate(); return; }
            if (ConsentBox.IsChecked != true)
                throw new ArgumentException("Confirm consent and organizational permission before starting.");
            if (PlaybackBox.SelectedItem is not AudioDevice playback)
                throw new ArgumentException("Select the playback device used by Teams.");
            if (IncludeMic.IsChecked == true && MicrophoneBox.SelectedItem is not AudioDevice)
                throw new ArgumentException("Select your microphone or disable microphone capture.");
            var region = RegionBox.Text.Trim();
            validationTab = 1;
            var speechProvider = SpeechProviderBox.SelectedIndex == 0
                ? SpeechProvider.MaiTranscribe2 : SpeechProvider.AzureStreaming;
            if (string.IsNullOrWhiteSpace(SpeechKeyBox.Password))
                throw new ArgumentException("Enter the Speech API key.");
            if (speechProvider == SpeechProvider.AzureStreaming)
                StreamingSpeechConfiguration.Validate(SpeechKeyBox.Password, region, SpeechEndpointBox.Text);
            var model = ReadModelSettings();
            _ = model.GetRequestUri();
            string[] phrases = PhrasesBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            validationTab = 0;
            if (phrases.Length > 50 || phrases.Any(p => p.Length > 100))
                throw new ArgumentException("Use up to 50 vocabulary hints, each at most 100 characters.");
            validationTab = 1;
            if (speechProvider == SpeechProvider.MaiTranscribe2)
                _ = new MaiSpeechSettings(SpeechEndpointBox.Text.Trim(), SpeechKeyBox.Password, phrases).GetRequestUri();
            var settings = new SessionSettings(SpeechKeyBox.Password, region,
                ((ComboBoxItem)LocaleBox.SelectedItem).Content.ToString()!,
                playback.Id, IncludeMic.IsChecked == true ? ((AudioDevice)MicrophoneBox.SelectedItem).Id : null,
                phrases, BriefBox.Text, model, speechProvider, SpeechEndpointBox.Text.Trim());
            if (!SaveSettings()) return;
            ShowOverlay(new OverlayWindow(new MeetingSession(settings), ExcludeCaptureBox.IsChecked == true));
            await overlay!.StartLiveAsync();
        }
        catch (ArgumentException ex)
        {
            SetupTabs.SelectedIndex = validationTab;
            ErrorText.Text = ex.Message;
        }
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (overlay is not null) { overlay.Activate(); return; }
        ShowOverlay(new OverlayWindow(null, ExcludeCaptureBox.IsChecked == true));
    }

    private void Teleprompter_Click(object sender, RoutedEventArgs e) =>
        ScriptLibraryWindow.Open(this, ExcludeCaptureBox.IsChecked == true);

    private void ShowOverlay(OverlayWindow window)
    {
        modelTest?.Cancel();
        overlay = window;
        window.CaptureExclusionChanged += enabled =>
        {
            ExcludeCaptureBox.IsChecked = enabled;
            if (!SaveSettings()) window.ReportCapturePreferenceSaveFailure();
        };
        window.Closed += (_, _) =>
        {
            overlay = null;
            StartButton.IsEnabled = true;
            SetupPanel.IsEnabled = true;
            Show();
            Activate();
        };
        StartButton.IsEnabled = false;
        SetupPanel.IsEnabled = false;
        window.Show();
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (overlay is not null)
        {
            e.Cancel = true;
            overlay.Close();
            return;
        }
        if (pendingSave && !SaveSettings()) e.Cancel = true;
        if (!e.Cancel) modelTest?.Cancel();
    }
}
