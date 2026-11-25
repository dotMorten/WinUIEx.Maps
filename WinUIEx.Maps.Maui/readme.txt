WinUIEx.Maps.Maui - Required setup

Call UseWinUIExMaps in MauiProgram.CreateMauiApp before builder.Build()
to register the WinUIEx.Maps handler for MAUI Maps on Windows.
Installing the package alone does not register the handler.

Add this using directive:

#if WINDOWS
using WinUIEx.Maps.Maui;
#endif

After creating and configuring your MauiAppBuilder, add:

#if WINDOWS
builder.UseWinUIExMaps("YOUR_AZURE_MAPS_KEY");
#endif

Then call builder.Build() as usual.

Pass your Azure Maps subscription key to UseWinUIExMaps. It calls
UseMauiMaps() for you and replaces the Windows map handler.
Keep your existing map setup for other platforms.
