using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LunaDrome.Controls;

/// <summary>
/// Feishin-style responsive sizing for the Home page: album cards (and genre tiles) grow or
/// shrink so a whole number of them exactly fills the row width - ~8 cards across at full
/// screen, fewer in a smaller window - instead of fixed-size cards that leave a ragged gap.
/// Templates bind to it via {Binding CardWidth, Source={StaticResource HomeLayout}};
/// MainPage calls <see cref="Update"/> when the Home page resizes.
/// </summary>
public sealed class HomeLayout : INotifyPropertyChanged
{
    public const double Gap = 16;
    private const double MinCardWidth = 170;
    private const double MinGenreTileWidth = 200;

    private double _cardWidth = 180;
    private double _genreTileWidth = 220;
    private int _cardsPerRow = 8;

    public double CardWidth { get => _cardWidth; private set => Set(ref _cardWidth, value); }
    public double GenreTileWidth { get => _genreTileWidth; private set => Set(ref _genreTileWidth, value); }
    public int CardsPerRow { get => _cardsPerRow; private set { if (_cardsPerRow != value) { _cardsPerRow = value; Raise(); } } }

    private int _genreTilesPerRow = 6;
    public int GenreTilesPerRow { get => _genreTilesPerRow; private set { if (_genreTilesPerRow != value) { _genreTilesPerRow = value; Raise(); } } }

    public void Update(double rowWidth)
    {
        if (rowWidth <= 0)
            return;

        var cards = Math.Max(2, (int)((rowWidth + Gap) / (MinCardWidth + Gap)));
        CardsPerRow = cards;
        CardWidth = Math.Floor((rowWidth - (cards - 1) * Gap) / cards);

        var tiles = Math.Max(2, (int)((rowWidth + Gap) / (MinGenreTileWidth + Gap)));
        GenreTilesPerRow = tiles;
        GenreTileWidth = Math.Floor((rowWidth - (tiles - 1) * Gap) / tiles);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (Math.Abs(field - value) < 0.5)
            return;
        field = value;
        Raise(name);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
