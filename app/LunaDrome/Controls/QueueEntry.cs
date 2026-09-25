using System.ComponentModel;
using LunaDrome.Core.Models;

namespace LunaDrome.Controls;

/// <summary>
/// One row of the play queue. Wrapping the Song means the same track can sit in the queue
/// twice (Play next on the song that's playing, an album added twice) without IndexOf finding
/// the wrong copy, and lets the playing row be marked independently of the (multi-)selection.
/// </summary>
public sealed class QueueEntry : INotifyPropertyChanged
{
    public QueueEntry(Song song) => Song = song;

    public Song Song { get; }

    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentMarkerVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleFontWeight)));
        }
    }

    public Visibility CurrentMarkerVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    // The accent is white in this theme, same as normal text - so the playing title goes bold.
    public Windows.UI.Text.FontWeight TitleFontWeight => IsCurrent ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;
}
