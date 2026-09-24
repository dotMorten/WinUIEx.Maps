using Windows.Storage;

namespace WindowsMapsSample.Services;

internal interface IMapServiceTokenStore
{
    string Load();
    void Save(string token);
}

internal sealed class MapServiceTokenStore : IMapServiceTokenStore
{
    private const string SettingKey = "WindowsMapsSample.AzureMapsToken";

    public string Load() =>
        ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out object? value) &&
        value is string token ? token : string.Empty;

    public void Save(string token)
    {
        string normalized = token.Trim();
        if (normalized.Length == 0)
        {
            ApplicationData.Current.LocalSettings.Values.Remove(SettingKey);
            return;
        }

        ApplicationData.Current.LocalSettings.Values[SettingKey] = normalized;
    }
}
