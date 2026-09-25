using Microsoft.UI.Xaml.Data;

namespace LunaDrome.Controls;

/// <summary>true -> Visible, anything else -> Collapsed (classic Binding has no implicit bool→Visibility).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
