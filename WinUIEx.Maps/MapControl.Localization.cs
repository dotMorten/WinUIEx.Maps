using Microsoft.UI.Xaml;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private void InitializeLocalization()
    {
        RegisterPropertyChangedCallback(
            LanguageProperty,
            OnLanguageChanged);
    }

    private void OnLanguageChanged(
        DependencyObject sender,
        DependencyProperty property)
    {
        ReplaceAzureTileLayer();
        PublishLayerSnapshots();
    }
}
