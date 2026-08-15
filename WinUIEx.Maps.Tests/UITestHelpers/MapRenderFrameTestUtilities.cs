using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests.UITestHelpers;

internal static class MapRenderFrameTestUtilities
{
    internal static async Task<string> SavePngAsync(
        this MapRenderFrame frame,
        string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "The render-frame path has no parent directory.");
        Directory.CreateDirectory(directory);

        StorageFolder folder =
            await StorageFolder.GetFolderFromPathAsync(directory);
        StorageFile file = await folder.CreateFileAsync(
            Path.GetFileName(fullPath),
            CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream =
            await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)frame.Width,
            (uint)frame.Height,
            96,
            96,
            frame.Pixels.ToArray());
        await encoder.FlushAsync();
        return fullPath;
    }
}
