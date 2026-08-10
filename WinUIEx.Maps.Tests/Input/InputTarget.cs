using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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

        AutomationPeer peer =
            FrameworkElementAutomationPeer.FromElement(element) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(element) ??
            throw new InvalidOperationException("Could not create an automation peer for the input target.");
        Windows.Foundation.Rect automationBounds = peer.GetBoundingRectangle();
        if (!Interop.GetWindowRect(new HWND(handle), out RECT windowBounds))
        {
            throw new InvalidOperationException("Could not get the WinUI test window bounds.");
        }

        var bounds = new InputBounds(
            (int)Math.Round(automationBounds.X),
            (int)Math.Round(automationBounds.Y),
            (int)Math.Round(automationBounds.Width),
            (int)Math.Round(automationBounds.Height));
        if (bounds.Left < windowBounds.left ||
            bounds.Top < windowBounds.top ||
            bounds.Left + bounds.Width > windowBounds.right ||
            bounds.Top + bounds.Height > windowBounds.bottom)
        {
            bounds = bounds with
            {
                Left = bounds.Left + windowBounds.left,
                Top = bounds.Top + windowBounds.top,
            };
        }

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
        if (!Interop.SetForegroundWindow(WindowHandle) ||
            Interop.GetForegroundWindow() != WindowHandle)
        {
            throw new InvalidOperationException("The WinUI test window could not receive foreground input.");
        }
    }

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
