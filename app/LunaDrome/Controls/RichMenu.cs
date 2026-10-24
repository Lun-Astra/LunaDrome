using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LunaDrome.Controls;

/// <summary>One row of a <see cref="RichMenu"/> built from code: a stroked 16x16 icon path, a
/// label and an optional hint on the right. Null = a separator line.</summary>
public sealed record RichMenuItem(string Text, string Icon, Action OnClick, string? Hint = null, bool Filled = false);

/// <summary>
/// Every popup menu of the app, drawn as a card instead of the plain MenuFlyout: an optional
/// header (cover, title, subtitle), icon rows that light up on hover and dip when pressed,
/// check marks for choices, sub-menus that open in place, and an entrance - the card grows in
/// while the rows rise one after another.
///
/// Menus are still put together as a MenuFlyout (items, separators, toggles, sub-items), so the
/// code that builds them stays as it was; <see cref="ShowAt"/> draws that. A MenuFlyoutItem's
/// Click can't be raised from outside, so menu actions are registered with <see cref="OnClick"/>
/// (which also hooks Click). A disabled item without an action is a label: the first one is the
/// menu's title ("Play on"), later ones are notes ("Looking for speakers…").
/// </summary>
public static class RichMenu
{
    private static readonly ConditionalWeakTable<MenuFlyoutItemBase, List<Action>> Actions = new();
    private static readonly ConditionalWeakTable<MenuFlyoutItemBase, string> Icons = new();
    private static readonly ConditionalWeakTable<MenuFlyoutItemBase, string> Hints = new();
    private static readonly ConditionalWeakTable<MenuFlyoutItemBase, object> Filled = new();
    private static readonly ConditionalWeakTable<MenuFlyout, Header> Headers = new();

    private sealed record Header(string? CoverUrl, string Title, string? Subtitle);

