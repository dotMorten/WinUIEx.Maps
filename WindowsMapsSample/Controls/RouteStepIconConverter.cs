using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using WindowsMapsSample.Models;

namespace WindowsMapsSample.Controls;

public sealed partial class RouteStepIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is RouteManeuverIcon icon &&
        string.Equals(icon.ToString(), parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
