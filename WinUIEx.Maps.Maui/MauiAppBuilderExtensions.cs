using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MauiMap = Microsoft.Maui.Controls.Maps.Map;

namespace WinUIEx.Maps.Maui;

public static class MauiAppBuilderExtensions
{
    /// <summary>Registers MAUI Maps, replacing its Windows handler with the native WinUIEx.Maps control.</summary>
    public static MauiAppBuilder UseWinUIExMaps(this MauiAppBuilder builder, string mapServiceToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(mapServiceToken);
        builder.UseMauiMaps();
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<MauiMap, WinUIExMapHandler>());
        builder.Services.AddSingleton(new MapConfiguration(mapServiceToken));
        return builder;
    }

    internal sealed class MapConfiguration(string token)
    {
        internal string Token { get; } = token;
    }
}