    /// <summary>Runs <paramref name="action"/> when the item is picked (several can be added).</summary>
    public static T OnClick<T>(T item, Action action) where T : MenuFlyoutItem
    {
        Actions.GetOrCreateValue(item).Add(action);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An explicit icon (16x16 path data) instead of the one guessed from the text.</summary>
    public static T SetIcon<T>(T item, string pathData) where T : MenuFlyoutItemBase
    {
        Icons.AddOrUpdate(item, pathData);
        return item;
    }

    public static void SetHint(MenuFlyoutItemBase item, string hint) => Hints.AddOrUpdate(item, hint);

    /// <summary>A cover + title header on top of the menu (songs, albums, playlists).</summary>
    public static void SetHeader(MenuFlyout menu, string? coverUrl, string title, string? subtitle = null) =>
        Headers.AddOrUpdate(menu, new Header(coverUrl, title, subtitle));

    /// <summary>The code-built form (the now-playing title menu).</summary>
    public static void Show(FrameworkElement anchor, Point at, string? coverUrl, string? title, string? subtitle,
        IEnumerable<RichMenuItem?> items)
    {
        var menu = new MenuFlyout();
        foreach (var entry in items)
        {
            if (entry == null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }
            var item = OnClick(new MenuFlyoutItem { Text = entry.Text }, entry.OnClick);
            SetIcon(item, entry.Icon);
            if (entry.Hint != null)
                SetHint(item, entry.Hint);
            if (entry.Filled)
                Filled.AddOrUpdate(item, true);
            menu.Items.Add(item);
        }
        if (title != null)
            SetHeader(menu, coverUrl, title, subtitle);
        ShowAt(menu, anchor, at);
    }

    /// <summary>Shows <paramref name="menu"/> at the pointer (or by the anchor). The handle
    /// redraws it when the menu's items change while it's open (the Play on menu after a scan).</summary>
    public static Handle ShowAt(MenuFlyout menu, FrameworkElement anchor, Point? at = null)
    {
        var handle = new Handle(menu, anchor);
        handle.Open(at);
        return handle;
    }

    public sealed class Handle
    {
        private readonly MenuFlyout _menu;
        private readonly FrameworkElement _anchor;
        private readonly Flyout _flyout;
        private readonly StackPanel _rows = new() { Spacing = 1 };
        private readonly Border _card;

        internal Handle(MenuFlyout menu, FrameworkElement anchor)
        {
            _menu = menu;
            _anchor = anchor;
            var root = anchor.XamlRoot?.Size ?? new Size(1200, 800);
            var phone = root.Width < 600;
            _card = new Border
            {
                Width = phone ? Math.Min(360, root.Width - 24) : 290,
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(14),
                Background = Res("RaisedBrush"),
                BorderBrush = Res("BorderBrush2"),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = _rows,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            };
            _flyout = new Flyout
            {
                FlyoutPresenterStyle = PresenterStyle.Value,
                ShouldConstrainToRootBounds = true,
                Content = _card,
            };

            // The anchor's tooltip would pop up over the menu (the pointer is still on it).
            var tooltip = ToolTipService.GetToolTip(anchor);
            if (tooltip != null)
            {
                ToolTipService.SetToolTip(anchor, null);
                _flyout.Closed += (_, _) => ToolTipService.SetToolTip(anchor, tooltip);
            }
        }

        public bool IsOpen { get; private set; }

        internal void Open(Point? at)
        {
            // Open towards the side with more room (below a click near the top, above one near
            // the bottom) and never taller than that room, so nothing ends up off screen.
            var root = _anchor.XamlRoot?.Size ?? new Size(1200, 800);
            var origin = _anchor.TransformToVisual(null).TransformPoint(at ?? new Point(0, 0));
            var anchorBottom = origin.Y + (at == null ? _anchor.ActualHeight : 0);
            var below = root.Height - anchorBottom;
            var above = origin.Y;
            var down = below >= above;
            ((ScrollViewer)_card.Child).MaxHeight = Math.Max(160, Math.Min(root.Height * 0.8, (down ? below : above) - 28));
            var placement = down ? FlyoutPlacementMode.BottomEdgeAlignedLeft : FlyoutPlacementMode.TopEdgeAlignedLeft;

            var animated = Build();
            _flyout.Opened += (_, _) =>
            {
                IsOpen = true;
                Enter(_card, animated);
            };
            _flyout.Closed += (_, _) => IsOpen = false;
            if (at is { } point)
                _flyout.ShowAt(_anchor, new FlyoutShowOptions { Position = point, Placement = placement });
            else
                _flyout.ShowAt(_anchor, new FlyoutShowOptions { Placement = placement });
        }

        /// <summary>The menu's items changed: draw them again (no entrance this time).</summary>
        public void Refresh()
        {
            if (IsOpen)
                Build();
        }

        public void Hide() => _flyout.Hide();

        private List<UIElement> Build()
        {
            _rows.Children.Clear();
            var animated = new List<UIElement>();
            if (Headers.TryGetValue(_menu, out var header))
            {
                var h = HeaderRow(header);
                _rows.Children.Add(h);
                animated.Add(h);
                _rows.Children.Add(Separator());
            }
            AddItems(_rows, _menu.Items, animated, nested: false);
            // No separator at either end or two in a row (sections that came out empty).
            for (var i = _rows.Children.Count - 1; i >= 0; i--)
            {
                if (IsSeparator(_rows.Children[i])
                    && (i == _rows.Children.Count - 1 || i == 0 || IsSeparator(_rows.Children[i - 1])))
                    _rows.Children.RemoveAt(i);
            }
            return animated;
        }

        private static bool IsSeparator(UIElement element) => element is Border { Tag: "separator" };

        private void AddItems(Panel target, IList<MenuFlyoutItemBase> items, List<UIElement> animated, bool nested)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var row = BuildRow(items[i], first: i == 0 && !nested, nested);
                if (row == null)
                    continue;
                target.Children.Add(row);
                if (!IsSeparator(row))
                    animated.Add(row);
            }
        }

        private UIElement? BuildRow(MenuFlyoutItemBase item, bool first, bool nested)
        {
            switch (item)
            {
                case MenuFlyoutSeparator:
                    return Separator();
                case MenuFlyoutSubItem sub:
                    return SubRow(sub);
                case MenuFlyoutItem entry:
                    var hasAction = Actions.TryGetValue(entry, out var actions) && actions.Count > 0;
                    if (!hasAction && !entry.IsEnabled)
                        return first ? Title(entry.Text) : Note(entry.Text);
                    return ItemRow(entry, actions ?? new List<Action>(), nested);
                default:
                    return null;
            }
        }

        private Border ItemRow(MenuFlyoutItem item, List<Action> actions, bool nested)
        {
            var indent = item.Padding.Left > 20;   // e.g. the sound cards under "This computer"
            // A choice list (Sort by) reads best as plain text + check; speakers etc. set an icon.
            var row = Row(
                Icons.TryGetValue(item, out var icon) ? icon : item is ToggleMenuFlyoutItem ? null : GuessIcon(item.Text),
                item.Text,
                ToolTipService.GetToolTip(item) as string,
                Hints.TryGetValue(item, out var hint) ? hint : null,
                (item as ToggleMenuFlyoutItem)?.IsChecked,
                Filled.TryGetValue(item, out _),
                small: nested || indent);
            if (indent)
                row.Margin = new Thickness(26, 0, 0, 0);
            if (!item.IsEnabled)
            {
                row.Opacity = 0.45;
                row.IsHitTestVisible = false;
                return row;
            }
            row.Tapped += (_, e) =>
            {
                e.Handled = true;
                _flyout.Hide();
                foreach (var action in actions.ToList())
                    action();
            };
            return row;
        }

