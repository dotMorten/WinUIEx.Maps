using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class MouseInputInjector(InputTarget target)
{
    private const uint MouseWheelMessage = 0x020A;

    internal InputPoint Center => target.Center;

    internal void Click() => Click(target.Center);

    internal void DoubleClick() => DoubleClick(target.Center);

    internal Task WheelAsync(int delta) => WheelAsync(target.Center, delta);

    internal void MoveTo(InputPoint point)
    {
        target.ActivateWindow();
        target.VerifyPoint(point);
        NativeInput.Send(CreateMoveInput(point));
        Thread.Sleep(30);
    }

    internal void RightClick(InputPoint point)
    {
        Prepare(point);
        NativeInput.Send(
            CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN),
            CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP));
    }

    internal Task DragAsync(
        InputPoint start,
        InputPoint end,
        int durationMilliseconds = 250)
    {
        target.ActivateWindow();
        if (!Interop.SetCursorPos(start.X, start.Y))
        {
            throw new InvalidOperationException("Could not position the mouse at the drag start.");
        }

        target.VerifyPoint(start);
        target.VerifyPoint(end);
        return Task.Run(() =>
        {
            NativeInput.Send(CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN));
            bool released = false;
            try
            {
                const int steps = 20;
                for (int step = 1; step <= steps; step++)
                {
                    Thread.Sleep(Math.Max(1, durationMilliseconds / steps));
                    double progress = step / (double)steps;
                    int x = start.X + (int)Math.Round((end.X - start.X) * progress);
                    int y = start.Y + (int)Math.Round((end.Y - start.Y) * progress);
                    NativeInput.Send(CreateMoveInput(new InputPoint(x, y)));
                }

                NativeInput.Send(CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
                released = true;
            }
            finally
            {
                if (!released)
                {
                    try
                    {
                        NativeInput.Send(
                            CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        });
    }

    internal void Click(InputPoint point)
    {
        Prepare(point);
        SendClick();
    }

    internal void DoubleClick(InputPoint point)
    {
        Prepare(point);
        SendClick();
        Thread.Sleep(50);
        SendClick();
    }

    internal async Task WheelAsync(InputPoint point, int delta)
    {
        target.ActivateWindow();
        if (!Interop.SetCursorPos(point.X, point.Y))
        {
            throw new InvalidOperationException("Could not position the mouse over the input target.");
        }

        target.VerifyPoint(point);
        // WM_MOUSEWHEEL targets the focus HWND while carrying screen coordinates for hit testing.
        HWND focusWindow = Interop.GetFocus();
        if (focusWindow == HWND.Null)
        {
            focusWindow = target.WindowHandle;
        }
        uint wheelData = unchecked((uint)(delta << 16));
        nint screenCoordinates =
            (point.X & 0xffff) |
            ((point.Y & 0xffff) << 16);
        if (!Interop.PostMessage(
            focusWindow,
            MouseWheelMessage,
            new WPARAM(wheelData),
            new LPARAM(screenCoordinates)))
        {
            throw new InvalidOperationException("Could not post mouse wheel input.");
        }
        await Task.Delay(50);
    }

    private void Prepare(InputPoint point)
    {
        target.ActivateWindow();
        if (!Interop.SetCursorPos(point.X, point.Y))
        {
            throw new InvalidOperationException("Could not position the mouse over the input target.");
        }

        Thread.Sleep(30);
        target.VerifyPoint(point);
    }

    private static void SendClick() =>
        NativeInput.Send(
            CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateButtonInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));

    private static INPUT CreateButtonInput(MOUSE_EVENT_FLAGS flags) =>
        new()
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = { mi = new MOUSEINPUT { dwFlags = flags } },
        };

    private static INPUT CreateMoveInput(InputPoint point)
    {
        int desktopX = Interop.GetSystemMetrics(
            Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int desktopY = Interop.GetSystemMetrics(
            Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int desktopWidth = Interop.GetSystemMetrics(
            Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int desktopHeight = Interop.GetSystemMetrics(
            Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        int absoluteX = Math.Clamp(
            (int)Math.Round(((point.X - desktopX) * 65535d) / Math.Max(desktopWidth - 1, 1)),
            0,
            65535);
        int absoluteY = Math.Clamp(
            (int)Math.Round(((point.Y - desktopY) * 65535d) / Math.Max(desktopHeight - 1, 1)),
            0,
            65535);
        return new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous =
            {
                mi = new MOUSEINPUT
                {
                    dx = absoluteX,
                    dy = absoluteY,
                    dwFlags =
                        MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE |
                        MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE |
                        MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
    }

}
