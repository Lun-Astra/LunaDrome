using LunaDrome.Core.Models;
using Microsoft.UI.Xaml.Documents;

namespace LunaDrome.Controls;

/// <summary>
/// A song's artist line with each artist separately clickable: "Jeff Williams, Casey Lee
/// Williams" becomes two links (Hyperlinks inside the TextBlock, so trimming still works).
/// A single artist stays plain text and the surrounding element's tap goes to the artist as
/// before. Use <c>controls:ArtistLinks.Song="{Binding}"</c> in templates, or <see cref="Fill"/>.
/// </summary>
public static class ArtistLinks
{
    /// <summary>One of the links was clicked (MainPage opens that artist).</summary>
    public static event Action<ArtistRef>? Clicked;

    public static readonly DependencyProperty SongProperty = DependencyProperty.RegisterAttached(
        "Song", typeof(Song), typeof(ArtistLinks), new PropertyMetadata(null, (d, e) =>
        {
            if (d is TextBlock text)
                Fill(text, e.NewValue as Song);
        }));

    public static Song? GetSong(DependencyObject element) => (Song?)element.GetValue(SongProperty);
    public static void SetSong(DependencyObject element, Song? value) => element.SetValue(SongProperty, value);

    /// <summary>True when the song's artists are shown as separate links (the line's own tap then does nothing).</summary>
    public static bool HasLinks(Song? song) => song?.Artists.Count > 1;

    public static void Fill(TextBlock text, Song? song)
    {
        text.Inlines.Clear();
        if (song == null)
            return;
        if (!HasLinks(song))
        {
            text.Inlines.Add(new Run { Text = song.ArtistName });
            return;
        }
        for (var i = 0; i < song.Artists.Count; i++)
        {
            if (i > 0)
                text.Inlines.Add(new Run { Text = ", " });
            var artist = song.Artists[i];
            // Grey like the text around it, white under the pointer (Hyperlink* brushes in
            // App.xaml), so you see which one artist you're about to open.
            var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
            link.Inlines.Add(new Run { Text = artist.Name });
            link.Click += (_, _) => Clicked?.Invoke(artist);
            ToolTipService.SetToolTip(link, $"Go to {artist.Name}");
            text.Inlines.Add(link);
        }
    }
}
