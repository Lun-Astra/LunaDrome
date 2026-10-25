using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome.Controls;

/// <summary>
/// Shared column widths for every track list (album, playlist, library-wide). Row templates
/// bind their cell widths to this via {Binding TitleWidth, Source={StaticResource TrackColumns}},
/// and <see cref="TrackListHeader"/> changes them by dragging - so all rows (and all lists)
/// resize together. WinUI's ListView has no GridView-style resizable header like WPF's, hence
/// this hand-rolled version.
/// </summary>
public sealed class TrackColumnLayout : INotifyPropertyChanged
{
    public const double MinWidth = 48;

    /// <summary>The Tracks page's heart column: fixed width, taken out of the title column so that
    /// page lines up with the other lists.</summary>
    public const double FavoriteWidth = 44;

    private double _titleWidth = 290;
    private double _timeWidth = 64;
    private double _genreWidth = 140;

    public double TitleWidth { get => _titleWidth; set => Set(ref _titleWidth, value); }
    public double TimeWidth { get => _timeWidth; set => Set(ref _timeWidth, value); }
    public double GenreWidth { get => _genreWidth; set => Set(ref _genreWidth, value); }
    public double FavoriteColumnWidth => FavoriteWidth;
    public double TitleWidthBesideFavorite => Math.Max(MinWidth, _titleWidth - FavoriteWidth);
    /// <summary>Discover rows: a heart and a "Not for me" cross, both out of the title column.</summary>
    public double TitleWidthBesideTwoButtons => Math.Max(MinWidth, _titleWidth - 2 * FavoriteWidth);

    public event PropertyChangedEventHandler? PropertyChanged;

    private (double Title, double Time, double Genre)? _desktopWidths;

    /// <summary>Phones: the title column fills the width and the genre column is hidden (0 wide,
    /// which the draggable Set() doesn't allow). Switching back restores the desktop widths.</summary>
    public void SetCompact(bool compact, double titleWidth)
    {
        if (compact)
        {
            _desktopWidths ??= (_titleWidth, _timeWidth, _genreWidth);
            _titleWidth = Math.Max(MinWidth, titleWidth);
            _genreWidth = 0;
        }
        else if (_desktopWidths is { } d)
        {
            (_titleWidth, _timeWidth, _genreWidth) = d;
            _desktopWidths = null;
        }
        else
            return;
        foreach (var name in new[] { nameof(TitleWidth), nameof(TitleWidthBesideFavorite), nameof(TitleWidthBesideTwoButtons),
                     nameof(TimeWidth), nameof(GenreWidth) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        value = Math.Max(MinWidth, value);
        if (Math.Abs(field - value) < 0.5)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(TitleWidth))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleWidthBesideFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleWidthBesideTwoButtons)));
        }
    }
}

/// <summary>
/// Header row (TITLE / TIME / GENRE, matching the WPF app's GridView columns) with a drag handle at the right edge of each
/// column, driving a shared <see cref="TrackColumnLayout"/>. With <see cref="ShowFavorite"/> (the Tracks
/// page) a fixed heart column follows the title.
/// </summary>
public sealed class TrackListHeader : Grid
{
    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.Register(nameof(Layout), typeof(TrackColumnLayout), typeof(TrackListHeader),
            new PropertyMetadata(null, (d, e) => ((TrackListHeader)d).OnLayoutChanged(e)));

    public TrackColumnLayout? Layout
    {
        get => (TrackColumnLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static readonly DependencyProperty ShowFavoriteProperty =
        DependencyProperty.Register(nameof(ShowFavorite), typeof(bool), typeof(TrackListHeader),
            new PropertyMetadata(false, (d, _) => ((TrackListHeader)d).ApplyWidths()));

    /// <summary>Show the heart column (matching TracksPageRowTemplate).</summary>
    public bool ShowFavorite
    {
        get => (bool)GetValue(ShowFavoriteProperty);
        set => SetValue(ShowFavoriteProperty, value);
    }

    // A null header is the heart column (an icon, no drag handle).
    private static readonly (string? Header, string Property)[] Columns =
    {
        ("TITLE", nameof(TrackColumnLayout.TitleWidth)),
        (null, nameof(TrackColumnLayout.FavoriteColumnWidth)),
        ("TIME", nameof(TrackColumnLayout.TimeWidth)),
        ("GENRE", nameof(TrackColumnLayout.GenreWidth)),
    };

    private const string HeartPath =
        "M8,15 C8,15 1,10.5 1,5.8 C1,3 3,1.3 5.4,1.3 C6.9,1.3 7.7,2.1 8,2.6 C8.3,2.1 9.1,1.3 10.6,1.3 C13,1.3 15,3 15,5.8 C15,10.5 8,15 8,15 Z";

    public TrackListHeader()
    {
        // WPF GridViewColumnHeader look: 11px semibold secondary text, 8,4 padding, a hairline rule.
        Padding = new Thickness(0);
        Margin = new Thickness(0, 0, 0, 2);
        BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"];
        BorderThickness = new Thickness(0, 1, 0, 0);

        for (var i = 0; i < Columns.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (Columns[i].Header == null)
            {
                var heartCell = new Grid();
                heartCell.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Width = 11,
                    Height = 10,
                    Stretch = Stretch.Uniform,
                    Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                    StrokeThickness = 1.3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), HeartPath)
                });
                ToolTipService.SetToolTip(heartCell, "Favorited");
                SetColumn(heartCell, i);
                Children.Add(heartCell);
                continue;
            }

