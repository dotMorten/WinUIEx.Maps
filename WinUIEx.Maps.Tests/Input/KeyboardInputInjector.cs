using Windows.System;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class KeyboardInputInjector(InputTarget target)
{
    internal void Press(VirtualKey key)
    {
        target.ActivateWindow();
        ushort virtualKey = checked((ushort)key);
        bool extended = key is
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End or VirtualKey.Insert or VirtualKey.Delete or
            VirtualKey.PageUp or VirtualKey.PageDown;

        NativeInput.Send(
            CreateKeyInput(virtualKey, extended, keyUp: false),
            CreateKeyInput(virtualKey, extended, keyUp: true));
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool extended, bool keyUp)
    {
        var flags = (KEYBD_EVENT_FLAGS)0;
        if (extended)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        if (keyUp)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous =
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)virtualKey,
                    dwFlags = flags,
                },
            },
        };
    }
}
