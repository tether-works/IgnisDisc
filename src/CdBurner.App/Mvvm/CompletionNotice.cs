using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CdBurner.App.Mvvm;

/// <summary>
/// Tells the user a long operation (a burn, a disc copy, a burn queue) has ended: the Windows notification sound, the
/// taskbar button flashing until the window is brought forward when IgnisDisc is not in front, and a message box with
/// the result. Shown only when the main window is open and visible (never from tests or headless use) and while
/// <see cref="Enabled"/> (the "Tell me when a burn finishes" setting).
/// </summary>
public static class CompletionNotice
{
    /// <summary>Whether notices are shown; set from the settings at start-up and when they change.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Announces the end of an operation; safe to call from any thread.</summary>
    public static void Show(string title, string message, bool success)
    {
        var app = Application.Current;
        if (!Enabled || app == null)
        {
            return;
        }

        void Run()
        {
            var window = app.MainWindow;
            if (window == null || !window.IsVisible)
            {
                return;
            }
            (success ? SystemSounds.Asterisk : SystemSounds.Exclamation).Play();
            if (!window.IsActive)
            {
                Flash(window);
            }
            MessageBox.Show(window, message, title, MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // Always queued, never run inline: the burn code that calls this goes on to eject the disc and tidy up, and a
        // message box shown inline would hold that up until the user clicks OK.
        var dispatcher = app.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false } && dispatcher.Thread.IsAlive)
        {
            dispatcher.BeginInvoke(Run);
        }
    }

    private static void Flash(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashAll = 0x00000003;            // FLASHW_ALL: caption and taskbar button
    private const uint FlashUntilForeground = 0x0000000C; // FLASHW_TIMERNOFG: until the window comes to the front

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}
