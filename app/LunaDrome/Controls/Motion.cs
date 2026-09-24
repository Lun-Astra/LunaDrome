using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace LunaDrome.Controls;

/// <summary>
/// Hover/press micro-animations, ported from the WPF app's storyboard triggers. WinUI styles
/// have no property triggers, so these are attached properties that wire pointer events to
/// short storyboards - settable from a Style (e.g. the implicit Button style) or per element.
///
///   PressScale - shrinks to 0.94 while pressed (WPF's tactile button feedback).
///   HoverScale - grows to the given scale while hovered (album cards: 1.035).
///   HoverFade  - fades 0 -> 1 while hovered (album cards' play overlay).
/// </summary>
public static class Motion
{
    private static readonly TimeSpan PressIn = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan PressOut = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(150);

    // --- PressScale ---

    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached("PressScale", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnPressScaleChanged));

    public static bool GetPressScale(DependencyObject d) => (bool)d.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject d, bool value) => d.SetValue(PressScaleProperty, value);

    private static readonly PointerEventHandler PressedHandler = (s, _) => AnimateScale((UIElement)s, 0.94, PressIn);
    private static readonly PointerEventHandler ReleasedHandler = (s, _) => AnimateScale((UIElement)s, 1, PressOut);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        // handledEventsToo: Button marks pointer presses handled itself (to raise Click), so a
        // plain += subscription would never see them.
        if ((bool)e.NewValue)
        {
            element.AddHandler(UIElement.PointerPressedEvent, PressedHandler, true);
            element.AddHandler(UIElement.PointerReleasedEvent, ReleasedHandler, true);
            element.AddHandler(UIElement.PointerCaptureLostEvent, ReleasedHandler, true);
            element.AddHandler(UIElement.PointerExitedEvent, ReleasedHandler, true);
        }
        else
        {
            element.RemoveHandler(UIElement.PointerPressedEvent, PressedHandler);
            element.RemoveHandler(UIElement.PointerReleasedEvent, ReleasedHandler);
            element.RemoveHandler(UIElement.PointerCaptureLostEvent, ReleasedHandler);
            element.RemoveHandler(UIElement.PointerExitedEvent, ReleasedHandler);
        }
    }

    // --- HoverScale ---

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(Motion),
            new PropertyMetadata(1.0, OnHoverScaleChanged));

    public static double GetHoverScale(DependencyObject d) => (double)d.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject d, double value) => d.SetValue(HoverScaleProperty, value);

    private static readonly PointerEventHandler HoverScaleEntered = (s, _) =>
        AnimateScale((UIElement)s, GetHoverScale((DependencyObject)s), Hover);
    private static readonly PointerEventHandler HoverScaleExited = (s, _) => AnimateScale((UIElement)s, 1, Hover);

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.RemoveHandler(UIElement.PointerEnteredEvent, HoverScaleEntered);
        element.RemoveHandler(UIElement.PointerExitedEvent, HoverScaleExited);

        if ((double)e.NewValue != 1.0)
        {
            element.AddHandler(UIElement.PointerEnteredEvent, HoverScaleEntered, true);
            element.AddHandler(UIElement.PointerExitedEvent, HoverScaleExited, true);
        }
    }

    // --- HoverFade ---

    public static readonly DependencyProperty HoverFadeProperty =
        DependencyProperty.RegisterAttached("HoverFade", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnHoverFadeChanged));

    public static bool GetHoverFade(DependencyObject d) => (bool)d.GetValue(HoverFadeProperty);
    public static void SetHoverFade(DependencyObject d, bool value) => d.SetValue(HoverFadeProperty, value);

    private static readonly PointerEventHandler HoverFadeEntered = (s, _) => AnimateOpacity((UIElement)s, 1);
    private static readonly PointerEventHandler HoverFadeExited = (s, _) => AnimateOpacity((UIElement)s, 0);

    private static void OnHoverFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.RemoveHandler(UIElement.PointerEnteredEvent, HoverFadeEntered);
        element.RemoveHandler(UIElement.PointerExitedEvent, HoverFadeExited);

        if ((bool)e.NewValue)
        {
            element.Opacity = 0;
            element.AddHandler(UIElement.PointerEnteredEvent, HoverFadeEntered, true);
            element.AddHandler(UIElement.PointerExitedEvent, HoverFadeExited, true);
        }
    }

    // --- ClickableText: WPF's ClickableTextStyle (white + underline while hovered) ---

    public static readonly DependencyProperty ClickableTextProperty =
        DependencyProperty.RegisterAttached("ClickableText", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnClickableTextChanged));

    public static bool GetClickableText(DependencyObject d) => (bool)d.GetValue(ClickableTextProperty);
    public static void SetClickableText(DependencyObject d, bool value) => d.SetValue(ClickableTextProperty, value);

    private static readonly DependencyProperty RestForegroundProperty =
        DependencyProperty.RegisterAttached("RestForeground", typeof(Brush), typeof(Motion), new PropertyMetadata(null));

    private static readonly PointerEventHandler ClickableEntered = (s, _) =>
    {
        var text = (TextBlock)s;
        text.SetValue(RestForegroundProperty, text.Foreground);
        text.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
        text.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    };

    private static readonly PointerEventHandler ClickableExited = (s, _) =>
    {
        var text = (TextBlock)s;
        if (text.GetValue(RestForegroundProperty) is Brush rest)
            text.Foreground = rest;
        text.TextDecorations = Windows.UI.Text.TextDecorations.None;
    };

    private static void OnClickableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
            return;

        text.PointerEntered -= ClickableEntered;
        text.PointerExited -= ClickableExited;
        if ((bool)e.NewValue)
        {
            text.PointerEntered += ClickableEntered;
            text.PointerExited += ClickableExited;
        }
    }

    // --- HoverBackground: swaps a Border's background while hovered (WPF genre tiles) ---

    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached("HoverBackground", typeof(Brush), typeof(Motion),
            new PropertyMetadata(null, OnHoverBackgroundChanged));

    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject d, Brush? value) => d.SetValue(HoverBackgroundProperty, value);

    private static readonly DependencyProperty RestBackgroundProperty =
        DependencyProperty.RegisterAttached("RestBackground", typeof(Brush), typeof(Motion), new PropertyMetadata(null));

    private static readonly PointerEventHandler HoverBackgroundEntered = (s, _) =>
    {
        var border = (Border)s;
        border.SetValue(RestBackgroundProperty, border.Background);
        border.Background = GetHoverBackground(border);
    };

    private static readonly PointerEventHandler HoverBackgroundExited = (s, _) =>
    {
        var border = (Border)s;
        if (border.GetValue(RestBackgroundProperty) is Brush rest)
            border.Background = rest;
    };

    private static void OnHoverBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        border.PointerEntered -= HoverBackgroundEntered;
        border.PointerExited -= HoverBackgroundExited;
        if (e.NewValue != null)
        {
            border.PointerEntered += HoverBackgroundEntered;
            border.PointerExited += HoverBackgroundExited;
        }
    }

    // --- Animation plumbing ---

    private static CompositeTransform EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is CompositeTransform existing)
            return existing;

        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return transform;
    }

    private static void AnimateScale(UIElement element, double to, TimeSpan duration)
    {
        var transform = EnsureTransform(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var sx = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(sx, transform);
        Storyboard.SetTargetProperty(sx, nameof(CompositeTransform.ScaleX));

        var sy = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(sy, transform);
        Storyboard.SetTargetProperty(sy, nameof(CompositeTransform.ScaleY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(sx);
        storyboard.Children.Add(sy);
        storyboard.Begin();
    }

    /// <summary>Page-switch fade, same 0.18s as the WPF app's FadeIn.</summary>
    public static void FadeIn(UIElement element)
    {
        element.Opacity = 0;
        AnimateOpacity(element, 1, TimeSpan.FromMilliseconds(180));
    }

    private static void AnimateOpacity(UIElement element, double to) => AnimateOpacity(element, to, Hover);

    private static void AnimateOpacity(UIElement element, double to, TimeSpan duration)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
