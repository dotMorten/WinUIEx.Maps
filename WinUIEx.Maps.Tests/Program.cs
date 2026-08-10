using Microsoft.Testing.Platform.Builder;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        string[] arguments = Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(argument =>
                !argument.Contains("EnableMSTestRunner", StringComparison.Ordinal))
            .ToArray();
        ITestApplicationBuilder builder =
            await Microsoft.Testing.Platform.Builder.TestApplication.CreateBuilderAsync(
                arguments);
        builder.AddSelfRegisteredExtensions(arguments);
        using ITestApplication testApplication = await builder.BuildAsync();
        try
        {
            return await testApplication.RunAsync();
        }
        finally
        {
            await MapControlTestHost.ShutdownAsync();
        }
    }
}
