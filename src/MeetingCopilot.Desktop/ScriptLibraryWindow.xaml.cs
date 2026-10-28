using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MeetingCopilot.Desktop;

public partial class ScriptLibraryWindow : Window
{
    private static ScriptLibraryWindow? openLibrary;
    private readonly ScriptStore store;
    private readonly bool excludeFromCapture;
    private readonly ObservableCollection<PresentationScript> scripts = [];
    private PresentationScript current = new();
    private PrompterOptions draftOptions = new();
    private bool changing;
    private bool loadFailed;
    private bool closingReader;
    private bool closingLibrary;
    private Window? returnWindow;
    internal TeleprompterWindow? Reader { get; private set; }
    internal MinimalPrompterWindow? MinimalOverlay { get; private set; }
    internal bool HasUnsavedChanges => ScriptTitleBox.Text != current.Title ||
        ScriptBodyBox.Text != current.Text || draftOptions != current.Options;

    internal static ScriptLibraryWindow Open(Window previous, bool excludeFromCapture, ScriptStore? store = null)
    {
        if (openLibrary is not null)
        {
            openLibrary.ShowCurrentScreen();
            previous.Hide();
            return openLibrary;
        }
        openLibrary = new ScriptLibraryWindow(store ?? new ScriptStore(), excludeFromCapture);
        openLibrary.returnWindow = previous;
        previous.Closed += openLibrary.Previous_Closed;
        openLibrary.Closed += (_, _) => openLibrary = null;
        openLibrary.Show();
        previous.Hide();
        return openLibrary;
    }

