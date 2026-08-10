using Windows.Storage;

namespace MapSample.Services;

internal static class MapServiceTokenStore
{
    private const string SettingKey = "MapServiceToken";
    private static string? _current;

    internal static string Current => _current ??= Load();

    internal static void Save(string token)
    {
        string normalized = token.Trim();
        if (normalized.Length == 0)
        {
            ApplicationData.Current.LocalSettings.Values.Remove(SettingKey);
        }
        else
        {
            ApplicationData.Current.LocalSettings.Values[SettingKey] = normalized;
        }
        _current = normalized;
    }

    private static string Load()
    {
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(
            SettingKey,
            out object? storedToken) &&
            storedToken is string token)
        {
            return token;
        }

        return string.Empty;
    }
}
