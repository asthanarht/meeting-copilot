using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MeetingCopilot.Desktop;

internal sealed class SlideRegionWindow : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle outline = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly TextBlock instruction = new()
    {
        Text = "Drag around the slide only. Escape cancels.\nNothing is sent until you approve the preview.",
        Background = new SolidColorBrush(Color.FromRgb(32, 39, 43)),
        Foreground = Brushes.White, FontSize = 18, Padding = new Thickness(16), IsHitTestVisible = false
    };
    private Point? start;
    internal Int32Rect? SelectedRegion { get; private set; }

    internal SlideRegionWindow()
    {
        Title = "Select slide region";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Content = canvas;
        canvas.Children.Add(outline);
        canvas.Children.Add(instruction);
        Loaded += (_, _) =>
        {
            ScreenRegionCapture.CoverDesktop(new WindowInteropHelper(this).Handle);
            var cursor = PointFromScreen(ScreenRegionCapture.CursorPosition());
            Canvas.SetLeft(instruction, Math.Max(0, Math.Min(cursor.X, ActualWidth - 520)));
            Canvas.SetTop(instruction, Math.Max(0, Math.Min(cursor.Y - 90, ActualHeight - 100)));
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        MouseLeftButtonDown += (_, _) =>
        {
            start = ScreenRegionCapture.CursorPosition();
            instruction.Visibility = Visibility.Collapsed;
            CaptureMouse();
        };
        MouseMove += (_, _) =>
        {
            if (start is not { } first) return;
            var from = PointFromScreen(first);
            var to = PointFromScreen(ScreenRegionCapture.CursorPosition());
            Canvas.SetLeft(outline, Math.Min(from.X, to.X));
            Canvas.SetTop(outline, Math.Min(from.Y, to.Y));
            outline.Width = Math.Abs(to.X - from.X);
            outline.Height = Math.Abs(to.Y - from.Y);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (start is not { } first) return;
            var selected = ScreenRegionCapture.Selection(first, ScreenRegionCapture.CursorPosition());
            ReleaseMouseCapture();
            start = null;
            if (selected.Width < 16 || selected.Height < 16)
            {
                instruction.Text = "That selection is too small. Drag around the slide, or Escape to cancel.";
                instruction.Visibility = Visibility.Visible;
                return;
            }
            SelectedRegion = selected;
            DialogResult = true;
        };
    }
}
