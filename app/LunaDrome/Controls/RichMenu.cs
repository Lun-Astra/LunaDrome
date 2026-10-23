using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace LunaDrome.Controls;

/// <summary>One row of a <see cref="RichMenu"/>: a stroked 16x16 icon path, a label and an
/// optional hint on the right. Null = a separator line.</summary>
public sealed record RichMenuItem(string Text, string Icon, Action OnClick, string? Hint = null, bool Filled = false);

/// <summary>
/// A context menu with more to it than MenuFlyout: an optional header (cover, title, subtitle),
/// icon rows that fade/nudge on hover and dip when pressed, and an entrance - the card grows from
/// where it was opened while the rows rise in one after another.
/// </summary>
public static class RichMenu
{
    public static void Show(FrameworkElement anchor, Point at, string? coverUrl, string? title, string? subtitle,
        IEnumerable<RichMenuItem?> items)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            FlyoutPresenterStyle = PresenterStyle.Value,
            ShouldConstrainToRootBounds = true,
        };

        var rows = new StackPanel { Spacing = 1 };
        var animated = new List<UIElement>();

        if (title != null)
        {
            var header = Header(coverUrl, title, subtitle);
            rows.Children.Add(header);
            animated.Add(header);
            rows.Children.Add(Separator());
        }

        foreach (var item in items)
        {
            if (item == null)
            {
                rows.Children.Add(Separator());
                continue;
            }
            var row = Row(item, () =>
            {
                flyout.Hide();
                item.OnClick();
            });
            rows.Children.Add(row);
            animated.Add(row);
        }

        var card = new Border
        {
            Width = 280,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = Res("RaisedBrush"),
            BorderBrush = Res("BorderBrush2"),
            BorderThickness = new Thickness(1),
            Child = rows,
        };
        flyout.Content = card;
        flyout.Opened += (_, _) => Enter(card, animated);

        // The anchor's tooltip would pop up over the menu (the pointer is still on it).
        var tooltip = ToolTipService.GetToolTip(anchor);
        if (tooltip != null)
        {
            ToolTipService.SetToolTip(anchor, null);
            flyout.Closed += (_, _) => ToolTipService.SetToolTip(anchor, tooltip);
        }
        flyout.ShowAt(anchor, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.TopEdgeAlignedLeft });
    }

    // The presenter's own chrome (padding, background, border) is replaced by the card above.
    private static readonly Lazy<Style> PresenterStyle = new(() =>
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 400.0));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        return style;
    });

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Grid Header(string? coverUrl, string title, string? subtitle)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 8, 8, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cover = new Grid { Width = 48, Height = 48 };
        cover.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Res("BorderBrush2") });
        if (!string.IsNullOrEmpty(coverUrl))
            cover.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverUrl)), Stretch = Stretch.UniformToFill }
            });
        grid.Children.Add(cover);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static Border Separator() => new()
    {
        Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = Res("BorderBrush2"), IsHitTestVisible = false
    };

    private static Border Row(RichMenuItem item, Action click)
    {
        var icon = new Path
        {
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), item.Icon),
            Stroke = Res("TextPrimaryBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Fill = item.Filled ? Res("TextPrimaryBrush") : null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconBox = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Background = Res("PanelBrush"), Child = icon,
        };

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 5, 10, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(iconBox);
        var label = new TextBlock
        {
            Text = item.Text, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (item.Hint != null)
        {
            var hint = new TextBlock
            {
                Text = item.Hint, FontSize = 11, Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = grid,
        };
        Motion.SetHoverBackground(row, Res("HoverBrush"));
        Motion.SetPressScale(row, true);
        Motion.SetHandCursor(row);

        // Hover: the icon tile lights up and the icon nudges right.
        var iconShift = new TranslateTransform();
        icon.RenderTransform = iconShift;
        row.PointerEntered += (_, _) =>
        {
            iconBox.Background = Res("AccentBrush");
            icon.Stroke = Res("OnAccentBrush");
            if (item.Filled)
                icon.Fill = Res("OnAccentBrush");
            Slide(iconShift, 2);
        };
        row.PointerExited += (_, _) =>
        {
            iconBox.Background = Res("PanelBrush");
            icon.Stroke = Res("TextPrimaryBrush");
            if (item.Filled)
                icon.Fill = Res("TextPrimaryBrush");
            Slide(iconShift, 0);
        };
        row.Tapped += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return row;
    }

    private static void Slide(TranslateTransform transform, double to)
    {
        var slide = new DoubleAnimation
        {
            To = to, Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));
        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    // The card grows from its bottom-left corner (it opens above the click) while fading in; the rows
    // follow 30ms apart, each rising 8px.
    private static void Enter(Border card, List<UIElement> rows)
    {
        var storyboard = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var grow = new CompositeTransform { ScaleX = 0.9, ScaleY = 0.9 };
        card.RenderTransform = grow;
        card.RenderTransformOrigin = new Point(0, 1);
        card.Opacity = 0;
        Add(storyboard, card, nameof(UIElement.Opacity), 1, 160, 0, ease);
        Add(storyboard, grow, nameof(CompositeTransform.ScaleX), 1, 220, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        Add(storyboard, grow, nameof(CompositeTransform.ScaleY), 1, 220, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });

        for (var i = 0; i < rows.Count; i++)
        {
            // CompositeTransform: Motion.PressScale reuses it for the press dip.
            var rise = new CompositeTransform { TranslateY = 8 };
            rows[i].RenderTransform = rise;
            rows[i].RenderTransformOrigin = new Point(0.5, 0.5);
            rows[i].Opacity = 0;
            Add(storyboard, rows[i], nameof(UIElement.Opacity), 1, 200, 40 + i * 30, ease);
            Add(storyboard, rise, nameof(CompositeTransform.TranslateY), 0, 240, 40 + i * 30, ease);
        }
        storyboard.Begin();
    }

    private static void Add(Storyboard storyboard, DependencyObject target, string property, double to, int ms, int delay,
        EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation
        {
            To = to, Duration = TimeSpan.FromMilliseconds(ms), BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
