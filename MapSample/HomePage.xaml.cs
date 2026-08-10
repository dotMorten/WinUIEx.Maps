using MapSample.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MapSample;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        TokenInput.Password = MapServiceTokenStore.Current;
    }

    private void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        MapServiceTokenStore.Save(TokenInput.Password);
        SaveStatus.Title = string.IsNullOrWhiteSpace(TokenInput.Password)
            ? "Saved token removed"
            : "Token saved";
        SaveStatus.IsOpen = true;
    }
}
