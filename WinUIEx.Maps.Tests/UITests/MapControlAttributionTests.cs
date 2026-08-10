using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests.UITests;

[TestClass]
public sealed class MapControlAttributionTests
{
    [TestMethod]
    public Task AttributionAggregatesVisibleLayersAndCollapsesWhenEmpty()
    {
        return MapControlTestHost.LoadMapControlAsync(map =>
        {
            map.MapStyle = MapStyle.Blank;
            MapElementsLayer textLayer = new()
            {
                Attribution = "Plain attribution",
            };
            MapElementsLayer linkLayer = new()
            {
                Attribution = "Linked attribution",
                AttributionLink = new Uri("https://example.com/attribution"),
            };
            map.Layers.Add(textLayer);
            map.Layers.Add(linkLayer);

            Border container = FindDescendant<Border>(
                map,
                "PART_AttributionContainer");
            TextBlock text = FindDescendant<TextBlock>(
                map,
                "PART_Attribution");

            Assert.AreEqual(Visibility.Visible, container.Visibility);
            Assert.HasCount(3, text.Inlines);
            Assert.IsInstanceOfType<Run>(text.Inlines[0]);
            Assert.IsInstanceOfType<Run>(text.Inlines[1]);
            Assert.IsInstanceOfType<Hyperlink>(text.Inlines[2]);
            Hyperlink hyperlink = (Hyperlink)text.Inlines[2];
            Assert.AreEqual(
                "https://example.com/attribution",
                hyperlink.NavigateUri.AbsoluteUri.TrimEnd('/'));
            Assert.AreEqual(
                "Linked attribution",
                AutomationProperties.GetName(hyperlink));
            Assert.AreEqual(
                AccessibilityView.Content,
                AutomationProperties.GetAccessibilityView(text));
            Assert.AreEqual(
                AutomationLiveSetting.Polite,
                AutomationProperties.GetLiveSetting(text));
            Assert.AreEqual(
                "Map attribution: Plain attribution, Linked attribution",
                AutomationProperties.GetName(text));

            AutomationPeer peer =
                FrameworkElementAutomationPeer.FromElement(text) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(text)!;
            Assert.IsNotNull(peer);
            Assert.AreEqual(
                "Map attribution: Plain attribution, Linked attribution",
                peer.GetName());

            linkLayer.AttributionLink = null;

            Assert.IsInstanceOfType<Run>(text.Inlines[2]);

            textLayer.Attribution = string.Empty;
            linkLayer.IsVisible = false;

            Assert.AreEqual(Visibility.Collapsed, container.Visibility);
            Assert.IsEmpty(text.Inlines);
            return Task.CompletedTask;
        });
    }

    private static T FindDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        if (root is T match &&
            string.Equals(match.Name, name, StringComparison.Ordinal))
        {
            return match;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            try
            {
                return FindDescendant<T>(
                    VisualTreeHelper.GetChild(root, index),
                    name);
            }
            catch (AssertFailedException)
            {
            }
        }

        throw new AssertFailedException(
            $"Could not find {typeof(T).Name} named {name}.");
    }
}
