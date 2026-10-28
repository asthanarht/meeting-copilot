using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class SlidePreviewWindow : Window
{
    internal SlidePreviewWindow(byte[] png, bool excludeFromCapture)
    {
        SlideImage.Validate(png);
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 48);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 48);
        using var stream = new MemoryStream(png, false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        SlideImagePreview.Source = bitmap;
        Closed += (_, _) => SlideImagePreview.Source = null;
        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowCaptureProtection.Apply(new WindowInteropHelper(this).Handle, excludeFromCapture);
                PrivacyStatus.Text = excludeFromCapture
                    ? "Preview capture exclusion on - compatible tools only."
                    : "Capture exclusion off: this preview may appear in screen sharing.";
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                PrivacyStatus.Text = "Preview capture exclusion failed. This preview may appear in screen sharing.";
            }
        };
    }

    private void Send_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
