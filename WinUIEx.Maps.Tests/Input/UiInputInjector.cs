using Microsoft.UI.Xaml;

namespace WinUIEx.Maps.Tests.Input;

internal sealed class UiInputInjector
{
    private readonly InputTarget _target;

    private UiInputInjector(InputTarget target)
    {
        _target = target;
        Mouse = new MouseInputInjector(target);
        Keyboard = new KeyboardInputInjector(target);
        Touch = new TouchInputInjector(target);
        Screenshot = new ScreenshotInjector(target);
    }

    internal MouseInputInjector Mouse { get; }

    internal KeyboardInputInjector Keyboard { get; }

    internal TouchInputInjector Touch { get; }

    internal ScreenshotInjector Screenshot { get; }

    internal InputPoint PointAt(double horizontalFraction, double verticalFraction) =>
        _target.PointAt(horizontalFraction, verticalFraction);

    internal static UiInputInjector ForElement(Window window, FrameworkElement element) =>
        new(InputTarget.FromElement(window, element));
}
