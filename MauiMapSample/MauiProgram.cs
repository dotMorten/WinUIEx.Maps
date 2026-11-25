using Microsoft.Extensions.Logging;
using WinUIEx.Maps.Maui;

namespace MauiMapSample;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseWinUIExMaps(Environment.GetEnvironmentVariable("AZURE_MAPS_KEY")
				?? Environment.GetEnvironmentVariable("AZURE_MAPS_KEY", EnvironmentVariableTarget.User)
				?? string.Empty)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
