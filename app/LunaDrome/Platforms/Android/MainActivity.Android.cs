using Android.App;
using Android.Views;

namespace LunaDrome.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    /// <summary>Set by MainPage: true when a menu is open (Back should close it).</summary>
    internal static Func<bool>? MenuOpen;
    /// <summary>Set by MainPage: closes the open menus.</summary>
    internal static Action? CloseMenus;

    private bool _backClosesMenu;

    // With a menu (flyout) open, Back never reaches SystemNavigationManager.BackRequested (seen in
    // the emulator test), so the menu stayed open. Catch the key here and close the menu instead.
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.KeyCode == Keycode.Back)
        {
            LunaDrome.Controls.SwipeToQueue.Trace($"ACTIVITY BACK {e.Action} menu={MenuOpen?.Invoke()}");
            if (e.Action == KeyEventActions.Down && MenuOpen?.Invoke() == true)
            {
                _backClosesMenu = true;
                return true;
            }
            if (e.Action == KeyEventActions.Up && _backClosesMenu)
            {
                _backClosesMenu = false;
                CloseMenus?.Invoke();
                return true;
            }
        }
        return base.DispatchKeyEvent(e);
    }

    // Closed for real (not just sent to the background): delete the prebuffered tracks, unless
    // the media service keeps playing (it deletes them when it's swiped away while paused).
    protected override void OnDestroy()
    {
        if (IsFinishing && !LunaDrome.Services.LunaMediaService.IsPlaying)
            LunaDrome.Services.TrackPrefetcher.DeleteCacheOnExit();
        base.OnDestroy();
    }
}
