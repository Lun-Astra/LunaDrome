using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LunaDrome.Controls;

/// <summary>
/// Home row header, Feishin-style: section title on the left, ‹ › arrows on the right that
/// page the row's horizontal ScrollViewer (the next sibling in the same panel) by ~80% of its
/// width. Replaces scrolling rows with the mouse wheel / scrollbar - the wheel now scrolls the
/// page vertically as usual. Arrows disable themselves at either end of the row.
/// </summary>
public sealed class CarouselHeader : Grid
{
    private readonly TextBlock _title;
    private readonly Button _more;
    private readonly Button _left;
    private readonly Button _right;
    private ScrollViewer? _target;

    public event RoutedEventHandler? MoreClick;

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public bool ShowMore
    {
        get => _more.Visibility == Visibility.Visible;
        set => _more.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public CarouselHeader()
    {
        Margin = new Thickness(0, 0, 0, 10);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Children.Add(_title);

        _more = new Button
        {
            Content = "More ›",
            Style = (Style)Application.Current.Resources["LinkButtonStyle"],
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _more.Click += (s, e) => MoreClick?.Invoke(this, e);
        SetColumn(_more, 1);
        Children.Add(_more);

        _left = MakeArrow("M6,1 L1,6 L6,11", "Previous");
        _right = MakeArrow("M1,1 L6,6 L1,11", "Next");
        _left.Click += (_, _) => Page(-1);
        _right.Click += (_, _) => Page(1);

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(_left);
        arrows.Children.Add(_right);
        SetColumn(arrows, 2);
        Children.Add(arrows);

        Loaded += OnLoaded;
    }

    private static Button MakeArrow(string data, string tooltip)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Width = 28,
            Height = 28,
            Content = new Path
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 7,
                Height = 12
            }
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_target != null || Parent is not Panel panel)
            return;

        var index = panel.Children.IndexOf(this);
        _target = panel.Children.Skip(index + 1).OfType<ScrollViewer>().FirstOrDefault();
        if (_target == null)
            return;

        _target.ViewChanged += (_, _) => UpdateArrows();
        _target.SizeChanged += (_, _) => UpdateArrows();
        if (_target.Content is FrameworkElement content)
            content.SizeChanged += (_, _) => UpdateArrows(); // items arrive after the page loads
        UpdateArrows();
    }

    private void Page(int direction)
    {
        if (_target == null)
            return;

        var step = Math.Max(100, _target.ViewportWidth * 0.8);
        var offset = Math.Clamp(_target.HorizontalOffset + direction * step, 0, _target.ScrollableWidth);
        _target.ChangeView(offset, null, null, false);
    }

    private void UpdateArrows()
    {
        if (_target == null)
            return;

        _left.IsEnabled = _target.HorizontalOffset > 1;
        _right.IsEnabled = _target.HorizontalOffset < _target.ScrollableWidth - 1;
    }
}