    internal ScriptLibraryWindow(ScriptStore store, bool excludeFromCapture)
    {
        this.store = store;
        this.excludeFromCapture = excludeFromCapture;
        InitializeComponent();
        ScriptsList.ItemsSource = scripts;
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 48));
        SourceInitialized += (_, _) => ApplyCaptureProtection();
        Closing += Library_Closing;
        Closed += (_, _) =>
        {
            if (returnWindow is not { } previous) return;
            previous.Closed -= Previous_Closed;
            returnWindow = null;
            if (Application.Current.Dispatcher.HasShutdownStarted || !Application.Current.Windows.Cast<Window>().Contains(previous)) return;
            previous.Show();
            if (previous.WindowState == WindowState.Minimized) previous.WindowState = WindowState.Normal;
            previous.Activate();
        };
        LoadLibrary();
    }

    private void Previous_Closed(object? sender, EventArgs e)
    {
        if (returnWindow is { } previous) previous.Closed -= Previous_Closed;
        returnWindow = null;
        Close();
    }

    private void ShowCurrentScreen()
    {
        if (Reader is { } reader)
        {
            reader.Show();
            if (reader.WindowState == WindowState.Minimized) reader.WindowState = WindowState.Normal;
            reader.Activate();
            Hide();
        }
        else if (MinimalOverlay is { IsVisible: true } minimal) ShowMinimalOverlay(minimal);
        else ReturnToLibrary();
    }

    private void ReturnToLibrary()
    {
        if (closingLibrary || closingReader || Application.Current.Dispatcher.HasShutdownStarted) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadLibrary()
    {
        try
        {
            var saved = store.Load();
            changing = true;
            try
            {
                scripts.Clear();
                foreach (var script in saved) scripts.Add(script);
            }
            finally { changing = false; }
            loadFailed = false;
            LibraryError.Text = "";
            SetCurrent(scripts.FirstOrDefault() ?? new());
            LibraryStatus.Text = scripts.Count == 0 ? "Create your first script. Nothing is sent to the cloud."
                : $"{scripts.Count} saved {(scripts.Count == 1 ? "script" : "scripts")}. Choose one or create a new script.";
        }
        catch (Exception ex) when (ScriptStore.IsStorageError(ex))
        {
            loadFailed = true;
            LibraryError.Text = "The script library could not be read or decrypted. It has not been overwritten. Check file access and use Reload.";
        }
        UpdateControls();
    }

    private void SetCurrent(PresentationScript script)
    {
        changing = true;
        try
        {
            current = script;
            draftOptions = script.Options;
            ScriptTitleBox.Text = script.Title;
            ScriptBodyBox.Text = script.Text;
            ScriptsList.SelectedItem = scripts.FirstOrDefault(s => s.Id == script.Id);
        }
        finally { changing = false; }
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (ScriptTitleBox is null || ScriptBodyBox is null) return;
        CharacterCount.Text = $"{ScriptBodyBox.Text.Length:N0} / 100,000";
        LibraryEmpty.Visibility = scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewScriptButton.IsEnabled = SaveScriptButton.IsEnabled = !loadFailed;
        ScriptTitleBox.IsEnabled = ScriptBodyBox.IsEnabled = !loadFailed;
        DeleteScriptButton.IsEnabled = !loadFailed && current.Revision != Guid.Empty;
        OpenReaderButton.IsEnabled = !loadFailed && MinimalOverlay is null;
        OpenReaderButton.Content = Reader is not null ? "Return to reader" : "Save and open reader";
        OpenMinimalButton.IsEnabled = !loadFailed && Reader is null;
        OpenMinimalButton.Content = MinimalOverlay is not null ? "Return to minimal overlay" : "Open minimal overlay";
        ToggleMinimalButton.Visibility = MinimalOverlay is null ? Visibility.Collapsed : Visibility.Visible;
        ToggleMinimalButton.Content = MinimalOverlay is { IsVisible: true, WindowState: not WindowState.Minimized }
            ? "Hide overlay" : "Show overlay";
    }

    private void Draft_Changed(object sender, TextChangedEventArgs e)
    {
        if (changing || LibraryStatus is null || ScriptBodyBox is null) return;
        UpdateControls();
        LibraryStatus.Text = HasUnsavedChanges ? "Unsaved changes. Save to keep this script on this device." : "No unsaved changes.";
    }

    internal bool SaveDraft()
    {
        if (loadFailed) return false;
        try
        {
            var saved = store.Save(current with { Title = ScriptTitleBox.Text, Text = ScriptBodyBox.Text, Options = draftOptions });
            changing = true;
            try
            {
                int index = scripts.ToList().FindIndex(s => s.Id == saved.Id);
                if (index < 0) scripts.Add(saved);
                else scripts[index] = saved;
            }
            finally { changing = false; }
            SetCurrent(saved);
            LibraryError.Text = "";
            LibraryStatus.Text = "Script and reading settings saved on this device.";
            return true;
        }
        catch (Exception ex) when (ScriptStore.IsStorageError(ex))
        {
            LibraryError.Text = ex is InvalidDataException || ex is IOException && ex.Message.StartsWith("This script", StringComparison.Ordinal)
                ? ex.Message : "Could not save this script. Your draft is intact. Check file access, close another writer if necessary, and retry Save script.";
            return false;
        }
    }

    private bool ConfirmDraft()
    {
        if (!HasUnsavedChanges) return true;
        var decision = MessageBox.Show(this, "Save your changes before leaving this script?", "Unsaved script",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return decision == MessageBoxResult.No || decision == MessageBoxResult.Yes && SaveDraft();
    }

    private void Script_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (changing || ScriptsList.SelectedItem is not PresentationScript selected) return;
        if (ConfirmDraft()) { SetCurrent(selected); LibraryError.Text = ""; LibraryStatus.Text = "Saved script ready to rehearse."; }
        else
        {
            changing = true;
            try { ScriptsList.SelectedItem = scripts.FirstOrDefault(s => s.Id == current.Id); }
            finally { changing = false; }
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDraft()) return;
        SetCurrent(new());
        LibraryError.Text = "";
        LibraryStatus.Text = "New script. Add a title and paste your presentation text.";
        ScriptTitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveDraft();
    private void Reload_Click(object sender, RoutedEventArgs e) { if (ConfirmDraft()) LoadLibrary(); }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete the selected saved script and any unsaved edits? This cannot be undone.",
                "Delete script", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            store.Delete(current);
            changing = true;
            try
            {
                var entry = scripts.Single(s => s.Id == current.Id);
                scripts.Remove(entry);
            }
            finally { changing = false; }
            SetCurrent(scripts.FirstOrDefault() ?? new());
            LibraryError.Text = "";
            LibraryStatus.Text = "Script deleted from this device.";
        }
        catch (Exception ex) when (ScriptStore.IsStorageError(ex))
        {
            LibraryError.Text = "Could not delete the script. It may have changed in another window. Reload the library and try again.";
        }
    }

    private void OpenReader_Click(object sender, RoutedEventArgs e)
    {
        if (MinimalOverlay is not null) return;
        if (Reader is not null)
        {
            if (Reader.WindowState == WindowState.Minimized) Reader.WindowState = WindowState.Normal;
            Reader.Show();
            Reader.Activate();
            Hide();
            return;
        }
        if ((HasUnsavedChanges || current.Revision == Guid.Empty) && !SaveDraft()) return;
        Reader = new TeleprompterWindow(current, excludeFromCapture);
        var window = Reader;
        LibraryWorkspace.IsEnabled = false;
        window.Closed += (_, _) =>
        {
            Reader = null;
            LibraryWorkspace.IsEnabled = true;
            LibraryStatus.Text = "Reader closed. Your saved script is ready for another rehearsal.";
            if (window.Options != current.Options)
            {
                draftOptions = window.Options;
                SaveDraft();
            }
            UpdateControls();
            ReturnToLibrary();
        };
        LibraryStatus.Text = "Reader open. Your script is locked for editing until you close the reader.";
        UpdateControls();
        window.Show();
        Hide();
    }

    private void OpenMinimal_Click(object sender, RoutedEventArgs e)
    {
        if (Reader is not null) return;
        if (MinimalOverlay is { } existing)
        {
            ShowMinimalOverlay(existing);
            return;
        }
        if ((HasUnsavedChanges || current.Revision == Guid.Empty) && !SaveDraft()) return;
        var window = new MinimalPrompterWindow(current);
        // Readers are unowned so hiding the previous screen does not hide its reader.
        MinimalOverlay = window;
        LibraryWorkspace.IsEnabled = false;
        window.IsVisibleChanged += (_, _) => UpdateControls();
        window.StateChanged += (_, _) => UpdateControls();
        window.LibraryRequested += (_, _) =>
        {
            window.Hide();
            LibraryStatus.Text = "Overlay hidden and scrolling stopped. Show overlay returns to the same position; Run resumes.";
            ReturnToLibrary();
        };
        window.Closed += (_, _) =>
        {
            MinimalOverlay = null;
            LibraryWorkspace.IsEnabled = true;
            LibraryStatus.Text = "Minimal overlay closed. Your saved script is unchanged.";
            UpdateControls();
            ReturnToLibrary();
        };
        ShowMinimalOverlay(window);
    }

    private void ShowMinimalOverlay(MinimalPrompterWindow window)
    {
        if (!window.ShowProtected())
        {
            LibraryError.Text = window.CaptureError;
            ReturnToLibrary();
            return;
        }
        LibraryError.Text = "";
        LibraryStatus.Text = "Minimal overlay ready. Show overlay returns to your position. Closing the library closes the overlay.";
        UpdateControls();
        Hide();
    }

    private void ToggleMinimal_Click(object sender, RoutedEventArgs e)
    {
        if (MinimalOverlay is not { } window) return;
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
        {
            ShowMinimalOverlay(window);
            return;
        }
        window.Hide();
        LibraryStatus.Text = "Overlay hidden and scrolling stopped. Show overlay returns to the same position; Run resumes.";
        UpdateControls();
        ReturnToLibrary();
    }

    private async void Library_Closing(object? sender, CancelEventArgs e)
    {
        if (closingReader) { e.Cancel = true; return; }
        if (Reader is { } reader)
        {
            e.Cancel = true;
            closingReader = true;
            closingLibrary = true;
            await reader.CloseReaderAsync();
            closingReader = false;
            closingLibrary = false;
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        if (HasUnsavedChanges) ReturnToLibrary();
        if (!ConfirmDraft()) { e.Cancel = true; return; }
        closingLibrary = true;
        MinimalOverlay?.Close();
    }

    private void ApplyCaptureProtection()
    {
        try
        {
            WindowCaptureProtection.Apply(new WindowInteropHelper(this).Handle, excludeFromCapture);
            CaptureStatus.Text = excludeFromCapture ? "Library capture exclusion on - supported tools only; verify your sharing app."
                : "Capture exclusion off - this library may appear in screen sharing.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            CaptureStatus.Text = "Library capture exclusion not confirmed. Your scripts may appear in screen sharing; share only an application window.";
        }
    }
}
