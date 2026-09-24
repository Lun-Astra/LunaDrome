using Microsoft.UI.Xaml.Data;

namespace LunaDrome.Controls;

/// <summary>Favorite heart fill: white when favorited, hollow otherwise.</summary>
public sealed class FavoriteFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
