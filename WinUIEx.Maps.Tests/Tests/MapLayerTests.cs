using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx.Maps.Rendering;
using Windows.Devices.Geolocation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapLayerTests
{
    [TestMethod]
    public void PublicApiMatchesLayerPatternAndRemovesControlMapElements()
    {
        PropertyInfo layers = Assert.ContainsSingle(
            property => property.Name == nameof(MapControl.Layers),
            typeof(MapControl).GetProperties());
        Assert.AreEqual(typeof(MapLayerCollection), layers.PropertyType);
        Assert.IsTrue(layers.CanRead);
        Assert.IsTrue(layers.CanWrite);
        AssertDependencyPropertyIdentifier(
            typeof(MapControl),
            nameof(MapControl.LayersProperty));
        Assert.IsNull(typeof(MapControl).GetProperty("MapElements"));

        ConstructorInfo constructor = Assert.ContainsSingle(typeof(MapElementsLayer).GetConstructors());
        Assert.IsEmpty(constructor.GetParameters());
        PropertyInfo mapElements = typeof(MapElementsLayer).GetProperty(
            nameof(MapElementsLayer.MapElements))!;
        Assert.AreEqual(typeof(MapElementCollection), mapElements.PropertyType);
        Assert.IsTrue(mapElements.CanRead);
        Assert.IsTrue(mapElements.CanWrite);
        AssertDependencyPropertyIdentifier(
            typeof(MapElementsLayer),
            nameof(MapElementsLayer.MapElementsProperty));
        AssertDependencyPropertyIdentifier(
            typeof(MapLayer),
            nameof(MapLayer.AttributionProperty));
        AssertDependencyPropertyIdentifier(
            typeof(MapLayer),
            nameof(MapLayer.AttributionLinkProperty));

        PropertyInfo attribution = typeof(MapLayer).GetProperty(
            nameof(MapLayer.Attribution))!;
        Assert.AreEqual(typeof(string), attribution.PropertyType);
        Assert.IsTrue(attribution.CanRead);
        Assert.IsTrue(attribution.CanWrite);

        PropertyInfo attributionLink = typeof(MapLayer).GetProperty(
            nameof(MapLayer.AttributionLink))!;
        Assert.AreEqual(typeof(Uri), attributionLink.PropertyType);
        Assert.IsTrue(attributionLink.CanRead);
        Assert.IsTrue(attributionLink.CanWrite);
    }

    [TestMethod]
    public void MapLayerIsDependencyObjectWhileElementsRemainLightweight()
    {
        Assert.IsTrue(typeof(DependencyObject).IsAssignableFrom(typeof(MapLayer)));
        Assert.IsTrue(typeof(MapLayer).IsAssignableFrom(typeof(MapElementsLayer)));
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapElement)));
        Assert.IsFalse(typeof(DependencyObject).IsAssignableFrom(typeof(MapIcon)));
    }

    [TestMethod]
    public void CollectionDefaultsAreCreatedPerInstanceThroughSetValue()
    {
        AssertConstructorCreatesCollectionThroughSetValue(
            typeof(MapControl),
            typeof(MapLayerCollection));
        AssertConstructorCreatesCollectionThroughSetValue(
            typeof(MapElementsLayer),
            typeof(MapElementCollection));
    }

    [TestMethod]
    public void LayerAndElementCollectionsRejectNullAndDuplicateReferences()
    {
        MapLayerCollection layers = [];
        MapLayer layer = (MapLayer)RuntimeHelpers.GetUninitializedObject(typeof(MapLayer));
        layers.Add(layer);
        Assert.ThrowsExactly<ArgumentException>(() => layers.Add(layer));
        Assert.ThrowsExactly<ArgumentNullException>(() => layers.Add(null!));

        MapElementCollection elements = [];
        TestMapElement element = new();
        elements.Add(element);
        Assert.ThrowsExactly<ArgumentException>(() => elements.Add(element));
        Assert.ThrowsExactly<ArgumentNullException>(() => elements.Add(null!));
    }

    [TestMethod]
    public void RangeOperationsPublishOneChangeAtStressScale()
    {
        const int count = 100_000;
        MapElementCollection elements = [];
        int notifications = 0;
        NotifyCollectionChangedEventArgs? lastChange = null;
        elements.CollectionChanged += (_, args) =>
        {
            notifications++;
            lastChange = args;
        };
        TestMapElement[] added = Enumerable.Range(0, count)
            .Select(_ => new TestMapElement())
            .ToArray();

        elements.AddRange(added);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(count, lastChange?.NewItems?.Count);

        elements.RemoveRange(0, count);
        Assert.AreEqual(2, notifications);
        Assert.AreEqual(count, lastChange?.OldItems?.Count);
        Assert.IsEmpty(elements);
    }

    [TestMethod]
    public void MoveRaisesPreMutationNotificationForBothCollectionTypes()
    {
        MapElementCollection elements = [new TestMapElement(), new TestMapElement()];
        MapLayerCollection layers =
        [
            (MapLayer)RuntimeHelpers.GetUninitializedObject(typeof(MapLayer)),
            (MapLayer)RuntimeHelpers.GetUninitializedObject(typeof(MapLayer)),
        ];
        int elementChanging = 0;
        int layerChanging = 0;
        elements.Changing += (_, _) => elementChanging++;
        layers.Changing += (_, _) => layerChanging++;

        elements.Move(0, 1);
        layers.Move(0, 1);

        Assert.AreEqual(1, elementChanging);
        Assert.AreEqual(1, layerChanging);
    }

    [TestMethod]
    public void SpatialIndexReturnsBottomToTopLayerOrder()
    {
        MapIconSpatialIndex index = new();
        MapIconSnapshot top = new(7, 0, 0, 32, 32, 3);
        MapIconSnapshot bottom = new(7, 0, 0, 32, 32, 0);
        MapIconSnapshot middle = new(8, 0, 0, 32, 32, 2);
        index.Rebuild([top, bottom, middle]);

        MapIconSnapshot[] visible = index.GetVisible(0, 0, 10, 512, 512);

        Assert.AreSequenceEqual([bottom, middle, top], visible);
        Assert.AreEqual(
            3,
            visible.Select(icon => (icon.LayerIndex, icon.TextureId)).Distinct().Count());
    }

    private static Geopoint CreateLocation() => new(new BasicGeoposition());

    private static void AssertDependencyPropertyIdentifier(Type ownerType, string fieldName)
    {
        FieldInfo field = ownerType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)!;
        Assert.IsNotNull(field);
        Assert.AreEqual(typeof(DependencyProperty), field.FieldType);
        Assert.IsTrue(field.IsInitOnly);
    }

    private static void AssertConstructorCreatesCollectionThroughSetValue(
        Type ownerType,
        Type collectionType)
    {
        ConstructorInfo constructor = Assert.ContainsSingle(
            candidate => candidate.GetParameters().Length == 0,
            ownerType.GetConstructors());
        MethodBase[] referencedMethods = GetReferencedMethods(constructor).ToArray();

        Assert.Contains(
            method => method is ConstructorInfo &&
                method.DeclaringType == collectionType &&
                method.GetParameters().Length == 0,
            referencedMethods);
        Assert.Contains(
            method => method.Name == nameof(DependencyObject.SetValue) &&
                method.DeclaringType == typeof(DependencyObject),
            referencedMethods);
    }

    private static IEnumerable<MethodBase> GetReferencedMethods(MethodBase method)
    {
        byte[] instructions = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int index = 0; index <= instructions.Length - sizeof(int) - 1; index++)
        {
            byte opcode = instructions[index];
            if (opcode is not (0x28 or 0x6F or 0x73))
            {
                continue;
            }

            int token = BitConverter.ToInt32(instructions, index + 1);
            MethodBase? referencedMethod;
            try
            {
                referencedMethod = method.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (referencedMethod is not null)
            {
                yield return referencedMethod;
            }
        }
    }

    private sealed class TestMapElement : MapElement;
}
