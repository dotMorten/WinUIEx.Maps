using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class TouchInputInjector(InputTarget target)
{
    private const uint MaximumContacts = 10;
    private const uint TouchMaskContactArea = 0x00000001;
    private static readonly object InitializationLock = new();
    private static bool _initialized;

    internal Task TapAsync() => InjectAsync([[target.Center]], 0);

    internal async Task DoubleTapAsync()
    {
        await InjectAsync([[target.Center]], 0);
        await Task.Delay(60);
        await InjectAsync([[target.Center]], 0);
    }

    internal Task SwipeAsync(
        InputPoint start,
        InputPoint end,
        int durationMilliseconds = 250) =>
        InjectAsync([[start, end]], durationMilliseconds);

    internal Task PinchAsync(int distance = 100, int durationMilliseconds = 250)
        => PinchAsync(target.Center, distance, durationMilliseconds);

    internal Task PinchAsync(
        InputPoint center,
        int distance = 100,
        int durationMilliseconds = 250)
    {
        return InjectAsync(
            [
                [new InputPoint(center.X - distance, center.Y), new InputPoint(center.X - 10, center.Y)],
                [new InputPoint(center.X + distance, center.Y), new InputPoint(center.X + 10, center.Y)],
            ],
            durationMilliseconds);
    }

    internal Task StretchAsync(int distance = 100, int durationMilliseconds = 250)
        => StretchAsync(target.Center, distance, durationMilliseconds);

    internal Task StretchAsync(
        InputPoint center,
        int distance = 100,
        int durationMilliseconds = 250)
    {
        return InjectAsync(
            [
                [new InputPoint(center.X - 10, center.Y), new InputPoint(center.X - distance, center.Y)],
                [new InputPoint(center.X + 10, center.Y), new InputPoint(center.X + distance, center.Y)],
            ],
            durationMilliseconds);
    }

    internal Task RotateAsync(
        double degrees,
        int radius = 80,
        int durationMilliseconds = 250,
        Func<Task>? beforeRelease = null)
    {
        double radians = degrees * Math.PI / 180;
        int horizontal = (int)Math.Round(radius * Math.Cos(radians));
        int vertical = (int)Math.Round(radius * Math.Sin(radians));
        return InjectAsync(
            [
                [
                    new InputPoint(target.Center.X - radius, target.Center.Y),
                    new InputPoint(
                        target.Center.X - horizontal,
                        target.Center.Y - vertical),
                ],
                [
                    new InputPoint(target.Center.X + radius, target.Center.Y),
                    new InputPoint(
                        target.Center.X + horizontal,
                        target.Center.Y + vertical),
                ],
            ],
            durationMilliseconds,
            beforeRelease);
    }

    private async Task InjectAsync(
        IReadOnlyList<IReadOnlyList<InputPoint>> paths,
        int durationMilliseconds,
        Func<Task>? beforeRelease = null)
    {
        target.ActivateWindow();
        await Task.Delay(100);
        foreach (IReadOnlyList<InputPoint> path in paths)
        {
            target.VerifyPoint(path[0]);
            target.VerifyPoint(path[^1]);
        }

        HSYNTHETICPOINTERDEVICE device = Interop.CreateSyntheticPointerDevice(
            POINTER_INPUT_TYPE.PT_TOUCH,
            MaximumContacts,
            POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE);
        Action<POINTER_TOUCH_INFO[]> send;
        if (device.IsNull)
        {
            EnsureInitialized();
            send = SendLegacy;
        }
        else
        {
            send = contacts => SendSynthetic(device, contacts);
        }

        var contacts = new POINTER_TOUCH_INFO[paths.Count];
        try
        {
            for (int index = 0; index < paths.Count; index++)
            {
                contacts[index] = CreateContact(
                    (uint)index,
                    paths[index][0],
                    POINTER_FLAGS.POINTER_FLAG_DOWN |
                    POINTER_FLAGS.POINTER_FLAG_INRANGE |
                    POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                    index == 0);
            }

            send(contacts);
            try
            {
                const int steps = 20;
                for (int step = 1; step <= steps && paths.Any(path => path.Count > 1); step++)
                {
                    if (durationMilliseconds > 0)
                    {
                        await Task.Delay(Math.Max(1, durationMilliseconds / steps));
                    }

                    double progress = step / (double)steps;
                    for (int index = 0; index < paths.Count; index++)
                    {
                        InputPoint start = paths[index][0];
                        InputPoint end = paths[index][^1];
                        var point = new InputPoint(
                            start.X + (int)Math.Round((end.X - start.X) * progress),
                            start.Y + (int)Math.Round((end.Y - start.Y) * progress));
                        contacts[index] = CreateContact(
                            (uint)index,
                            point,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE |
                            POINTER_FLAGS.POINTER_FLAG_INRANGE |
                            POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                            index == 0);
                    }

                    send(contacts);
                }

                if (beforeRelease is not null)
                {
                    await beforeRelease();
                }
                await Task.Delay(50);
            }
            finally
            {
                for (int index = 0; index < paths.Count; index++)
                {
                    contacts[index] = CreateContact(
                        (uint)index,
                        paths[index][^1],
                        POINTER_FLAGS.POINTER_FLAG_UP,
                        index == 0);
                }

                send(contacts);
            }
        }
        finally
        {
            if (!device.IsNull)
            {
                Interop.DestroySyntheticPointerDevice(device);
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitializationLock)
        {
            if (_initialized)
            {
                return;
            }

            if (!Interop.InitializeTouchInjection(
                MaximumContacts,
                TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_NONE))
            {
                ThrowNativeFailure("InitializeTouchInjection");
            }

            _initialized = true;
        }
    }

    private static POINTER_TOUCH_INFO CreateContact(
        uint id,
        InputPoint point,
        POINTER_FLAGS flags,
        bool primary)
    {
        if (primary)
        {
            flags |= POINTER_FLAGS.POINTER_FLAG_PRIMARY;
        }

        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                pointerId = id,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(point.X, point.Y),
            },
            touchMask = TouchMaskContactArea,
            rcContact = new RECT
            {
                left = point.X - 2,
                top = point.Y - 2,
                right = point.X + 2,
                bottom = point.Y + 2,
            },
        };
    }

    private static unsafe void SendLegacy(POINTER_TOUCH_INFO[] contacts)
    {
        fixed (POINTER_TOUCH_INFO* contactPointer = contacts)
        {
            if (!Interop.InjectTouchInput((uint)contacts.Length, contactPointer))
            {
                ThrowNativeFailure("InjectTouchInput");
            }
        }
    }

    private static unsafe void SendSynthetic(
        HSYNTHETICPOINTERDEVICE device,
        POINTER_TOUCH_INFO[] contacts)
    {
        var pointerInfos = new POINTER_TYPE_INFO[contacts.Length];
        for (int index = 0; index < contacts.Length; index++)
        {
            pointerInfos[index] = new POINTER_TYPE_INFO
            {
                type = POINTER_INPUT_TYPE.PT_TOUCH,
            };
            pointerInfos[index].Anonymous.touchInfo = contacts[index];
        }

        fixed (POINTER_TYPE_INFO* pointerInfo = pointerInfos)
        {
            if (!Interop.InjectSyntheticPointerInput(
                device,
                pointerInfo,
                (uint)pointerInfos.Length))
            {
                ThrowNativeFailure("InjectSyntheticPointerInput");
            }
        }
    }

    private static void ThrowNativeFailure(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(
            $"{operation} failed (Win32 error {error}: {new Win32Exception(error).Message}).");
    }
}
