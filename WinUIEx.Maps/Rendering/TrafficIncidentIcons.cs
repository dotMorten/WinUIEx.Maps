using System.Text.Json;

namespace WinUIEx.Maps.Rendering;

/// <summary>Original, font-independent pictograms shared by all Azure incident tiles.</summary>
internal static class TrafficIncidentIcons
{
    internal const string Identity = "azure-incident-signs-v1";
    internal const int Size = 32;
    private const int PixelRatio = 2;
    private const int TextureSize = Size * PixelRatio;
    private static readonly string[] Names =
    [
        "warning", "accident", "fog", "danger", "rain", "ice", "congestion",
        "lane-closed", "road-closed", "roadworks", "wind", "flooding", "detour",
        "cluster", "breakdown",
    ];
    private static readonly Lazy<VectorSpriteAtlas> SharedAtlas = new(CreateAtlas);
    internal static VectorSpriteAtlas Atlas => SharedAtlas.Value;

    internal static bool Contains(double offsetX, double offsetY)
    {
        double y = offsetY + Size / 2d;
        return y >= 0 && y <= Size && Math.Abs(offsetX) <= y / 2;
    }

    internal static void WriteImageExpression(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        writer.WriteStringValue("case");
        writer.WriteRawValue("""["==",["get","magnitude"],3]""");
        WriteCategoryExpression(writer, major: true);
        WriteCategoryExpression(writer, major: false);
        writer.WriteEndArray();
    }

    private static void WriteCategoryExpression(Utf8JsonWriter writer, bool major)
    {
        // Azure's IconCategory enum (including 14: Broken Down Vehicle):
        // https://learn.microsoft.com/rest/api/maps/traffic/get-traffic-incident-detail#iconcategory
        // Render does not publish its MVT schema; unrecognized values remain generic warnings.
        writer.WriteStartArray();
        writer.WriteStringValue("match");
        writer.WriteRawValue("""["number",["get","icon_category"],["get","icon_category_0"],0]""");
        for (int category = 0; category < Names.Length; category++)
        {
            writer.WriteNumberValue(category);
            writer.WriteStringValue(Names[category] + (major ? "-major" : ""));
        }
        writer.WriteStringValue(Names[0] + (major ? "-major" : ""));
        writer.WriteEndArray();
    }

    private static VectorSpriteAtlas CreateAtlas()
    {
        int width = TextureSize * Names.Length * 2;
        byte[] pixels = new byte[width * TextureSize * 4];
        Dictionary<string, VectorSpriteEntry> entries = new(StringComparer.Ordinal);
        for (int index = 0; index < Names.Length * 2; index++)
        {
            int category = index % Names.Length;
            bool major = index >= Names.Length;
            Painter painter = new(major ? White : Ink, major ? Red : Yellow);
            Draw(painter, category);
            byte[] icon = painter.Downsample();
            for (int row = 0; row < TextureSize; row++)
                Buffer.BlockCopy(icon, row * TextureSize * 4, pixels,
                    (row * width + index * TextureSize) * 4, TextureSize * 4);
            entries.Add(Names[category] + (major ? "-major" : ""), new((uint)(index * TextureSize), 0,
                TextureSize, TextureSize, PixelRatio, true));
        }
        return new(Identity, entries, pixels, (uint)width, TextureSize);
    }

    private const uint Ink = 0xff202c38;
    private const uint White = 0xffffffff;
    private const uint Yellow = 0xffffd54f;
    private const uint Red = 0xffd83b3b;

