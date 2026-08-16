using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WinUIEx.Maps.Localization;

internal static class MapControlResources
{
    private static readonly ResourceLoader Loader =
        new(
            Path.Combine(
                AppContext.BaseDirectory,
                "WinUIEx.Maps.pri"),
            "WinUIEx.Maps/Resources");

    internal static string GetString(string resourceId) =>
        Loader.GetString(resourceId);

    internal static string Format(string resourceId, params object[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            GetString(resourceId),
            arguments);
}
