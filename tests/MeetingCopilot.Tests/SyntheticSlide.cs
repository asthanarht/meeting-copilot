using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeetingCopilot.Tests;

internal static class SyntheticSlide
{
    internal static byte[] Png()
    {
        byte[]? png = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 600));
                    var text = new FormattedText(
                        "SYNTHETIC ARCHIVE INGESTION\n\nIncoming files -> Workers -> Storage\n\nRetention: 30 days\n\nUploads acknowledged after storage.\nErrors return to the retry queue.",
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 30, Brushes.Black, 1);
                    drawing.DrawText(text, new Point(40, 40));
                }
                var bitmap = new RenderTargetBitmap(1000, 600, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                png = stream.ToArray();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic image rendering timed out.");
        if (error is not null) throw new InvalidOperationException("Synthetic image rendering failed.", error);
        return png!;
    }
}