    private static void Draw(Painter p, int category)
    {
        p.Triangle(16, 0, 32, 32, 0, 32, White);
        p.Triangle(16, 1, 31, 31, 1, 31, Ink);
        p.Triangle(16, 2.5, 29, 29, 3, 29, p.Background);
        p.GlyphTransform = true;
        switch (category)
        {
            case 0:
                p.Line(12, 6, 12, 13, 2.4);
                p.Circle(12, 17, 1.3, p.Foreground);
                break;
            case 1: // Two vehicles and an impact burst.
                Car(p, 5, 13, 6);
                Car(p, 13, 13, 6);
                p.Line(12, 5, 11, 8);
                p.Line(11, 8, 14, 8);
                p.Line(14, 8, 12, 11);
                p.Line(7, 7, 9, 9);
                p.Line(17, 7, 15, 9);
                break;
            case 2:
                Cloud(p, 7);
                p.Line(6, 13, 18, 13);
                p.Line(8, 16, 16, 16);
                p.Line(6, 19, 18, 19);
                break;
            case 3:
                Car(p, 8, 5, 8);
                p.Line(9, 12, 7, 14);
                p.Line(7, 14, 10, 16);
                p.Line(10, 16, 8, 19);
                p.Line(15, 12, 13, 14);
                p.Line(13, 14, 16, 16);
                p.Line(16, 16, 14, 19);
                break;
            case 4:
                Cloud(p, 8);
                p.Line(8, 14, 6.5, 17);
                p.Line(12, 14, 10.5, 18);
                p.Line(16, 14, 14.5, 17);
                break;
            case 5:
                for (int arm = 0; arm < 6; arm++)
                {
                    double angle = arm * Math.PI / 3;
                    double x = Math.Cos(angle), y = Math.Sin(angle);
                    p.Line(12, 12, 12 + 7 * x, 12 + 7 * y, 1.3);
                    p.Line(12 + 4 * x, 12 + 4 * y,
                        12 + 5.5 * x - 1.8 * y, 12 + 5.5 * y + 1.8 * x, 1.1);
                    p.Line(12 + 4 * x, 12 + 4 * y,
                        12 + 5.5 * x + 1.8 * y, 12 + 5.5 * y - 1.8 * x, 1.1);
                }
                break;
            case 6:
                Car(p, 9, 5, 6);
                Car(p, 5, 12, 6);
                Car(p, 13, 12, 6);
                break;
            case 7:
                p.Line(8, 18, 8, 6);
                p.Line(8, 6, 5.5, 9);
                p.Line(8, 6, 10.5, 9);
                p.Line(16, 18, 16, 14);
                p.Line(16, 14, 11, 10);
                p.Line(14, 6, 18, 10, 2);
                p.Line(18, 6, 14, 10, 2);
                break;
            case 8:
                p.Circle(12, 12, 8, p.Foreground);
                p.Line(7, 12, 17, 12, 3, p.Background);
                break;
            case 9: // Worker with a shovel.
                p.Circle(10, 6, 1.5, p.Foreground);
                p.Line(10, 9, 12, 13, 2.4);
                p.Line(12, 13, 8, 19, 2);
                p.Line(12, 13, 15, 19, 2);
                p.Line(10.5, 10, 15, 12);
                p.Line(14, 8, 17, 17, 1.2);
                p.Line(16.5, 17, 19, 18, 2.4);
                break;
            case 10:
                p.Line(7, 6, 7, 19);
                p.Line(7, 7, 18, 9);
                p.Line(18, 9, 18, 12);
                p.Line(18, 12, 7, 12);
                p.Line(10, 8, 10, 11, 2.5);
                p.Line(15, 9, 15, 11, 2);
                p.Line(5, 19, 10, 19);
                break;
            case 11:
                Car(p, 7, 6, 10);
                Wave(p, 15);
                Wave(p, 18);
                break;
            case 12:
                p.Line(6, 18, 6, 9);
                p.Line(6, 9, 17, 9);
                p.Line(17, 9, 14, 6);
                p.Line(17, 9, 14, 12);
                p.Line(12, 14, 17, 19, 1.5);
                p.Line(17, 14, 12, 19, 1.5);
                break;
            case 13:
                p.Circle(9, 9, 4, p.Foreground);
                p.Circle(9, 9, 2.4, p.Background);
                p.Circle(15, 10, 4, p.Foreground);
                p.Circle(15, 10, 2.4, p.Background);
                p.Circle(12, 16, 4, p.Foreground);
                p.Circle(12, 16, 2.4, p.Background);
                break;
            case 14:
                Car(p, 6, 12, 12);
                p.Line(6, 12, 4.5, 8);
                p.Line(17, 5, 17, 8, 1.7);
                p.Circle(17, 10, 0.9, p.Foreground);
                break;
        }
    }

    private static void Car(Painter p, double x, double y, double width)
    {
        p.Line(x, y + 3, x + width, y + 3, 2.5);
        p.Line(x + 1, y + 2, x + 2, y);
        p.Line(x + 2, y, x + width - 2, y);
        p.Line(x + width - 2, y, x + width - 1, y + 2);
        p.Circle(x + 1.2, y + 5, 0.9, p.Foreground);
        p.Circle(x + width - 1.2, y + 5, 0.9, p.Foreground);
    }

