using System.Windows;

namespace Musicplayer.App.Services;

/// <summary>
/// WPF's WindowStartupLocation="CenterScreen" centers a window on whichever monitor
/// contains the mouse cursor at Show() time, not the primary monitor - on a multi-monitor
/// machine this can silently put the window on a display the user isn't looking at.
/// SystemParameters.WorkArea always refers to the primary monitor's work area regardless
/// of cursor position, so centering against it pins the window there instead.
/// </summary>
public static class WindowPlacement
{
    public static void CenterOnPrimaryScreen(Window window)
    {
        void Center()
        {
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + (area.Width - window.ActualWidth) / 2;
            window.Top = area.Top + (area.Height - window.ActualHeight) / 2;
        }

        // ActualWidth/ActualHeight aren't final until layout has run (matters for
        // SizeToContent windows like LoginWindow) - Loaded fires after that layout
        // pass but before the window is first painted, so there's no visible jump.
        if (window.IsLoaded)
            Center();
        else
            window.Loaded += (_, _) => Center();
    }
}
