using System.Windows.Input;

namespace WorkingWithMaps.Views;

public partial class MainPage : ContentPage
{
    public ICommand NavigateCommand { get; set; }

    public MainPage()
    {
        InitializeComponent();

        NavigateCommand = new Command<Type>(async (Type pageType) =>
        {
            Page page = Activator.CreateInstance(pageType) as Page
                ?? throw new InvalidOperationException($"Could not create sample page {pageType.Name}.");
            await Navigation.PushAsync(page);
        });

        BindingContext = this;
    }
}

