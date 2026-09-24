using Microsoft.UI.Xaml;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WindowsMapsSample;

// The sample page is source-linked into the existing WinUI host, not a second application.
internal static class App
{
    internal static Window Window => MapControlTestHost.Window;
}
