namespace LunaDrome.Services;

/// <summary>
/// A short vibration as feedback for a gesture (a swipe that crossed its threshold). Phones only;
/// desktop does nothing. Android uses the view's haptic feedback, which needs no permission and
/// follows the phone's own "touch vibration" setting.
/// </summary>
internal static class Haptics
{
    /// <summary>A light tick: the gesture will do something if you let go now.</summary>
    public static void Tick()
    {
#if ANDROID
        Perform(Android.Views.FeedbackConstants.ClockTick);
#elif __IOS__
        try
        {
            using var generator = new UIKit.UISelectionFeedbackGenerator();
            generator.SelectionChanged();
        }
        catch { /* feedback is a nicety */ }
#endif
    }

    /// <summary>A firmer bump: the action happened (song queued).</summary>
    public static void Confirm()
    {
#if ANDROID
        Perform(OperatingSystem.IsAndroidVersionAtLeast(30) ? Android.Views.FeedbackConstants.Confirm : Android.Views.FeedbackConstants.LongPress);
#elif __IOS__
        try
        {
            using var generator = new UIKit.UIImpactFeedbackGenerator(UIKit.UIImpactFeedbackStyle.Medium);
            generator.ImpactOccurred();
        }
        catch { /* feedback is a nicety */ }
#endif
    }

#if ANDROID
    private static void Perform(Android.Views.FeedbackConstants feedback)
    {
        try
        {
            (Uno.UI.ContextHelper.Current as Android.App.Activity)?.Window?.DecorView?.PerformHapticFeedback(feedback);
        }
        catch { /* feedback is a nicety */ }
    }
#endif
}
