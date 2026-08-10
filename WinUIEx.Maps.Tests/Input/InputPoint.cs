namespace WinUIEx.Maps.Tests.Input;

internal readonly record struct InputPoint(int X, int Y);

internal readonly record struct InputBounds(int Left, int Top, int Width, int Height)
{
    internal InputPoint Center => new(Left + (Width / 2), Top + (Height / 2));
}