    private static void Cloud(Painter p, double y)
    {
        p.Circle(8, y + 1, 2.5, p.Foreground);
        p.Circle(11.5, y - 0.5, 3.3, p.Foreground);
        p.Circle(15.5, y + 1, 2.8, p.Foreground);
        p.Line(8, y + 2, 16, y + 2, 2);
    }

    private static void Wave(Painter p, double y)
    {
        for (int x = 5; x < 19; x += 2)
            p.Line(x, y + (x % 4 == 1 ? 0 : 1), x + 2, y + (x % 4 == 1 ? 1 : 0), 1.3);
    }

    private sealed class Painter(uint foreground, uint background)
    {
        internal uint Foreground => foreground;
        internal uint Background => background;
        private const int Scale = 4;
        private const int Width = Size * Scale;
        private readonly uint[] _pixels = new uint[Width * Width];
        internal bool GlyphTransform { get; set; }

        internal void Circle(double cx, double cy, double radius, uint color)
        {
            if (GlyphTransform)
            {
                cx = 16 + (cx - 12) * 0.85;
                cy = 20 + (cy - 12) * 0.85;
                radius *= 0.85;
            }
            Paint(cx - radius, cy - radius, cx + radius, cy + radius,
                (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius, color);
        }

        internal void Triangle(double ax, double ay, double bx, double by,
            double cx, double cy, uint color) =>
            Paint(Math.Min(ax, Math.Min(bx, cx)), Math.Min(ay, Math.Min(by, cy)),
                Math.Max(ax, Math.Max(bx, cx)), Math.Max(ay, Math.Max(by, cy)), (x, y) =>
                    (bx - ax) * (y - ay) - (by - ay) * (x - ax) >= 0 &&
                    (cx - bx) * (y - by) - (cy - by) * (x - bx) >= 0 &&
                    (ax - cx) * (y - cy) - (ay - cy) * (x - cx) >= 0, color);

        internal void Line(double ax, double ay, double bx, double by,
            double width = 1.5, uint? color = null)
        {
            if (GlyphTransform)
            {
                ax = 16 + (ax - 12) * 0.85;
                ay = 20 + (ay - 12) * 0.85;
                bx = 16 + (bx - 12) * 0.85;
                by = 20 + (by - 12) * 0.85;
                width *= 0.85;
            }
            double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
            double radius = width / 2;
            Paint(Math.Min(ax, bx) - radius, Math.Min(ay, by) - radius,
                Math.Max(ax, bx) + radius, Math.Max(ay, by) + radius, (x, y) =>
                {
                    double t = length == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / length, 0, 1);
                    double px = ax + t * dx - x, py = ay + t * dy - y;
                    return px * px + py * py <= radius * radius;
                }, color ?? Foreground);
        }

        private void Paint(double left, double top, double right, double bottom,
            Func<double, double, bool> contains, uint color)
        {
            for (int y = Math.Max(0, (int)(top * Scale)); y < Math.Min(Width, Math.Ceiling(bottom * Scale)); y++)
            for (int x = Math.Max(0, (int)(left * Scale)); x < Math.Min(Width, Math.Ceiling(right * Scale)); x++)
                if (contains((x + 0.5) / Scale, (y + 0.5) / Scale))
                    _pixels[y * Width + x] = color;
        }

        internal byte[] Downsample()
        {
            byte[] result = new byte[TextureSize * TextureSize * 4];
            for (int y = 0; y < TextureSize; y++)
            for (int x = 0; x < TextureSize; x++)
            {
                int b = 0, g = 0, r = 0, count = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    uint pixel = _pixels[(y * 2 + dy) * Width + x * 2 + dx];
                    if (pixel == 0) continue;
                    b += (int)(pixel & 255);
                    g += (int)((pixel >> 8) & 255);
                    r += (int)((pixel >> 16) & 255);
                    count++;
                }
                if (count == 0) continue;
                int offset = (y * TextureSize + x) * 4;
                result[offset] = (byte)(b / 4);
                result[offset + 1] = (byte)(g / 4);
                result[offset + 2] = (byte)(r / 4);
                result[offset + 3] = (byte)(255 * count / 4);
            }
            return result;
        }
    }
}
