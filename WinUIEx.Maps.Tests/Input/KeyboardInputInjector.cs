using Windows.System;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class KeyboardInputInjector(InputTarget target)
{
    internal void Press(VirtualKey key)
    {
        Press(key, []);
    }

    internal void Press(
        VirtualKey key,
        params VirtualKey[] modifiers)
    {
        target.ActivateWindow();
        List<INPUT> inputs = new((modifiers.Length * 2) + 2);
        foreach (VirtualKey modifier in modifiers)
        {
            inputs.Add(CreateKeyInput(modifier, keyUp: false));
        }
        inputs.Add(CreateKeyInput(key, keyUp: false));
        inputs.Add(CreateKeyInput(key, keyUp: true));
        for (int index = modifiers.Length - 1; index >= 0; index--)
        {
            inputs.Add(CreateKeyInput(modifiers[index], keyUp: true));
        }
        NativeInput.Send(inputs.ToArray());
    }

    internal void KeyDown(
        VirtualKey key,
        params VirtualKey[] modifiers)
    {
        target.ActivateWindow();
        INPUT[] inputs = new INPUT[modifiers.Length + 1];
        for (int index = 0; index < modifiers.Length; index++)
        {
            inputs[index] = CreateKeyInput(modifiers[index], keyUp: false);
        }
        inputs[^1] = CreateKeyInput(key, keyUp: false);
        NativeInput.Send(inputs);
    }

    internal void KeyUp(
        VirtualKey key,
        params VirtualKey[] modifiers)
    {
        List<INPUT> inputs = new(modifiers.Length + 1)
        {
            CreateKeyInput(key, keyUp: true),
        };
        for (int index = modifiers.Length - 1; index >= 0; index--)
        {
            inputs.Add(CreateKeyInput(modifiers[index], keyUp: true));
        }
        NativeInput.Send(inputs.ToArray());
    }

    private static INPUT CreateKeyInput(VirtualKey key, bool keyUp)
    {
        ushort virtualKey = checked((ushort)key);
        bool extended = key is
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End or VirtualKey.Insert or VirtualKey.Delete or
            VirtualKey.PageUp or VirtualKey.PageDown or
            VirtualKey.RightControl or VirtualKey.RightMenu;
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
