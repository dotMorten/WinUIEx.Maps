using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureGlyphRangeDecoderTests
{
    [TestMethod]
    public void DecodesMapboxGlyphMetricsAndBitmap()
    {
        byte[] encoded = FontStack(
            "Roboto-Regular",
            "0-255",
            Glyph(
                65,
                Enumerable.Range(0, 64).Select(value => (byte)value).ToArray(),
                2,
                2,
                -1,
                3,
                4));

        AzureGlyphRange range = AzureGlyphRangeDecoder.Decode(
            encoded,
            "Roboto-Regular",
            0);
        AzureGlyph glyph = range.Glyphs[65];

        Assert.AreEqual(-1, glyph.Left);
        Assert.AreEqual(3, glyph.Top);
        Assert.AreEqual(4u, glyph.Advance);
        Assert.HasCount(64, glyph.Bitmap);
    }

    [TestMethod]
    public void RejectsBitmapThatDoesNotMatchGlyphDimensions()
    {
        byte[] encoded = FontStack(
            "Roboto-Regular",
            "0-255",
            Glyph(65, [1, 2, 3], 2, 2, 0, 2, 4));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            AzureGlyphRangeDecoder.Decode(encoded, "Roboto-Regular", 0));
    }

    [TestMethod]
    public void AcceptsAdvanceOnlyWhitespaceGlyph()
    {
        byte[] encoded = FontStack(
            "Roboto-Regular",
            "0-255",
            Glyph(32, [], 0, 0, 0, 0, 6));

        AzureGlyph glyph = AzureGlyphRangeDecoder.Decode(
            encoded,
            "Roboto-Regular",
            0).Glyphs[32];

        Assert.AreEqual(6u, glyph.Advance);
        Assert.IsEmpty(glyph.Bitmap);
        Assert.AreEqual(0u, glyph.TextureWidth);
    }

    [TestMethod]
    public void GlyphTextureIdentityIncludesFontAndCodePoint()
    {
        long first = AzureGlyphAtlas.CreateTextureId("road", "Roboto-Regular", 65);

        Assert.AreNotEqual(
            first,
            AzureGlyphAtlas.CreateTextureId("road", "Roboto-Medium", 65));
        Assert.AreNotEqual(
            first,
            AzureGlyphAtlas.CreateTextureId("road", "Roboto-Regular", 66));
    }

    private static byte[] FontStack(
        string name,
        string range,
        byte[] glyph)
    {
        List<byte> stack = [];
        WriteBytes(stack, 1, Encoding.UTF8.GetBytes(name));
        WriteBytes(stack, 2, Encoding.UTF8.GetBytes(range));
        WriteBytes(stack, 3, glyph);
        List<byte> root = [];
        WriteBytes(root, 1, stack.ToArray());
        return root.ToArray();
    }

    private static byte[] Glyph(
        int id,
        byte[] bitmap,
        uint width,
        uint height,
        int left,
        int top,
        uint advance)
    {
        List<byte> glyph = [];
        WriteVarint(glyph, 1, unchecked((uint)id));
        WriteBytes(glyph, 2, bitmap);
        WriteVarint(glyph, 3, width);
        WriteVarint(glyph, 4, height);
        WriteVarint(glyph, 5, EncodeZigZag(left));
        WriteVarint(glyph, 6, EncodeZigZag(top));
        WriteVarint(glyph, 7, advance);
        return glyph.ToArray();
    }

    private static void WriteBytes(List<byte> destination, int field, byte[] value)
    {
        WriteRawVarint(destination, (ulong)((field << 3) | 2));
        WriteRawVarint(destination, (ulong)value.Length);
        destination.AddRange(value);
    }

    private static void WriteVarint(
        List<byte> destination,
        int field,
        uint value)
    {
        WriteRawVarint(destination, (ulong)(field << 3));
        WriteRawVarint(destination, value);
    }

    private static void WriteRawVarint(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)(value | 0x80));
            value >>= 7;
        }
        destination.Add((byte)value);
    }

    private static uint EncodeZigZag(int value) =>
        unchecked((uint)((value << 1) ^ (value >> 31)));
}
