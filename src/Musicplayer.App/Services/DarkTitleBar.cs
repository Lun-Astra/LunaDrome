using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Musicplayer.App.Services;

/// <summary>
/// Forces the native Windows title bar into dark mode (Windows 10 20H1+ / Windows 11)
/// so it matches the app's dark theme instead of staying the default light chrome.
/// </summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    // Windows 11 22H2+ only; DwmSetWindowAttribute just no-ops on older builds.
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    public static void Apply(Window window)
    {
        void TryApply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDark = 1;
            var result = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            if (result != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref useDark, sizeof(int));

            // Feishin (Electron) shows no native window border at all; drop Windows 11's
            // default thin accent-colored frame instead of trying to recolor it to match.
            int noBorder = unchecked((int)DwmwaColorNone);
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));

            int captionColor = 0x001E1B1A; // COLORREF is 0x00BBGGRR, not 0x00RRGGBB - this is #1A1B1E
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
        }

        if (window.IsLoaded)
            TryApply();
        else
            window.SourceInitialized += (_, _) => TryApply();
    }
}
