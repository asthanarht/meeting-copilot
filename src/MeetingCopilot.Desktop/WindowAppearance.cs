using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeetingCopilot.Desktop;

public static class WindowAppearance
{
    private const int UseImmersiveDarkMode = 20, BorderColor = 34, CaptionColor = 35, TextColor = 36;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window) return;
        if ((bool)e.NewValue) window.SourceInitialized += OnSourceInitialized;
        else window.SourceInitialized -= OnSourceInitialized;
        Apply(window, SystemParameters.HighContrast);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window) Apply(window, SystemParameters.HighContrast);
    }

    internal static bool Apply(Window window, bool highContrast)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || window.WindowStyle == WindowStyle.None) return true;
        bool themed = GetEnabled(window) && !highContrast;
        bool applied = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            applied &= SetAttribute(handle, UseImmersiveDarkMode, themed ? 1 : 0);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return applied;

        // COLORREF is BGR, not WPF's ARGB. -1 hands colors back to Windows in high contrast.
        applied &= SetAttribute(handle, CaptionColor, themed ? ToColorRef((SolidColorBrush)window.FindResource("Surface")) : -1);
        applied &= SetAttribute(handle, TextColor, themed ? ToColorRef((SolidColorBrush)window.FindResource("Foreground")) : -1);
        applied &= SetAttribute(handle, BorderColor, themed ? ToColorRef((SolidColorBrush)window.FindResource("Stroke")) : -1);
        return applied;
    }

    private static int ToColorRef(SolidColorBrush brush) =>
        brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16;

    private static bool SetAttribute(nint handle, int attribute, int value)
    {
        int result = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        if (result < 0)
            Trace.TraceWarning($"Title bar attribute {attribute} could not be applied (HRESULT 0x{result:X8}); Windows chrome remains in use.");
        return result >= 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
