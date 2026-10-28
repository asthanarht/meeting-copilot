using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MeetingCopilot.Desktop;

internal static class WindowCaptureProtection
{
    internal const uint ExcludeFromCapture = 0x11;

    public static void Apply(nint window, bool enabled)
    {
        if (window == 0) throw new ArgumentException("A created top-level window is required.");
        if (enabled && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Capture exclusion requires Windows 10 version 2004 or later.");
        uint requested = enabled ? ExcludeFromCapture : 0;
        if (!SetWindowDisplayAffinity(window, requested))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (Read(window) != requested)
            throw new InvalidOperationException("Windows did not confirm the requested capture setting.");
    }

    public static uint Read(nint window)
    {
        if (window == 0) throw new ArgumentException("A created top-level window is required.");
        if (!GetWindowDisplayAffinity(window, out uint affinity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return affinity;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hWnd, out uint affinity);
}
