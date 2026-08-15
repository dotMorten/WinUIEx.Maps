using WinUIEx.Maps.Tests.Input;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal delegate bool PixelColorFilter(
    byte red,
    byte green,
    byte blue,
    byte alpha);

internal static class ConnectedComponentAnalyzer
{
    private static readonly (int X, int Y)[] Neighbors =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),           (1, 0),
        (-1, 1),  (0, 1),  (1, 1),
    ];

    internal static ConnectedComponent[] Find(
        ScreenshotFrame frame,
        PixelColorFilter filter,
        int minimumPixelCount = 1) =>
        Find(
            frame.Pixels,
            frame.Width,
            frame.Height,
            filter,
            minimumPixelCount);

    internal static ConnectedComponent[] Find(
        MapRenderFrame frame,
        PixelColorFilter filter,
        int minimumPixelCount = 1) =>
        Find(
            frame.Pixels,
            frame.Width,
            frame.Height,
            filter,
            minimumPixelCount);

    private static ConnectedComponent[] Find(
        ReadOnlyMemory<byte> pixels,
        int width,
        int height,
        PixelColorFilter filter,
        int minimumPixelCount = 1)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (minimumPixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPixelCount));
        }
        int pixelCount = checked(width * height);
        if (pixels.Length != checked(pixelCount * 4))
        {
            throw new ArgumentException(
                "The pixel buffer does not match its dimensions.",
                nameof(pixels));
        }

        ReadOnlySpan<byte> pixelSpan = pixels.Span;
        bool[] matching = new bool[pixelCount];
        for (int index = 0; index < pixelCount; index++)
        {
            int offset = index * 4;
            matching[index] = filter(
                pixelSpan[offset + 2],
                pixelSpan[offset + 1],
                pixelSpan[offset],
                pixelSpan[offset + 3]);
        }

        bool[] visited = new bool[pixelCount];
        Queue<int> pending = new();
        List<ConnectedComponent> components = [];
        for (int start = 0; start < pixelCount; start++)
        {
            if (!matching[start] || visited[start])
            {
                continue;
            }
            visited[start] = true;
            pending.Enqueue(start);
            int left = width;
            int top = height;
            int right = -1;
            int bottom = -1;
            int count = 0;
            while (pending.TryDequeue(out int index))
            {
                int x = index % width;
                int y = index / width;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                count++;
                foreach ((int offsetX, int offsetY) in Neighbors)
                {
                    int neighborX = x + offsetX;
                    int neighborY = y + offsetY;
                    if ((uint)neighborX >= (uint)width ||
                        (uint)neighborY >= (uint)height)
                    {
                        continue;
                    }
                    int neighbor = (neighborY * width) + neighborX;
                    if (matching[neighbor] && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        pending.Enqueue(neighbor);
                    }
                }
            }
            if (count >= minimumPixelCount)
            {
                components.Add(new ConnectedComponent(
                    new PixelBounds(
                        left,
                        top,
                        right - left + 1,
                        bottom - top + 1),
                    count));
            }
        }

        return
        [
            .. components
                .OrderByDescending(component => component.PixelCount)
                .ThenBy(component => component.Bounds.Top)
                .ThenBy(component => component.Bounds.Left),
        ];
    }

    internal static PixelColorFilter Near(
        byte red,
        byte green,
        byte blue,
        byte tolerance = 0,
        byte minimumAlpha = 1) =>
        (candidateRed, candidateGreen, candidateBlue, alpha) =>
            alpha >= minimumAlpha &&
            Math.Abs(candidateRed - red) <= tolerance &&
            Math.Abs(candidateGreen - green) <= tolerance &&
            Math.Abs(candidateBlue - blue) <= tolerance;
}

internal readonly record struct ConnectedComponent(
    PixelBounds Bounds,
    int PixelCount);

internal readonly record struct PixelBounds(
    int Left,
    int Top,
    int Width,
    int Height)
{
    internal int Right => Left + Width;

    internal int Bottom => Top + Height;

    internal double CenterX => Left + (Width / 2d);

    internal double CenterY => Top + (Height / 2d);

    internal bool Contains(PixelBounds other) =>
        other.Left >= Left &&
        other.Top >= Top &&
        other.Right <= Right &&
        other.Bottom <= Bottom;
}
