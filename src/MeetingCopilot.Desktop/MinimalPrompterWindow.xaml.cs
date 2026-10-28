using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MeetingCopilot.Core;

namespace MeetingCopilot.Desktop;

public partial class MinimalPrompterWindow : Window
{
    private readonly PrompterPlayback playback = new();
    private readonly Stopwatch clock = new();
    private readonly Action<nint> protectWindow;
    private TimeSpan lastFrame;
    private bool applyingPosition;
    private bool layingOut;
    private bool rendering;
    private bool closed;
    private bool protectedWindow;
    internal bool IsRunning => playback.IsPlaying;
    internal double Position => playback.Position;
    internal string CaptureError { get; private set; } = "";
    internal event EventHandler? LibraryRequested;

    internal MinimalPrompterWindow(PresentationScript script, Action<nint>? protectWindow = null)
    {
        script.Options.Validate();
        if (string.IsNullOrWhiteSpace(script.Text) || script.Text.Length > ScriptStore.MaxTextLength)
            throw new ArgumentException("A saved presentation script is required.", nameof(script));
        this.protectWindow = protectWindow ?? (handle => WindowCaptureProtection.Apply(handle, true));
        InitializeComponent();
        ScriptText.Text = script.Text;
        ScriptText.FontSize = script.Options.FontSize;
        ScriptText.LineHeight = script.Options.FontSize * script.Options.LineSpacing;
        playback.PixelsPerSecond = script.Options.Speed;
        Loaded += (_, _) => { ArrangeScript(); ScriptScroll.Focus(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Stop(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        Closed += (_, _) => { closed = true; Stop(); };
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 48));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 48));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 24;
    }

    // Keep private text hidden until the layered HWND has confirmed capture exclusion.
    private void ProtectContent()
    {
        protectedWindow = false;
        ProtectedContent.Visibility = Visibility.Hidden;
        CaptureError = "";
        try
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            protectWindow(handle);
            if (WindowCaptureProtection.Read(handle) != WindowCaptureProtection.ExcludeFromCapture)
                throw new InvalidOperationException("Capture exclusion was not confirmed.");
            protectedWindow = true;
            ProtectedContent.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            CaptureError = "The minimal overlay could not confirm Windows capture exclusion. It was closed without showing your script. Check Windows and your sharing setup before trying again.";
        }
    }

    internal bool ShowProtected()
    {
        if (closed) return false;
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Stop();
            ProtectContent();
            if (!protectedWindow)
            {
                Close();
                return false;
            }
            WindowState = WindowState.Normal;
            Show();
        }
        Activate();
        return true;
    }

    private void ArrangeScript()
    {
        if (!IsLoaded || layingOut || closed) return;
        layingOut = true;
        try
        {
            Stop();
            double fraction = playback.End > 0 ? playback.Position / playback.End : 0;
            EndSpace.Height = Math.Max(0, ScriptScroll.ViewportHeight - ScriptText.LineHeight);
            ScriptScroll.UpdateLayout();
            playback.SetExtent(Math.Max(0, Math.Min(ScriptScroll.ScrollableHeight, ScriptText.ActualHeight - ScriptText.LineHeight)));
            playback.Seek(fraction * playback.End);
            ApplyPosition();
        }
        finally { layingOut = false; }
    }

    internal void Run()
    {
        if (closed || !IsVisible || WindowState == WindowState.Minimized || !protectedWindow || playback.IsPlaying) return;
        if (playback.IsAtEnd) playback.Seek(0);
        playback.Play();
        if (playback.IsPlaying)
        {
            clock.Restart();
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
            rendering = true;
        }
        ApplyPosition();
    }

    internal void Stop()
    {
        playback.Pause();
        if (rendering) CompositionTarget.Rendering -= OnRendering;
        rendering = false;
        clock.Stop();
        UpdateState();
    }

    private void UpdateState()
    {
        RunButton.IsEnabled = protectedWindow && !playback.IsPlaying && playback.End > 0;
        StopButton.IsEnabled = playback.IsPlaying;
        PlaybackStatus.Text = playback.IsPlaying ? "Running" : playback.End == 0 ? "No scrolling needed"
            : playback.IsAtEnd ? "End - Run restarts" : "Stopped";
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = clock.Elapsed;
        AdvanceFrame(now - lastFrame);
        lastFrame = now;
    }

    internal void AdvanceFrame(TimeSpan elapsed)
    {
        if (closed) return;
        playback.Advance(elapsed);
        ApplyPosition();
        if (!playback.IsPlaying) Stop();
    }

    internal void Seek(double position)
    {
        Stop();
        playback.Seek(position);
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        applyingPosition = true;
        try
        {
            ScriptScroll.ScrollToVerticalOffset(playback.Position);
            ScriptScroll.UpdateLayout();
        }
        finally { applyingPosition = false; }
        UpdateState();
    }

    private void Script_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (applyingPosition || layingOut || e.VerticalChange == 0) return;
        Stop();
        playback.Seek(ScriptScroll.VerticalOffset);
        UpdateState();
    }

    private void Script_SizeChanged(object sender, SizeChangedEventArgs e) => ArrangeScript();
    private void Scroll_MouseWheel(object sender, MouseWheelEventArgs e) => Stop();
    private void Scroll_MouseDown(object sender, MouseButtonEventArgs e) => Stop();
    private void Run_Click(object sender, RoutedEventArgs e) => Run();
    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Library_Click(object sender, RoutedEventArgs e) => LibraryRequested?.Invoke(this, EventArgs.Empty);

    private void Move_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.LeftButton != MouseButtonState.Pressed) return;
        Stop();
        DragMove();
        e.Handled = true;
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Stop();
        Width = Math.Clamp(ActualWidth + e.HorizontalChange, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(ActualHeight + e.VerticalChange, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        e.Handled = true;
    }

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape) Stop();
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not ButtonBase)
        {
            if (!e.IsRepeat) { if (IsRunning) Stop(); else Run(); }
        }
        else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            Seek(e.Key switch
            {
                Key.Home => 0,
                Key.End => playback.End,
                Key.Up => Position - ScriptText.LineHeight,
                Key.Down => Position + ScriptText.LineHeight,
                Key.PageUp => Position - ScriptScroll.ViewportHeight,
                _ => Position + ScriptScroll.ViewportHeight
            });
        else return;
        e.Handled = true;
    }
}
