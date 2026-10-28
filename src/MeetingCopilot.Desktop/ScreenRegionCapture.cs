using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

internal static class ScreenRegionCapture
{
    internal static Int32Rect DesktopBounds => new(GetSystemMetrics(76), GetSystemMetrics(77),
        GetSystemMetrics(78), GetSystemMetrics(79));

    internal static Point CursorPosition()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }

    internal static Int32Rect Selection(Point first, Point last)
    {
        int left = (int)Math.Min(first.X, last.X), top = (int)Math.Min(first.Y, last.Y);
        return new(left, top, (int)Math.Abs(last.X - first.X), (int)Math.Abs(last.Y - first.Y));
    }

    internal static void CoverDesktop(nint window)
    {
        var bounds = DesktopBounds;
        if (!SetWindowPos(window, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static byte[] Capture(Int32Rect region)
    {
        var desktop = DesktopBounds;
        if (region.Width < 16 || region.Height < 16 || region.X < desktop.X || region.Y < desktop.Y ||
            (long)region.X + region.Width > (long)desktop.X + desktop.Width ||
            (long)region.Y + region.Height > (long)desktop.Y + desktop.Height)
            throw new ArgumentException("Select a slide region of at least 16 by 16 pixels within the desktop.");
        double scale = Math.Min(1, (double)SlideImage.MaxDimension / Math.Max(region.Width, region.Height));
        int width = Math.Max(1, (int)(region.Width * scale)), height = Math.Max(1, (int)(region.Height * scale));
        nint screen = GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint destination = 0, bitmap = 0, previous = 0;
        try
        {
            destination = CreateCompatibleDC(screen);
            if (destination == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(destination, bitmap);
            if (previous == 0 || previous == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetStretchBltMode(destination, 4) == 0 ||
                !StretchBlt(destination, 0, 0, width, height, screen,
                    region.X, region.Y, region.Width, region.Height, 0x00CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Screen bitmaps may contain undefined alpha; encode opaque RGB pixels.
            var rgb = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rgb));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var png = stream.ToArray();
            SlideImage.Validate(png);
            return png;
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1)) SelectObject(destination, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (destination != 0) DeleteDC(destination);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
