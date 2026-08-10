using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinUIEx.Maps.Tests.Input;

internal static class NativeInput
{
    internal static unsafe void Send(params INPUT[] inputs)
    {
        fixed (INPUT* inputPointer = inputs)
        {
            uint sent = Interop.SendInput((uint)inputs.Length, inputPointer, sizeof(INPUT));
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"SendInput delivered {sent} of {inputs.Length} events " +
                    $"(Win32 error {error}: {new Win32Exception(error).Message}).");
            }
        }
    }
}