        // A sub-menu opens in place, under its row (on a phone a second popup beside it has no room).
        private UIElement SubRow(MenuFlyoutSubItem sub)
        {
            var panel = new StackPanel();
            var row = Row(Icons.TryGetValue(sub, out var icon) ? icon : GuessIcon(sub.Text), sub.Text, null, null, null, false,
                small: false, chevron: true);
            var children = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(18, 2, 0, 4),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(10),
                Background = Res("PanelBrush"),
            };
            panel.Children.Add(row);
            panel.Children.Add(children);
            var built = false;
            row.Tapped += (_, e) =>
            {
                e.Handled = true;
                var open = children.Visibility != Visibility.Visible;
                if (open && !built)
                {
                    built = true;
                    AddItems(children, sub.Items, new List<UIElement>(), nested: true);
                }
                children.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                if (row.Tag is RotateTransform chevron)
                    Rotate(chevron, open ? 90 : 0);
                if (open)
                    Motion.SlideIn(children, 0, -6, 220);
            };
            return panel;
        }
    }

    // ---------- building blocks ----------

    // The presenter's own chrome (padding, background, border) is replaced by the card.
    private static readonly Lazy<Style> PresenterStyle = new(() =>
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        return style;
    });

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Geometry ToGeometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    private static Grid HeaderRow(Header header)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 8, 8, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cover = new Grid { Width = 48, Height = 48 };
        cover.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Res("BorderBrush2") });
        if (!string.IsNullOrEmpty(header.CoverUrl))
            cover.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new ImageBrush
                {
                    ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(header.CoverUrl)), Stretch = Stretch.UniformToFill
                }
            });
        else
            cover.Children.Add(new Path
            {
                Width = 20, Height = 20, Stretch = Stretch.Uniform, Stroke = Res("TextSecondaryBrush"), StrokeThickness = 1.6,
                Data = ToGeometry(MenuIcons.Note), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        grid.Children.Add(cover);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = header.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(header.Subtitle))
            text.Children.Add(new TextBlock
            {
                Text = header.Subtitle, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock Title(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 60,
        Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(10, 8, 10, 6)
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Res("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(10, 6, 10, 6)
    };

    private static Border Separator() => new()
    {
        Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = Res("BorderBrush2"), IsHitTestVisible = false, Tag = "separator"
    };

    // A row: icon tile · label (+ a second line) · hint / check / chevron.
    private static Border Row(string? icon, string text, string? detail, string? hint, bool? isChecked, bool filled, bool small,
        bool chevron = false)
    {
        var on = isChecked == true;
        var glyph = new Path
        {
            Width = small ? 14 : 16, Height = small ? 14 : 16, Stretch = Stretch.Uniform, Data = ToGeometry(icon ?? MenuIcons.Sort),
            Stroke = Res(on ? "OnAccentBrush" : "TextPrimaryBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Fill = filled ? Res(on ? "OnAccentBrush" : "TextPrimaryBrush") : null,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var tile = new Border
        {
            Width = small ? 28 : 32, Height = small ? 28 : 32, CornerRadius = new CornerRadius(8),
            Background = Res(on ? "AccentBrush" : "PanelBrush"), Child = glyph,
            Visibility = icon == null ? Visibility.Collapsed : Visibility.Visible,
        };

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 5, 10, 5), MinHeight = small ? 38 : 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(tile);

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = text, FontSize = small ? 12.5 : 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(detail))
            label.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 11, Margin = new Thickness(0, 1, 0, 0),
                Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        RotateTransform? chevronTurn = null;
        FrameworkElement? right = null;
        if (isChecked != null)
            right = Check(on);
        else if (chevron)
        {
            chevronTurn = new RotateTransform();
            right = new Path
            {
                Width = 7, Height = 12, Stretch = Stretch.Uniform, Data = ToGeometry("M1,1 L6,6 L1,11"),
                Stroke = Res("TextSecondaryBrush"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = chevronTurn, RenderTransformOrigin = new Point(0.5, 0.5), Margin = new Thickness(0, 0, 4, 0),
            };
        }
        else if (hint != null)
            right = new TextBlock { Text = hint, FontSize = 11, Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        if (right != null)
        {
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = grid,
            Tag = chevronTurn,
        };
        Motion.SetHoverBackground(row, Res("HoverBrush"));
        Motion.SetPressScale(row, true);
        Motion.SetHandCursor(row);

        // Mouse hover: the icon tile lights up and the icon nudges right (not for touch - a
        // finger has no "leave" to undo it).
        var shift = new TranslateTransform();
        glyph.RenderTransform = shift;
        row.PointerEntered += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch || on)
                return;
            tile.Background = Res("AccentBrush");
            glyph.Stroke = Res("OnAccentBrush");
            if (filled) glyph.Fill = Res("OnAccentBrush");
            Slide(shift, 2);
        };
        row.PointerExited += (_, _) =>
        {
            if (on)
                return;
            tile.Background = Res("PanelBrush");
            glyph.Stroke = Res("TextPrimaryBrush");
            if (filled) glyph.Fill = Res("TextPrimaryBrush");
            Slide(shift, 0);
        };
        return row;
    }

    private static Border Check(bool on)
    {
        var mark = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Background = on ? Res("AccentBrush") : null,
            BorderBrush = Res(on ? "AccentBrush" : "BorderBrush2"), BorderThickness = new Thickness(on ? 0 : 1.5),
        };
        if (on)
            mark.Child = new Path
            {
                Width = 10, Height = 8, Stretch = Stretch.Uniform, Data = ToGeometry("M1,4.5 L4,7.5 L10,1"),
                Stroke = Res("OnAccentBrush"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return mark;
    }

    /// <summary>An icon for the common menu texts, so every menu gets them without listing each.</summary>
    private static string GuessIcon(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("radio")) return MenuIcons.Radio;
        if (t.Contains("favorite")) return MenuIcons.Heart;
        if (t.StartsWith("remove") || t.StartsWith("delete") || t.Contains("turn off")) return MenuIcons.Trash;
        if (t.Contains("shuffle")) return MenuIcons.Shuffle;
        if (t.Contains("next")) return MenuIcons.Next;
        if (t.Contains("queue") || t.Contains("last")) return MenuIcons.Queue;
        if (t.Contains("playlist")) return MenuIcons.ListPlus;
        if (t.StartsWith("play")) return MenuIcons.Play;
        if (t.StartsWith("move") || t.StartsWith("to the") || t is "up" or "down") return MenuIcons.Move;
        if (t.Contains("album")) return MenuIcons.Album;
        if (t.Contains("artist")) return MenuIcons.Artist;
        if (t.Contains("share")) return MenuIcons.Share;
        if (t.Contains("rename")) return MenuIcons.Pencil;
        if (t.Contains("jam")) return MenuIcons.People;
        if (t.Contains("minutes") || t.Contains("end of this song") || t.Contains("sleep")) return MenuIcons.Moon;
        if (t.Contains("select all")) return MenuIcons.CheckAll;
        if (t.Contains("speaker") || t.Contains("address")) return MenuIcons.Plus;
        return MenuIcons.Note;   // e.g. a playlist's name
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

    private static void Rotate(RotateTransform transform, double to)
    {
        var turn = new DoubleAnimation
        {
            To = to, Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(turn, transform);
        Storyboard.SetTargetProperty(turn, nameof(RotateTransform.Angle));
        var storyboard = new Storyboard();
        storyboard.Children.Add(turn);
        storyboard.Begin();
    }

    // The card grows in while fading; the rows follow 25ms apart, each rising 8px.
    private static void Enter(Border card, List<UIElement> rows)
    {
        var storyboard = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var grow = new CompositeTransform { ScaleX = 0.92, ScaleY = 0.92 };
        card.RenderTransform = grow;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.Opacity = 0;
        Add(storyboard, card, nameof(UIElement.Opacity), 1, 150, 0, ease);
        Add(storyboard, grow, nameof(CompositeTransform.ScaleX), 1, 220, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        Add(storyboard, grow, nameof(CompositeTransform.ScaleY), 1, 220, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });

        for (var i = 0; i < rows.Count && i < 14; i++)
        {
            // CompositeTransform: Motion.PressScale reuses it for the press dip.
            var rise = new CompositeTransform { TranslateY = 8 };
            rows[i].RenderTransform = rise;
            rows[i].RenderTransformOrigin = new Point(0.5, 0.5);
            rows[i].Opacity = 0;
            Add(storyboard, rows[i], nameof(UIElement.Opacity), 1, 200, 30 + i * 25, ease);
            Add(storyboard, rise, nameof(CompositeTransform.TranslateY), 0, 240, 30 + i * 25, ease);
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

/// <summary>16x16 stroked icon paths for menus.</summary>
public static class MenuIcons
{
    public const string Play = "M4,2 L13,8 L4,14 Z";
    public const string Shuffle = "M1,4 L4,4 C8,4 8,12 12,12 L15,12 M12,9.5 L15,12 L12,14.5 M1,12 L4,12 C5.5,12 6.5,10.8 7.3,9.4 M9,6 C9.8,4.7 10.7,4 12,4 L15,4 M12,1.5 L15,4 L12,6.5";
    public const string Next = "M2,14 L2,10 C2,7 4,5.5 7,5.5 L14,5.5 M10.5,2 L14,5.5 L10.5,9";
    public const string Queue = "M1,3 L11,3 M1,8 L11,8 M1,13 L7,13 M12,10 L12,16 M9,13 L15,13";
    public const string ListPlus = "M1,3 L10,3 M1,7 L10,7 M1,11 L6,11 M12,8 L12,15 M8.5,11.5 L15.5,11.5";
    public const string Radio = "M8,6.5 A1.5,1.5 0 1,1 7.99,6.5 M4.5,4 A5,5 0 0,0 4.5,12 M11.5,4 A5,5 0 0,1 11.5,12 M2,2 A8,8 0 0,0 2,14 M14,2 A8,8 0 0,1 14,14";
    public const string Heart = "M8,14.5 C8,14.5 1,10 1,5.5 C1,3 3,1.3 5.2,1.3 C6.7,1.3 7.6,2.1 8,2.7 C8.4,2.1 9.3,1.3 10.8,1.3 C13,1.3 15,3 15,5.5 C15,10 8,14.5 8,14.5 Z";
    public const string Trash = "M2,4 L14,4 M6,4 L6,2 L10,2 L10,4 M3.5,4 L4.5,15 L11.5,15 L12.5,4 M6.5,7 L6.5,12 M9.5,7 L9.5,12";
    public const string Move = "M5,1 L5,15 M2,4 L5,1 L8,4 M11,15 L11,1 M8,12 L11,15 L14,12";
    public const string Album = "M8,1 A7,7 0 1,1 7.99,1 Z M8,6 A2,2 0 1,1 7.99,6 Z";
    public const string Artist = "M8,1.5 A3.5,3.5 0 1,1 7.99,1.5 Z M1.5,15 C1.5,9.5 14.5,9.5 14.5,15";
    public const string Share = "M8,10 L8,1 M4.5,4.5 L8,1 L11.5,4.5 M2,8 L2,14 L14,14 L14,8";
    public const string Pencil = "M11,1.5 L14.5,5 L5,14.5 L1.5,14.5 L1.5,11 Z M9,3.5 L12.5,7";
    public const string People = "M5.5,2 A2.5,2.5 0 1,1 5.49,2 Z M0.5,14 C0.5,9.5 10.5,9.5 10.5,14 M11,2.2 A2.3,2.3 0 1,1 10.99,2.2 M12,9 C14,9.3 15.5,10.8 15.5,14";
    public const string Moon = "M13.5,10.5 A6.5,6.5 0 1,1 5.5,2.5 A5,5 0 0,0 13.5,10.5 Z";
    public const string CheckAll = "M1,8.5 L4,11.5 L10,5 M7,11.5 L8,12.5 L15,5";
    public const string Plus = "M8,2 L8,14 M2,8 L14,8";
    public const string Sort = "M2,4 L14,4 M2,8 L11,8 M2,12 L8,12";
    public const string Note = "M6,13 A2,2 0 1,1 5.99,13 M8,13 L8,2 L14,1 L14,11 M14,11 A2,2 0 1,1 13.99,11";
    public const string Computer = "M1,2.5 L15,2.5 L15,11.5 L1,11.5 Z M5,15 L11,15 M8,11.5 L8,15";
    public const string Phone = "M4,1 L12,1 L12,15 L4,15 Z M7,12.5 L9,12.5";
    public const string Speaker = "M4,1 L12,1 L12,15 L4,15 Z M8,10 A2.2,2.2 0 1,1 7.99,10 M8,4 L8,4.5";
    public const string Output = "M2,5.5 L5,5.5 L9,2 L9,14 L5,10.5 L2,10.5 Z M11.5,5 A4,4 0 0,1 11.5,11 M13.5,3 A7,7 0 0,1 13.5,13";
    public const string Tv = "M1,3 L15,3 L15,12 L1,12 Z M5,15 L11,15";
}
