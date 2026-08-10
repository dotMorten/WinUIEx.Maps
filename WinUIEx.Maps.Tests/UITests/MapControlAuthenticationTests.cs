using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
public sealed class MapControlAuthenticationTests
{
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
        });
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
