using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LunaDrome.Services;

/// <summary>Win32 access to LunaDrome's own window (media flyout registration, bring to front).</summary>
[SupportedOSPlatform("windows")]
internal static class NativeWindow
{
    // Process.MainWindowHandle isn't reliable here: a debug build also owns a console window.
    // Take this process's visible top-level window that isn't a console (Uno's window).
    public static IntPtr FindAppWindow()
    {
        var pid = (uint)Environment.ProcessId;
        IntPtr found = IntPtr.Zero;
        var className = new char[64];
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != pid || !IsWindowVisible(hwnd))
                return true;
            var length = GetClassName(hwnd, className, className.Length);
            if (new string(className, 0, length) == "ConsoleWindowClass")
                return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Restores the window if minimized and gives it focus.</summary>
    public static void BringToFront()
    {
        var hwnd = FindAppWindow();
        if (hwnd == IntPtr.Zero)
            return;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
