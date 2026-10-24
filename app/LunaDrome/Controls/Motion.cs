using Microsoft.UI.Xaml.Input;
using ItemsRepeater = Microsoft.UI.Xaml.Controls.ItemsRepeater;
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
/// Plus one-shot helpers called from code: FadeIn (pages, optionally rising), Pop (heart),
/// PopIn (play/pause icon swap) and SwapIn (now-playing cover when the song changes).
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

    // The faded-out overlay is Collapsed, not just Opacity 0: on Uno's Skia renderer an Opacity-0
    // element still costs full compositing work every frame, and with ~100 album-card overlays on
    // Home that alone made maximized scrolling drop from 60fps to ~17fps (measured: 58ms -> 16.9ms
    // per frame with overlays collapsed). A collapsed element can't detect the pointer itself, so
    // hover is tracked on its parent (the cover art Grid) and the overlay is shown for the fade-in
    // and collapsed again once the fade-out finishes.
    private static readonly DependencyProperty FadeTargetProperty =
        DependencyProperty.RegisterAttached("FadeTarget", typeof(UIElement), typeof(Motion), new PropertyMetadata(null));

    private static readonly PointerEventHandler HoverFadeEntered = (s, _) =>
    {
        if (((DependencyObject)s).GetValue(FadeTargetProperty) is UIElement overlay)
            FadeOverlay(overlay, show: true);
    };

    private static readonly PointerEventHandler HoverFadeExited = (s, _) =>
    {
        if (((DependencyObject)s).GetValue(FadeTargetProperty) is UIElement overlay)
            FadeOverlay(overlay, show: false);
    };

    private static void OnHoverFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || !(bool)e.NewValue)
            return;

        element.Opacity = 0;
        element.Visibility = Visibility.Collapsed;
        element.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetParent(element) is not UIElement parent || parent.GetValue(FadeTargetProperty) != null)
                return;

            parent.SetValue(FadeTargetProperty, element);
            parent.AddHandler(UIElement.PointerEnteredEvent, HoverFadeEntered, true);
            parent.AddHandler(UIElement.PointerExitedEvent, HoverFadeExited, true);
        };
    }

    private static void FadeOverlay(UIElement overlay, bool show)
    {
        var fade = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = Hover,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, overlay);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);

        if (show)
        {
            overlay.Visibility = Visibility.Visible;
        }
        else
        {
            storyboard.Completed += (_, _) =>
            {
                // Only collapse if nothing re-showed it mid-fade.
                if (overlay.Opacity < 0.01)
                    overlay.Visibility = Visibility.Collapsed;
            };
        }

        storyboard.Begin();
    }

    // --- ClickableText: WPF's ClickableTextStyle (white + underline while hovered) ---

    public static readonly DependencyProperty ClickableTextProperty =
        DependencyProperty.RegisterAttached("ClickableText", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnClickableTextChanged));

    public static bool GetClickableText(DependencyObject d) => (bool)d.GetValue(ClickableTextProperty);
    public static void SetClickableText(DependencyObject d, bool value) => d.SetValue(ClickableTextProperty, value);

    private static readonly DependencyProperty RestForegroundProperty =
        DependencyProperty.RegisterAttached("RestForeground", typeof(Brush), typeof(Motion), new PropertyMetadata(null));

    // Set on a transparent-background Border wrapping the TextBlock, not on the TextBlock itself:
    // on Uno's Skia host a bare TextBlock doesn't raise PointerEntered from plain mouse movement
    // (only on press), so hover never showed. A Border with a (transparent) background is
    // reliably hit-tested, so it detects the hover and restyles its child text.
    private static TextBlock? ClickableTarget(object sender) => sender switch
    {
        Border { Child: TextBlock child } => child,
        TextBlock text => text,
        _ => null
    };

    private static readonly PointerEventHandler ClickableEntered = (s, _) =>
    {
        if (ClickableTarget(s) is not { } text)
            return;
        text.SetValue(RestForegroundProperty, text.Foreground);
        text.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
        text.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    };

    private static readonly PointerEventHandler ClickableExited = (s, _) =>
    {
        if (ClickableTarget(s) is not { } text)
            return;
        if (text.GetValue(RestForegroundProperty) is Brush rest)
            text.Foreground = rest;
        text.TextDecorations = Windows.UI.Text.TextDecorations.None;
    };

    private static void OnClickableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PointerEntered -= ClickableEntered;
        element.PointerExited -= ClickableExited;
        if ((bool)e.NewValue)
        {
            element.PointerEntered += ClickableEntered;
            element.PointerExited += ClickableExited;
            SetHandCursor(element);
        }
    }

    // UIElement.ProtectedCursor is protected (only settable from a subclass), and TextBlock is
    // sealed - reflection is the only way to give a plain TextBlock the hand cursor Feishin's
    // links show. Best-effort: if a platform lacks it, the text is still clickable.
    private static readonly System.Reflection.PropertyInfo? ProtectedCursorProperty =
        typeof(UIElement).GetProperty("ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public static void SetHandCursor(UIElement element)
    {
        try
        {
            ProtectedCursorProperty?.SetValue(element,
                Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand));
        }
        catch { /* cursor is cosmetic */ }
    }

    // --- HoverBackground: swaps a Border's background while hovered (WPF genre tiles) ---

    public static readonly DependencyProperty HoverBackgroundProperty =
        DependencyProperty.RegisterAttached("HoverBackground", typeof(Brush), typeof(Motion),
            new PropertyMetadata(null, OnHoverBackgroundChanged));

    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject d, Brush? value) => d.SetValue(HoverBackgroundProperty, value);

    private static readonly DependencyProperty RestBackgroundProperty =
        DependencyProperty.RegisterAttached("RestBackground", typeof(Brush), typeof(Motion), new PropertyMetadata(null));

    // The colour eases between the two: the Border gets its own brush on first hover (the rest
    // brush is a shared resource - animating that would recolour every tile at once).
    private static readonly PointerEventHandler HoverBackgroundEntered = (s, _) =>
    {
        var border = (Border)s;
        if (border.GetValue(RestBackgroundProperty) == null)
            border.SetValue(RestBackgroundProperty, border.Background);
        if (GetHoverBackground(border) is SolidColorBrush hover)
            AnimateBackground(border, hover.Color);
    };

    private static readonly PointerEventHandler HoverBackgroundExited = (s, _) =>
    {
        var border = (Border)s;
        if (border.GetValue(RestBackgroundProperty) is SolidColorBrush rest)
            AnimateBackground(border, rest.Color);
    };

    private static void AnimateBackground(Border border, Windows.UI.Color to)
    {
        if (border.Background is not SolidColorBrush own || own == border.GetValue(RestBackgroundProperty))
            border.Background = own = new SolidColorBrush((border.Background as SolidColorBrush)?.Color ?? to);

        var color = new ColorAnimation
        {
            To = to,
            Duration = Hover,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(color, own);
        Storyboard.SetTargetProperty(color, nameof(SolidColorBrush.Color));
        var storyboard = new Storyboard();
        storyboard.Children.Add(color);
        storyboard.Begin();
    }

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

    // --- RowHover: hovering a row fades its HoverRole="Show" parts in (the play button) and its
    // HoverRole="Hide" parts out (the track number). Show parts are Collapsed at rest - an
    // Opacity-0 element still costs compositing on Skia, and lists have many rows. ---

    public static readonly DependencyProperty RowHoverProperty =
        DependencyProperty.RegisterAttached("RowHover", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnRowHoverChanged));

    public static bool GetRowHover(DependencyObject d) => (bool)d.GetValue(RowHoverProperty);
    public static void SetRowHover(DependencyObject d, bool value) => d.SetValue(RowHoverProperty, value);

    public static readonly DependencyProperty HoverRoleProperty =
        DependencyProperty.RegisterAttached("HoverRole", typeof(string), typeof(Motion),
            new PropertyMetadata(null, (d, e) =>
            {
                if (d is UIElement element && e.NewValue as string == "Show")
                {
                    element.Opacity = 0;
                    element.Visibility = Visibility.Collapsed;
                }
            }));

    public static string? GetHoverRole(DependencyObject d) => (string?)d.GetValue(HoverRoleProperty);
    public static void SetHoverRole(DependencyObject d, string? value) => d.SetValue(HoverRoleProperty, value);

    private static readonly PointerEventHandler RowEntered = (s, _) => SetRowHovered((DependencyObject)s, true);
    private static readonly PointerEventHandler RowExited = (s, _) => SetRowHovered((DependencyObject)s, false);

    private static void OnRowHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        element.RemoveHandler(UIElement.PointerEnteredEvent, RowEntered);
        element.RemoveHandler(UIElement.PointerExitedEvent, RowExited);
        if ((bool)e.NewValue)
        {
            element.AddHandler(UIElement.PointerEnteredEvent, RowEntered, true);
            element.AddHandler(UIElement.PointerExitedEvent, RowExited, true);
        }
    }

    private static void SetRowHovered(DependencyObject row, bool hovered)
    {
        foreach (var part in HoverParts(row))
        {
            if (GetHoverRole(part) == "Show")
                FadeOverlay(part, show: hovered);
            else
                AnimateOpacity(part, hovered ? 0 : 1);
        }
    }

    private static IEnumerable<UIElement> HoverParts(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement element && GetHoverRole(element) != null)
                yield return element;
            else
                foreach (var part in HoverParts(child))
                    yield return part;
        }
    }

    // --- ThumbGrow (Slider style): the handle grows while the pointer is over the slider ---

    public static readonly DependencyProperty ThumbGrowProperty =
        DependencyProperty.RegisterAttached("ThumbGrow", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnThumbGrowChanged));

    public static bool GetThumbGrow(DependencyObject d) => (bool)d.GetValue(ThumbGrowProperty);
    public static void SetThumbGrow(DependencyObject d, bool value) => d.SetValue(ThumbGrowProperty, value);

    private static readonly PointerEventHandler ThumbEntered = (s, _) => GrowThumb((DependencyObject)s, 1.4);
    private static readonly PointerEventHandler ThumbExited = (s, _) => GrowThumb((DependencyObject)s, 1);

    private static void OnThumbGrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || !(bool)e.NewValue)
            return;
        element.AddHandler(UIElement.PointerEnteredEvent, ThumbEntered, true);
        element.AddHandler(UIElement.PointerExitedEvent, ThumbExited, true);
        element.AddHandler(UIElement.PointerCaptureLostEvent, ThumbExited, true);
    }

    private static void GrowThumb(DependencyObject slider, double scale)
    {
        if (FindNamed(slider, "HorizontalThumb") is UIElement thumb)
            AnimateScale(thumb, scale, Hover);
    }

    private static DependencyObject? FindNamed(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var n } && n == name)
                return child;
            if (FindNamed(child, name) is { } found)
                return found;
        }
        return null;
    }

    // --- EnterStagger: an ItemsRepeater's items fade + rise in one after another when its
    // ItemsSource is (re)set - album grids opening, a new sort. Items prepared later (scrolling)
    // just appear, and only the first 24 animate so a big grid isn't held back. ---

    public static readonly DependencyProperty EnterStaggerProperty =
        DependencyProperty.RegisterAttached("EnterStagger", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnEnterStaggerChanged));

    public static bool GetEnterStagger(DependencyObject d) => (bool)d.GetValue(EnterStaggerProperty);
    public static void SetEnterStagger(DependencyObject d, bool value) => d.SetValue(EnterStaggerProperty, value);

    private sealed class StaggerState
    {
        public long ArmedAt;
        public int Count;
    }

    private static readonly DependencyProperty StaggerStateProperty =
        DependencyProperty.RegisterAttached("StaggerState", typeof(StaggerState), typeof(Motion), new PropertyMetadata(null));

    private static void OnEnterStaggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsRepeater repeater || !(bool)e.NewValue || repeater.GetValue(StaggerStateProperty) != null)
            return;

        var state = new StaggerState { ArmedAt = Environment.TickCount64 };
        repeater.SetValue(StaggerStateProperty, state);
        repeater.RegisterPropertyChangedCallback(ItemsRepeater.ItemsSourceProperty, (_, _) =>
        {
            state.ArmedAt = Environment.TickCount64;
            state.Count = 0;
        });
        repeater.ElementPrepared += (_, args) =>
        {
            if (Environment.TickCount64 - state.ArmedAt > 600 || state.Count >= 24)
                return;
            EnterItem(args.Element, TimeSpan.FromMilliseconds(state.Count++ * 25));
        };
    }

    private static void EnterItem(UIElement element, TimeSpan delay)
    {
        var transform = EnsureTransform(element);
        element.Opacity = 0;
        transform.TranslateY = 14;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = Animate(element, nameof(UIElement.Opacity), 1, TimeSpan.FromMilliseconds(260), ease);
        var rise = Animate(transform, nameof(CompositeTransform.TranslateY), 0, TimeSpan.FromMilliseconds(320), ease);
        fade.BeginTime = rise.BeginTime = delay;

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(rise);
        // Recycled containers come back for other items later: leave plain local values behind,
        // not a held animation.
        storyboard.Completed += (_, _) =>
        {
            element.Opacity = 1;
            transform.TranslateY = 0;
            storyboard.Stop();
        };
        storyboard.Begin();
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

    /// <summary>Page-switch fade, same 0.18s as the WPF app's FadeIn. With <paramref name="rise"/>
    /// the element also slides up that many pixels into place (pages: 12).</summary>
    public static void FadeIn(UIElement element, double rise = 0)
    {
        element.Opacity = 0;
        AnimateOpacity(element, 1, TimeSpan.FromMilliseconds(rise > 0 ? 240 : 180));
        if (rise > 0)
        {
            var transform = EnsureTransform(element);
            transform.TranslateY = rise;
            Run(Animate(transform, nameof(CompositeTransform.TranslateY), 0, TimeSpan.FromMilliseconds(240),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
    }

    /// <summary>Fades in while sliding from (dx, dy) into place - the phone player coming up,
    /// the More menu, the Home header moving to the next album.</summary>
    public static void SlideIn(UIElement element, double dx, double dy, int ms = 280)
    {
        var transform = EnsureTransform(element);
        transform.TranslateX = dx;
        transform.TranslateY = dy;
        element.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Run(Animate(element, nameof(UIElement.Opacity), 1, TimeSpan.FromMilliseconds(ms * 2 / 3), ease),
            Animate(transform, nameof(CompositeTransform.TranslateX), 0, TimeSpan.FromMilliseconds(ms), ease),
            Animate(transform, nameof(CompositeTransform.TranslateY), 0, TimeSpan.FromMilliseconds(ms), ease));
    }

    /// <summary>Slides to (dx, dy) while fading out, then <paramref name="done"/> (which hides it)
    /// and puts the element back in place for next time.</summary>
    public static void SlideOut(UIElement element, double dx, double dy, Action done, int ms = 220)
    {
        var transform = EnsureTransform(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(element, nameof(UIElement.Opacity), 0, TimeSpan.FromMilliseconds(ms), ease));
        storyboard.Children.Add(Animate(transform, nameof(CompositeTransform.TranslateX), dx, TimeSpan.FromMilliseconds(ms), ease));
        storyboard.Children.Add(Animate(transform, nameof(CompositeTransform.TranslateY), dy, TimeSpan.FromMilliseconds(ms), ease));
        storyboard.Completed += (_, _) =>
        {
            done();
            storyboard.Stop();
            element.Opacity = 1;
            transform.TranslateX = transform.TranslateY = 0;
        };
        storyboard.Begin();
    }

    /// <summary>A quick bump: grows to 1.3 and settles back (favorite heart toggled).</summary>
    public static void Pop(UIElement element)
    {
        var transform = EnsureTransform(element);
        Run(Bump(transform, nameof(CompositeTransform.ScaleX)), Bump(transform, nameof(CompositeTransform.ScaleY)));

        static Timeline Bump(CompositeTransform target, string property)
        {
            var bump = new DoubleAnimationUsingKeyFrames();
            bump.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(110), Value = 1.3,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            bump.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(320), Value = 1,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
            });
            Storyboard.SetTarget(bump, target);
            Storyboard.SetTargetProperty(bump, property);
            return bump;
        }
    }

    /// <summary>Grows in from 0.6 with a slight overshoot (play/pause icon swap).</summary>
    public static void PopIn(UIElement element) => ScaleFrom(element, 0.6, 220);

    /// <summary>Fades in from 0.96 scale (now-playing cover when the song changes).</summary>
    public static void SwapIn(UIElement element)
    {
        element.Opacity = 0;
        AnimateOpacity(element, 1, TimeSpan.FromMilliseconds(260));
        ScaleFrom(element, 0.94, 300);
    }

    private static void ScaleFrom(UIElement element, double from, int ms)
    {
        var transform = EnsureTransform(element);
        transform.ScaleX = transform.ScaleY = from;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        Run(Animate(transform, nameof(CompositeTransform.ScaleX), 1, TimeSpan.FromMilliseconds(ms), ease),
            Animate(transform, nameof(CompositeTransform.ScaleY), 1, TimeSpan.FromMilliseconds(ms), ease));
    }

    private static DoubleAnimation Animate(DependencyObject target, string property, double to, TimeSpan duration,
        EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static void Run(params Timeline[] animations)
    {
        var storyboard = new Storyboard();
        foreach (var animation in animations)
            storyboard.Children.Add(animation);
        storyboard.Begin();
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
