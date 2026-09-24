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

    private double _titleWidth = 340;
    private double _artistWidth = 200;
    private double _albumWidth = 200;
    private double _durationWidth = 64;

    public double TitleWidth { get => _titleWidth; set => Set(ref _titleWidth, value); }
    public double ArtistWidth { get => _artistWidth; set => Set(ref _artistWidth, value); }
    public double AlbumWidth { get => _albumWidth; set => Set(ref _albumWidth, value); }
    public double DurationWidth { get => _durationWidth; set => Set(ref _durationWidth, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        value = Math.Max(MinWidth, value);
        if (Math.Abs(field - value) < 0.5)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Header row (TITLE / ARTIST / ALBUM / TIME) with a drag handle at the right edge of each
/// column, driving a shared <see cref="TrackColumnLayout"/>.
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

    private static readonly (string Header, string Property)[] Columns =
    {
        ("TITLE", nameof(TrackColumnLayout.TitleWidth)),
        ("ARTIST", nameof(TrackColumnLayout.ArtistWidth)),
        ("ALBUM", nameof(TrackColumnLayout.AlbumWidth)),
        ("TIME", nameof(TrackColumnLayout.DurationWidth)),
    };

    public TrackListHeader()
    {
        // Matches ListViewItem's default horizontal padding so headers line up with cells.
        Padding = new Thickness(8, 0, 8, 6);

        for (var i = 0; i < Columns.Length; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = Columns[i].Header,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Padding = new Thickness(0, 0, 12, 0)
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
            ((FrameworkElement)Children[i]).Width = GetWidth(Columns[i].Property);
    }

    internal double GetWidth(string property) => property switch
    {
        nameof(TrackColumnLayout.TitleWidth) => Layout!.TitleWidth,
        nameof(TrackColumnLayout.ArtistWidth) => Layout!.ArtistWidth,
        nameof(TrackColumnLayout.AlbumWidth) => Layout!.AlbumWidth,
        _ => Layout!.DurationWidth
    };

    internal void SetWidth(string property, double value)
    {
        switch (property)
        {
            case nameof(TrackColumnLayout.TitleWidth): Layout!.TitleWidth = value; break;
            case nameof(TrackColumnLayout.ArtistWidth): Layout!.ArtistWidth = value; break;
            case nameof(TrackColumnLayout.AlbumWidth): Layout!.AlbumWidth = value; break;
            default: Layout!.DurationWidth = value; break;
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
