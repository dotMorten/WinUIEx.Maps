using Microsoft.Extensions.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;
using Windows.Devices.Geolocation;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
[DoNotParallelize]
public sealed class MapControlAuthenticationTests
{
    private const string TokenSecretName = "AzureMaps:MapServiceToken";
    private const string TokenSetupCommand =
        "dotnet user-secrets set \"AzureMaps:MapServiceToken\" \"<Azure Maps subscription key>\" --project .\\WinUIEx.Maps.Tests\\WinUIEx.Maps.Tests.csproj";
    private static readonly BasicGeoposition AzureTestCenter = new()
    {
        Latitude = 47.6062,
        Longitude = -122.3321,
    };

    [TestMethod]
    [DataRow((int)MapStyle.RoadRaster)]
    [DataRow((int)MapStyle.GrayscaleDarkRaster)]
    [DataRow((int)MapStyle.Satellite)]
    [DataRow((int)MapStyle.RoadShadedReliefRaster)]
    [DataRow((int)MapStyle.BlankAccessible)]
    [DataRow((int)MapStyle.GrayscaleLight)]
    [DataRow((int)MapStyle.Night)]
    [DataRow((int)MapStyle.HighContrastDark)]
    [DataRow((int)MapStyle.HighContrastLight)]
    [DataRow((int)MapStyle.SatelliteWithRoads)]
    [DataRow((int)MapStyle.Road)]
    [DataRow((int)MapStyle.GrayscaleDark)]
    [DataRow((int)MapStyle.RoadShadedRelief)]
    public async Task AzureStyleLoadsMapData(int styleValue)
    {
        string token = GetAzureMapsTokenOrMarkInconclusive();
        MapStyle style = (MapStyle)styleValue;
        using RenderingEventListener listener = new(
            "RasterCoverageMilestone",
            "VectorTileCommitSummary");

        await MapControlTestHost.LoadMapControlAsync(
            AzureTestCenter,
            6,
            async map =>
            {
                map.MapServiceToken = token;
                map.MapStyle = style;

                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(30));
                MapRenderFrame frame =
                    await map.CaptureRenderedFrameAsync(timeout.Token);
                Assert.IsGreaterThan(0, frame.Width);
                Assert.IsGreaterThan(0, frame.Height);
                if (style != MapStyle.BlankAccessible)
                {
                    Assert.IsTrue(
                        ContainsRenderedMapPixel(frame),
                        $"Azure style {style} left the map surface blank.");
                }
                await WaitForAsync(
                    () => HasRenderedAzureData(listener, style),
                    $"Azure style {style} did not render map data.");

                InfoBar infoBar = FindDescendant<InfoBar>(
                    map,
                    "PART_AzureAuthenticationInfoBar");
                Assert.IsFalse(infoBar.IsOpen);
            });
    }

    [TestMethod]
    public Task SwitchingToBlankBeforeValidationDoesNotShowTokenWarning()
    {
        return MapControlTestHost.LoadMapControlAsync(async map =>
        {
            map.MapStyle = MapStyle.Blank;

            await Task.Delay(750);

            InfoBar infoBar = FindDescendant<InfoBar>(
                map,
                "PART_AzureAuthenticationInfoBar");
            Assert.IsFalse(infoBar.IsOpen);
        });
    }

    [TestMethod]
    public Task MissingTokenShowsWarningForAzureBasemap()
    {
        return MapControlTestHost.LoadMapControlAsync(async map =>
        {
            await Task.Delay(750);

            InfoBar infoBar = FindDescendant<InfoBar>(
                map,
                "PART_AzureAuthenticationInfoBar");
            Assert.IsTrue(infoBar.IsOpen);
            Assert.AreEqual(
                "Azure Maps token required",
                infoBar.Title);
            AssertAccessibleAuthenticationMessage(
                infoBar,
                InfoBarSeverity.Warning,
                "Azure Maps token required",
                "Set MapServiceToken to display the selected Azure basemap.");
        });
    }

    [TestMethod]
    public Task InvalidTokenShowsAccessibleAuthenticationError()
    {
        return MapControlTestHost.LoadMapControlAsync(
            AzureTestCenter,
            6,
            async map =>
            {
                map.MapServiceToken = "invalid-test-token";
                map.MapStyle = MapStyle.RoadRaster;

                InfoBar infoBar = FindDescendant<InfoBar>(
                    map,
                    "PART_AzureAuthenticationInfoBar");
                await WaitForAsync(
                    () => infoBar.IsOpen &&
                        string.Equals(
                            infoBar.Title,
                            "Azure Maps authentication failed",
                            StringComparison.Ordinal),
                    "Azure Maps did not report the invalid token.");
                AssertAccessibleAuthenticationMessage(
                    infoBar,
                    InfoBarSeverity.Error,
                    "Azure Maps authentication failed",
                    "Azure Maps rejected MapServiceToken. Verify the token and try again.");
            });
    }

    private static string GetAzureMapsTokenOrMarkInconclusive()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets<MapControlAuthenticationTests>(optional: true)
            .Build();
        string? token = configuration[TokenSecretName];
        if (string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive(
                $"An Azure Maps token is required for this test. Run: {TokenSetupCommand}");
        }

        return token!;
    }

    private static bool HasRenderedAzureData(
        RenderingEventListener listener,
        MapStyle style)
    {
        bool needsVector = AzureTileAcquisitionSession.IsVectorStyle(style);
        bool needsRaster =
            !needsVector ||
            AzureTileAcquisitionSession.IsHybridStyle(style);
        bool hasVector = !needsVector ||
            listener.Events("VectorTileCommitSummary").Any(captured =>
                (int)captured.Payload[0]! == (int)style &&
                (int)captured.Payload[1]! > 0);
        bool hasRaster = !needsRaster ||
            listener.Events("RasterCoverageMilestone").Any(captured =>
                string.Equals(
                    captured.Payload[3] as string,
                    "FirstTile",
                    StringComparison.Ordinal));
        return hasVector && hasRaster;
    }

    private static bool ContainsRenderedMapPixel(MapRenderFrame frame)
    {
        ReadOnlySpan<byte> pixels = frame.Pixels.Span;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (Math.Abs(pixels[index] - 240) > 8 ||
                Math.Abs(pixels[index + 1] - 240) > 8 ||
                Math.Abs(pixels[index + 2] - 240) > 8)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAccessibleAuthenticationMessage(
        InfoBar infoBar,
        InfoBarSeverity severity,
        string title,
        string message)
    {
        string accessibleName = $"{title}. {message}";
        Assert.AreEqual(severity, infoBar.Severity);
        Assert.AreEqual(title, infoBar.Title);
        Assert.AreEqual(message, infoBar.Message);
        Assert.AreEqual(
            AccessibilityView.Control,
            AutomationProperties.GetAccessibilityView(infoBar));
        Assert.AreEqual(
            AutomationLiveSetting.Assertive,
            AutomationProperties.GetLiveSetting(infoBar));
        Assert.AreEqual(
            accessibleName,
            AutomationProperties.GetName(infoBar));

        AutomationPeer peer =
            FrameworkElementAutomationPeer.FromElement(infoBar) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(infoBar)!;
        Assert.IsNotNull(peer);
        Assert.AreEqual(accessibleName, peer.GetName());
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        string failureMessage)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(failureMessage);
    }

    private static T FindDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match &&
                string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            T? descendant = FindDescendantOrDefault<T>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        throw new AssertFailedException(
            $"Could not find {typeof(T).Name} named {name}.");
    }

    private static T? FindDescendantOrDefault<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match &&
                string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            T? descendant = FindDescendantOrDefault<T>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
