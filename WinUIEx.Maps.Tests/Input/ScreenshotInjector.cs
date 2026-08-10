using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class ScreenshotInjector(InputTarget target)
{
    internal ScreenshotFrame Capture()
    {
        target.ActivateWindow();
        if (!Interop.GetWindowRect(target.WindowHandle, out RECT windowBounds))
        {
            throw new InvalidOperationException("Could not get the screenshot window bounds.");
        }

        Interop.DwmFlush();
        return new ScreenshotFrame(
            CaptureScreen(new InputBounds(
                windowBounds.left,
                windowBounds.top,
                windowBounds.right - windowBounds.left,
                windowBounds.bottom - windowBounds.top)),
            windowBounds.right - windowBounds.left,
            windowBounds.bottom - windowBounds.top);
    }

    internal async Task<string> SaveAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ScreenshotFrame frame = Capture();
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("The screenshot path has no parent directory.");
        Directory.CreateDirectory(directory);

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
        StorageFile file = await folder.CreateFileAsync(
            Path.GetFileName(fullPath),
            CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)frame.Width,
            (uint)frame.Height,
            96,
            96,
            frame.Pixels);
        await encoder.FlushAsync();
        return fullPath;
    }

    private static unsafe byte[] CaptureScreen(InputBounds bounds)
    {
        HDC screenDc = Interop.GetDC(HWND.Null);
        if (screenDc.IsNull)
        {
            throw new InvalidOperationException("Could not acquire the screen device context.");
        }

        try
        {
            HDC memoryDc = Interop.CreateCompatibleDC(screenDc);
            if (memoryDc.IsNull)
            {
                throw new InvalidOperationException("Could not create a screenshot device context.");
            }

            try
            {
                HBITMAP bitmap = Interop.CreateCompatibleBitmap(
                    screenDc,
                    bounds.Width,
                    bounds.Height);
                if (bitmap.IsNull)
                {
                    throw new InvalidOperationException("Could not create a screenshot bitmap.");
                }

                try
                {
                    HGDIOBJ previous = Interop.SelectObject(memoryDc, *(HGDIOBJ*)&bitmap);
                    try
                    {
                        if (!Interop.BitBlt(
                            memoryDc,
                            0,
                            0,
                            bounds.Width,
                            bounds.Height,
                            screenDc,
                            bounds.Left,
                            bounds.Top,
                            ROP_CODE.SRCCOPY))
                        {
                            throw new InvalidOperationException("Could not copy the UI target from the screen.");
                        }
                    }
                    finally
                    {
                        Interop.SelectObject(memoryDc, previous);
                    }

                    return ExtractPixels(screenDc, bitmap, bounds.Width, bounds.Height);
                }
                finally
                {
                    Interop.DeleteObject(*(HGDIOBJ*)&bitmap);
                }
            }
            finally
            {
                Interop.DeleteDC(memoryDc);
            }
        }
        finally
        {
            Interop.ReleaseDC(HWND.Null, screenDc);
        }
    }

    private static unsafe byte[] ExtractPixels(
        HDC deviceContext,
        HBITMAP bitmap,
        int width,
        int height)
    {
        var bitmapInfo = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        byte[] pixels = new byte[checked(width * height * 4)];
        fixed (byte* pixelPointer = pixels)
        {
            int rows = Interop.GetDIBits(
                deviceContext,
                bitmap,
                0,
                (uint)height,
                pixelPointer,
                &bitmapInfo,
                DIB_USAGE.DIB_RGB_COLORS);
            if (rows != height)
            {
                throw new InvalidOperationException(
                    $"Screenshot extraction returned {rows} of {height} rows.");
            }
        }

        return pixels;
    }
}

internal sealed record ScreenshotFrame(byte[] Pixels, int Width, int Height);
