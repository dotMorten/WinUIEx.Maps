using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class InputTarget
{
    private InputTarget(HWND windowHandle, InputBounds bounds)
    {
        WindowHandle = windowHandle;
        Bounds = bounds;
    }

    internal HWND WindowHandle { get; }

    internal InputBounds Bounds { get; }

    internal InputPoint Center => Bounds.Center;

    internal InputPoint PointAt(double horizontalFraction, double verticalFraction)
    {
        if (horizontalFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(horizontalFraction));
        }

        if (verticalFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFraction));
        }

        return new InputPoint(
            Bounds.Left + (int)Math.Round(Bounds.Width * horizontalFraction),
            Bounds.Top + (int)Math.Round(Bounds.Height * verticalFraction));
    }

    internal static InputTarget FromElement(Window window, FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(element);

        element.UpdateLayout();
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (handle == 0)
        {
            throw new InvalidOperationException("The WinUI test window does not have a native handle.");
        }

        // UIA can return root-relative rectangles for the self-hosted WinUI tree.
        // Whether such a rectangle happens to fit inside the screen window is not
        // a reliable way to distinguish it from a screen-relative rectangle.
        Windows.Foundation.Point offset = element.TransformToVisual(null)
            .TransformPoint(default);
        double scale = element.XamlRoot.RasterizationScale;
        var clientOrigin = new System.Drawing.Point();
        if (!ClientToScreen(handle, ref clientOrigin))
        {
            throw new InvalidOperationException("Could not get the test window client origin.");
        }
        if (!Interop.GetWindowRect(new HWND(handle), out RECT windowBounds))
        {
            throw new InvalidOperationException("Could not get the WinUI test window bounds.");
        }

        var bounds = new InputBounds(
            clientOrigin.X + (int)Math.Round(offset.X * scale),
            clientOrigin.Y + (int)Math.Round(offset.Y * scale),
            (int)Math.Round(element.ActualWidth * scale),
            (int)Math.Round(element.ActualHeight * scale));

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("The target element has not been arranged.");
        }

        if (bounds.Center.X < windowBounds.left ||
            bounds.Center.X >= windowBounds.right ||
            bounds.Center.Y < windowBounds.top ||
            bounds.Center.Y >= windowBounds.bottom)
        {
            throw new InvalidOperationException(
                $"The input target center ({bounds.Center.X}, {bounds.Center.Y}) is outside the " +
                $"test window ({windowBounds.left}, {windowBounds.top}, " +
                $"{windowBounds.right}, {windowBounds.bottom}).");
        }

        return new InputTarget(new HWND(handle), bounds);
    }

    internal void ActivateWindow()
    {
        if (Interop.GetForegroundWindow() == WindowHandle)
        {
            return;
        }
        Interop.SetForegroundWindow(WindowHandle);
        if (Interop.GetForegroundWindow() != WindowHandle)
        {
            throw new InvalidOperationException("The WinUI test window could not receive foreground input.");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref System.Drawing.Point point);

    internal void VerifyPoint(InputPoint point)
    {
        HWND pointWindow = Interop.WindowFromPoint(new System.Drawing.Point(point.X, point.Y));
        HWND rootWindow = Interop.GetAncestor(pointWindow, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (rootWindow != WindowHandle)
        {
            throw new InvalidOperationException(
                $"Screen point ({point.X}, {point.Y}) is not over the WinUI test window.");
        }
    }
}