            var label = new TextBlock
            {
                Text = Columns[i].Header,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Padding = new Thickness(8, 4, 8, 4)
            };
            var cell = new Grid();
            cell.Children.Add(label);
            cell.Children.Add(new ColumnSplitter(this, Columns[i].Property));
            SetColumn(cell, i);
            Children.Add(cell);
        }

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void OnLayoutChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TrackColumnLayout oldLayout)
            oldLayout.PropertyChanged -= Layout_PropertyChanged;
        if (e.NewValue is TrackColumnLayout newLayout)
            newLayout.PropertyChanged += Layout_PropertyChanged;
        ApplyWidths();
    }

    private void Layout_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ApplyWidths();

    private void ApplyWidths()
    {
        if (Layout == null)
            return;

        for (var i = 0; i < Columns.Length; i++)
        {
            var cell = (FrameworkElement)Children[i];
            var property = Columns[i].Property;
            if (property == nameof(TrackColumnLayout.FavoriteColumnWidth))
            {
                cell.Visibility = ShowFavorite ? Visibility.Visible : Visibility.Collapsed;
                cell.Width = TrackColumnLayout.FavoriteWidth;
            }
            else
                cell.Width = ShowFavorite && property == nameof(TrackColumnLayout.TitleWidth)
                    ? Layout.TitleWidthBesideFavorite
                    : GetWidth(property);
        }
    }

    internal double GetWidth(string property) => property switch
    {
        nameof(TrackColumnLayout.TitleWidth) => Layout!.TitleWidth,
        nameof(TrackColumnLayout.TimeWidth) => Layout!.TimeWidth,
        _ => Layout!.GenreWidth
    };

    internal void SetWidth(string property, double value)
    {
        switch (property)
        {
            case nameof(TrackColumnLayout.TitleWidth): Layout!.TitleWidth = value; break;
            case nameof(TrackColumnLayout.TimeWidth): Layout!.TimeWidth = value; break;
            default: Layout!.GenreWidth = value; break;
        }
    }

    /// <summary>A thin grab strip on a column's right edge; drag to resize.</summary>
    private sealed class ColumnSplitter : Grid
    {
        private readonly TrackListHeader _owner;
        private readonly string _property;
        private readonly Border _line;
        private double _startX;
        private double _startWidth;
        private bool _dragging;

        public ColumnSplitter(TrackListHeader owner, string property)
        {
            _owner = owner;
            _property = property;

            Width = 10;
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(0, 0, -5, 0); // straddle the column boundary
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

            _line = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = (Brush)Application.Current.Resources["BorderBrush2"],
                Opacity = 0.6
            };
            Children.Add(_line);

            PointerEntered += (_, _) => _line.Opacity = 1;
            PointerExited += (_, _) => { if (!_dragging) _line.Opacity = 0.6; };
            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
            PointerCaptureLost += (_, _) => EndDrag();
        }

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_owner.Layout == null)
                return;

            _dragging = true;
            _startX = e.GetCurrentPoint(_owner).Position.X;
            _startWidth = _owner.GetWidth(_property);
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
                return;

            var x = e.GetCurrentPoint(_owner).Position.X;
            _owner.SetWidth(_property, _startWidth + (x - _startX));
            e.Handled = true;
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
        }

        private void EndDrag()
        {
            _dragging = false;
            _line.Opacity = 0.6;
        }
    }
}
