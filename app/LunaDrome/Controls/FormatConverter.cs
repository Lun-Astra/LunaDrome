using Microsoft.UI.Xaml.Data;

namespace LunaDrome.Controls;

/// <summary>
/// WinUI bindings have no StringFormat, so "{0} albums"-style labels go through this:
/// Converter={StaticResource Format}, ConverterParameter='{}{0} albums'.
/// </summary>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        parameter is string format ? string.Format(format, value) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
