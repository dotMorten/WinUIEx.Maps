using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinUIEx.Maps.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapElementTests
{
    [TestMethod]
    public void MapElementIsAbstractAndNotDependencyObject()
    {
        Assert.IsTrue(typeof(MapElement).IsAbstract);
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapElement)));
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapIcon)));
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapPolygon)));
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapPolyline)));
        MethodInfo onChanged = typeof(MapElement).GetMethod(
            "OnChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsTrue(onChanged.IsFamilyAndAssembly);
        Assert.IsNull(typeof(MapControl).GetMethod(
            "InvalidateIconElement",
            BindingFlags.Instance | BindingFlags.Public));
    }

    [TestMethod]
    public void MapElementInteractionPropertiesPublishChanges()
    {
        TestMapElement element = new();
        int changes = 0;
        element.Changed += (_, _) => changes++;

        Assert.IsTrue(element.IsEnabled);
        Assert.IsTrue(element.IsVisible);
        Assert.AreEqual(0, element.ZIndex);

        element.IsEnabled = false;
        element.IsVisible = false;
        element.ZIndex = 4;
        element.ZIndex = 4;

        Assert.IsFalse(element.IsEnabled);
        Assert.IsFalse(element.IsVisible);
        Assert.AreEqual(4, element.ZIndex);
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public void MapElementExposesVisibilityInputAndZIndexProperties()
    {
        Dictionary<string, Type> properties = new()
        {
            [nameof(MapElement.IsEnabled)] = typeof(bool),
            [nameof(MapElement.IsVisible)] = typeof(bool),
            [nameof(MapElement.ZIndex)] = typeof(int),
        };

        foreach ((string name, Type type) in properties)
        {
            PropertyInfo property = typeof(MapElement).GetProperty(name)!;
            Assert.AreEqual(type, property.PropertyType);
            Assert.IsTrue(property.CanRead);
            Assert.IsTrue(property.CanWrite);
        }
    }

    [TestMethod]
    public void VectorElementApiMatchesSupportedSurface()
    {
        Dictionary<string, Type> polygonProperties = new()
        {
            [nameof(MapPolygon.FillColor)] = typeof(Windows.UI.Color),
            [nameof(MapPolygon.Path)] = typeof(Geopath),
            [nameof(MapPolygon.Paths)] = typeof(IList<Geopath>),
            [nameof(MapPolygon.StrokeColor)] = typeof(Windows.UI.Color),
            [nameof(MapPolygon.StrokeDashed)] = typeof(bool),
            [nameof(MapPolygon.StrokeThickness)] = typeof(double),
        };
        Dictionary<string, Type> polylineProperties = new()
        {
            [nameof(MapPolyline.Path)] = typeof(Geopath),
            [nameof(MapPolyline.StrokeColor)] = typeof(Windows.UI.Color),
            [nameof(MapPolyline.StrokeDashed)] = typeof(bool),
            [nameof(MapPolyline.StrokeThickness)] = typeof(double),
        };

        Assert.IsTrue(typeof(MapPolygon).IsSealed);
        Assert.IsTrue(typeof(MapPolyline).IsSealed);
        Assert.AreEqual(
            polygonProperties.Count,
            typeof(MapPolygon).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Length);
        Assert.AreEqual(
            polylineProperties.Count,
            typeof(MapPolyline).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Length);
        foreach ((string name, Type type) in polygonProperties)
        {
            PropertyInfo property = typeof(MapPolygon).GetProperty(name)!;
            Assert.AreEqual(type, property.PropertyType);
            Assert.IsTrue(property.CanRead);
            Assert.AreEqual(name != nameof(MapPolygon.Paths), property.CanWrite);
        }
        foreach ((string name, Type type) in polylineProperties)
        {
            PropertyInfo property = typeof(MapPolyline).GetProperty(name)!;
            Assert.AreEqual(type, property.PropertyType);
            Assert.IsTrue(property.CanRead);
            Assert.IsTrue(property.CanWrite);
        }
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(typeof(MapPolygon)));
        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(typeof(MapPolyline)));
    }

    [TestMethod]
    public void MapIconExposesNormalizedAnchorPoint()
    {
        PropertyInfo property = typeof(MapIcon).GetProperty(
            nameof(MapIcon.NormalizedAnchorPoint))!;

        Assert.AreEqual(typeof(Point), property.PropertyType);
        Assert.IsTrue(property.CanRead);
        Assert.IsTrue(property.CanWrite);
    }

    [TestMethod]
    public void MapControlExposesHeadingDependencyProperty()
    {
        PropertyInfo property = typeof(MapControl).GetProperty(
            nameof(MapControl.Heading))!;
        FieldInfo identifier = typeof(MapControl).GetField(
            nameof(MapControl.HeadingProperty),
            BindingFlags.Public | BindingFlags.Static)!;

        Assert.AreEqual(typeof(double), property.PropertyType);
        Assert.IsTrue(property.CanRead);
        Assert.IsTrue(property.CanWrite);
        Assert.AreEqual(typeof(DependencyProperty), identifier.FieldType);
    }

    [TestMethod]
    public void MapControlExposesPitchDependencyProperty()
    {
        PropertyInfo property = typeof(MapControl).GetProperty(
            nameof(MapControl.Pitch))!;
        FieldInfo identifier = typeof(MapControl).GetField(
            nameof(MapControl.PitchProperty),
            BindingFlags.Public | BindingFlags.Static)!;

        Assert.AreEqual(typeof(double), property.PropertyType);
        Assert.IsTrue(property.CanRead);
        Assert.IsTrue(property.CanWrite);
        Assert.AreEqual(typeof(DependencyProperty), identifier.FieldType);
    }

    [TestMethod]
    public void MapElementsLayerExposesElementInputEvents()
    {
        Dictionary<string, Type> expected = new()
        {
            [nameof(MapElementsLayer.PointerEntered)] =
                typeof(EventHandler<MapElementPointerEventArgs>),
            [nameof(MapElementsLayer.PointerExited)] =
                typeof(EventHandler<MapElementPointerEventArgs>),
            [nameof(MapElementsLayer.PointerMoved)] =
                typeof(EventHandler<MapElementPointerEventArgs>),
            [nameof(MapElementsLayer.PointerPressed)] =
                typeof(EventHandler<MapElementPointerEventArgs>),
            [nameof(MapElementsLayer.PointerReleased)] =
                typeof(EventHandler<MapElementPointerEventArgs>),
            [nameof(MapElementsLayer.Tapped)] =
                typeof(EventHandler<MapElementTappedEventArgs>),
            [nameof(MapElementsLayer.RightTapped)] =
                typeof(EventHandler<MapElementRightTappedEventArgs>),
        };

        foreach ((string name, Type handlerType) in expected)
        {
            Assert.AreEqual(
                handlerType,
                typeof(MapElementsLayer).GetEvent(name)?.EventHandlerType);
        }
    }

    [TestMethod]
    public void TapEventArgumentsExposeGeographicLocation()
    {
        Assert.AreEqual(
            typeof(Geopoint),
            typeof(MapElementTappedEventArgs)
                .GetProperty(nameof(MapElementTappedEventArgs.Location))?
                .PropertyType);
        Assert.AreEqual(
            typeof(Geopoint),
            typeof(MapElementRightTappedEventArgs)
                .GetProperty(nameof(MapElementRightTappedEventArgs.Location))?
                .PropertyType);
    }

    [TestMethod]
    public void LayerTracksOnlySubscribedInputKinds()
    {
        var layer = (MapElementsLayer)RuntimeHelpers.GetUninitializedObject(
            typeof(MapElementsLayer));
        EventHandler<MapElementPointerEventArgs> moved = (_, _) => { };

        Assert.AreEqual(MapElementInputEventKind.None, layer.InputHandlers);

        layer.PointerMoved += moved;

        Assert.AreEqual(MapElementInputEventKind.PointerMoved, layer.InputHandlers);

        layer.PointerMoved -= moved;

        Assert.AreEqual(MapElementInputEventKind.None, layer.InputHandlers);
    }

    [TestMethod]
    public void MapIconAnchorDefaultsToCenterAndPublishesChanges()
    {
        var icon = new MapIcon(
            (FontIcon)RuntimeHelpers.GetUninitializedObject(typeof(FontIcon)),
            new Geopoint(new BasicGeoposition()));
        int changeCount = 0;
        icon.Changed += (_, _) => changeCount++;

        Assert.AreEqual(new Point(0.5, 0.5), icon.NormalizedAnchorPoint);

        icon.NormalizedAnchorPoint = new Point(0.25, 1);

        Assert.AreEqual(new Point(0.25, 1), icon.NormalizedAnchorPoint);
        Assert.AreEqual(1, changeCount);
    }

    [TestMethod]
    public void MapIconPlacementUsesNormalizedAnchorPoint()
    {
        var location = new MapViewportPoint(100, 80);
        var centered = new MapIconSnapshot(1, 0, 0, 20, 10);
        var bottomLeft = centered with
        {
            NormalizedAnchorX = 0,
            NormalizedAnchorY = 1,
        };

        Assert.AreEqual(
            new MapViewportPoint(90, 75),
            MapRenderer.GetMapIconTopLeft(location, centered));
        Assert.AreEqual(
            new MapViewportPoint(100, 70),
            MapRenderer.GetMapIconTopLeft(location, bottomLeft));
    }

    [TestMethod]
    public void TextureReferencesUseObjectIdentityAndReferenceCounts()
    {
        MapIconTextureReferences references = new();
        EqualValue first = new(1);
        EqualValue second = new(1);

        MapIconTextureReferences.Entry firstEntry = references.Add(first);
        MapIconTextureReferences.Entry sharedEntry = references.Add(first);
        MapIconTextureReferences.Entry secondEntry = references.Add(second);

        Assert.AreSame(firstEntry, sharedEntry);
        Assert.AreNotEqual(firstEntry.TextureId, secondEntry.TextureId);
        Assert.AreEqual(2, firstEntry.ReferenceCount);
        Assert.IsNull(references.Remove(first));
        Assert.AreSame(firstEntry, references.Remove(first));
        Assert.AreSame(secondEntry, references.Remove(second));
        Assert.IsEmpty(references.Entries);
    }

    [TestMethod]
    public void AddRangePublishesOneCollectionChange()
    {
        MapElementCollection elements = [];
        List<NotifyCollectionChangedEventArgs> changes = [];
        elements.CollectionChanged += (_, args) => changes.Add(args);
        MapElement[] added = [new TestMapElement(), new TestMapElement()];

        elements.AddRange(added);

        Assert.AreSequenceEqual(added, elements);
        NotifyCollectionChangedEventArgs change = Assert.ContainsSingle(changes);
        Assert.AreEqual(NotifyCollectionChangedAction.Add, change.Action);
        Assert.AreEqual(2, change.NewItems?.Count);
        Assert.AreEqual(0, change.NewStartingIndex);
    }

    [TestMethod]
    public void RemoveRangePublishesOneCollectionChange()
    {
        TestMapElement first = new();
        TestMapElement second = new();
        TestMapElement third = new();
        MapElementCollection elements = [first, second, third];
        List<NotifyCollectionChangedEventArgs> changes = [];
        elements.CollectionChanged += (_, args) => changes.Add(args);

        elements.RemoveRange(1, 2);

        Assert.AreSequenceEqual([first], elements);
        NotifyCollectionChangedEventArgs change = Assert.ContainsSingle(changes);
        Assert.AreEqual(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.AreEqual(2, change.OldItems?.Count);
        Assert.AreEqual(1, change.OldStartingIndex);
    }

    [TestMethod]
    public void SpatialIndexReturnsOnlyNearbyIconsAtHighZoom()
    {
        MapIconSpatialIndex index = new();
        MapIconSnapshot nearby = new(1, 0, 0, 32, 32);
        MapIconSnapshot distant = new(1, 90, 0, 32, 32);
        index.Rebuild([nearby, distant]);

        MapIconSnapshot[] visible = index.GetVisible(0, 0, 10, 512, 512);

        Assert.AreSequenceEqual([nearby], visible);
    }

    [TestMethod]
    public void SpatialIndexUpdatesMovedIconsIncrementally()
    {
        MapIconSpatialIndex index = new();
        MapIconSnapshot nearby = new(1, 0, 0, 32, 32);
        MapIconSnapshot moved = new(2, 90, 0, 32, 32);
        index.Rebuild([nearby, moved]);
        moved = moved with { Longitude = 0.01 };

        index.Update([new MapIconSnapshotUpdate(1, moved)]);

        Assert.AreSequenceEqual(
            [nearby, moved],
            index.GetVisible(0, 0, 10, 512, 512));
    }

    [TestMethod]
    public void SpatialIndexDoesNotDuplicateWrappedCells()
    {
        MapIconSpatialIndex index = new();
        MapIconSnapshot icon = new(1, -179, 0, 32, 32);
        index.Rebuild([icon]);

        MapIconSnapshot[] visible = index.GetVisible(3.6, 0, 0, 221.44, 1);

        Assert.AreSequenceEqual([icon], visible);
    }

    [TestMethod]
    public void SpatialIndexHitTestReturnsTopmostVisibleIcon()
    {
        MapIconSpatialIndex index = new();
        index.Rebuild(
        [
            new MapIconSnapshot(1, 0, 0, 32, 32, LayerIndex: 0),
            new MapIconSnapshot(2, 0, 0, 32, 32, LayerIndex: 1),
        ]);
        bool[] visibleLayers = [true, true];

        Assert.IsTrue(index.TryHitTest(
            0, 0, 5, 256, 256, 128, 128, visibleLayers, out int iconIndex));
        Assert.AreEqual(1, iconIndex);

        visibleLayers[1] = false;

        Assert.IsTrue(index.TryHitTest(
            0, 0, 5, 256, 256, 128, 128, visibleLayers, out iconIndex));
        Assert.AreEqual(0, iconIndex);
    }

    [TestMethod]
    public void SpatialIndexHitTestSkipsDisabledIcons()
    {
        MapIconSpatialIndex index = new();
        index.Rebuild(
        [
            new MapIconSnapshot(
                1,
                0,
                0,
                32,
                32,
                ElementIndex: 10,
                OrderIndex: 0),
            new MapIconSnapshot(
                2,
                0,
                0,
                32,
                32,
                ElementIndex: 20,
                OrderIndex: 1,
                IsEnabled: false),
        ]);

        Assert.IsTrue(index.TryHitTest(
            0, 0, 5, 256, 256, 128, 128, [true], out int elementIndex));
        Assert.AreEqual(10, elementIndex);
    }

    [TestMethod]
    public void SpatialIndexHitTestRejectsPointsOutsideIcon()
    {
        MapIconSpatialIndex index = new();
        index.Rebuild([new MapIconSnapshot(1, 0, 0, 32, 32)]);

        Assert.IsFalse(index.TryHitTest(
            0, 0, 5, 256, 256, 10, 10, [true], out int iconIndex));
        Assert.AreEqual(-1, iconIndex);
    }

    [TestMethod]
    public void RasterDimensionsScalePixelsButPreserveLogicalSize()
    {
        MapIconRasterDimensions dimensions =
            MapIconRasterDimensions.Create(20, 24, 30, 36);

        Assert.AreEqual(20u, dimensions.LogicalWidth);
        Assert.AreEqual(24u, dimensions.LogicalHeight);
        Assert.AreEqual(30u, dimensions.PixelWidth);
        Assert.AreEqual(36u, dimensions.PixelHeight);
    }

    [TestMethod]
    public void DefaultIconSlotPreservesLogicalTextureSize()
    {
        MapIconRasterDimensions dimensions = MapIconRasterDimensions.Create(
            32,
            32,
            48,
            48);

        Assert.AreEqual(32u, dimensions.LogicalWidth);
        Assert.AreEqual(32u, dimensions.LogicalHeight);
        Assert.AreEqual(48u, dimensions.PixelWidth);
        Assert.AreEqual(48u, dimensions.PixelHeight);
    }

    private sealed record EqualValue(int Value);
    private sealed class TestMapElement : MapElement;
}
