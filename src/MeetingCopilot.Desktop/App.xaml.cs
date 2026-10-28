using System.Windows;
using System.Windows.Media;
using System.ComponentModel;

namespace MeetingCopilot.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyContrastPalette(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += OnSystemPropertiesChanged;
        base.OnStartup(e);
    }

    private void OnSystemPropertiesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(() => ApplyContrastPalette(SystemParameters.HighContrast));
    }

    internal void ApplyContrastPalette(bool highContrast)
    {
        foreach (string key in new[] { "Surface", "Raised", "InputSurface", "Hover", "Selection", "Stroke",
            "Foreground", "Secondary", "Accent", "AccentInk", "Danger", "SelectedText", "InputStroke", "ScrollThumb", "PrompterSurface" })
            Resources.Remove(key);
        if (highContrast)
        {
            Resources["Surface"] = Resources["Raised"] = Resources["InputSurface"] = SystemColors.WindowBrush;
            Resources["PrompterSurface"] = SystemColors.WindowBrush;
            Resources["Hover"] = Resources["Selection"] = SystemColors.HighlightBrush;
            Resources["Stroke"] = SystemColors.WindowTextBrush;
            Resources["InputStroke"] = Resources["ScrollThumb"] = SystemColors.WindowTextBrush;
            Resources["Foreground"] = SystemColors.WindowTextBrush;
            Resources["Secondary"] = SystemColors.WindowTextBrush;
            Resources["Accent"] = SystemColors.HighlightBrush;
            Resources["AccentInk"] = SystemColors.HighlightTextBrush;
            Resources["SelectedText"] = SystemColors.HighlightTextBrush;
            Resources["Danger"] = SystemColors.WindowTextBrush;
        }
        foreach (Window window in Windows)
            if (WindowAppearance.GetEnabled(window)) WindowAppearance.Apply(window, highContrast);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemPropertiesChanged;
        base.OnExit(e);
    }
}
