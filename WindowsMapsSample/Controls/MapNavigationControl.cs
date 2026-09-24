using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsMapsSample.Controls;

[TemplatePart(Name = "PART_ZoomIn", Type = typeof(Button))]
[TemplatePart(Name = "PART_ZoomOut", Type = typeof(Button))]
[TemplatePart(Name = "PART_NorthUp", Type = typeof(Button))]
[TemplatePart(Name = "PART_Tilt", Type = typeof(Button))]
[TemplatePart(Name = "PART_Locate", Type = typeof(Button))]
[TemplatePart(Name = "PART_NorthIndicator", Type = typeof(FrameworkElement))]
public sealed partial class MapNavigationControl : Control
{
    private Button? _zoomIn, _zoomOut, _northUp, _locate, _tilt;
    private FrameworkElement? _northIndicator;

    public MapNavigationControl() => DefaultStyleKey = typeof(MapNavigationControl);

    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(double), typeof(MapNavigationControl),
        new PropertyMetadata(0d, (sender, _) => ((MapNavigationControl)sender).UpdateCompass()));

    public double Heading
    {
        get => (double)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public event RoutedEventHandler? ZoomInRequested;
    public event RoutedEventHandler? ZoomOutRequested;
    public event RoutedEventHandler? NorthUpRequested;
    public event RoutedEventHandler? LocateRequested;
    public event RoutedEventHandler? TiltRequested;

    protected override void OnApplyTemplate()
    {
        if (_zoomIn is not null) _zoomIn.Click -= ZoomIn_Click;
        if (_zoomOut is not null) _zoomOut.Click -= ZoomOut_Click;
        if (_northUp is not null) _northUp.Click -= NorthUp_Click;
        if (_locate is not null) _locate.Click -= Locate_Click;
        if (_tilt is not null) _tilt.Click -= Tilt_Click;
        base.OnApplyTemplate();
        _zoomIn = GetTemplateChild("PART_ZoomIn") as Button;
        _zoomOut = GetTemplateChild("PART_ZoomOut") as Button;
        _northUp = GetTemplateChild("PART_NorthUp") as Button;
        _locate = GetTemplateChild("PART_Locate") as Button;
        _tilt = GetTemplateChild("PART_Tilt") as Button;
        _northIndicator = GetTemplateChild("PART_NorthIndicator") as FrameworkElement;
        UpdateCompass();
        if (_zoomIn is not null) _zoomIn.Click += ZoomIn_Click;
        if (_zoomOut is not null) _zoomOut.Click += ZoomOut_Click;
        if (_northUp is not null) _northUp.Click += NorthUp_Click;
        if (_locate is not null) _locate.Click += Locate_Click;
        if (_tilt is not null) _tilt.Click += Tilt_Click;
    }

    private void UpdateCompass()
    {
        if (_northIndicator is not null)
            _northIndicator.RenderTransform = new RotateTransform { Angle = -Heading };
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomInRequested?.Invoke(this, e);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke(this, e);
    private void NorthUp_Click(object sender, RoutedEventArgs e) => NorthUpRequested?.Invoke(this, e);
    private void Locate_Click(object sender, RoutedEventArgs e) => LocateRequested?.Invoke(this, e);
    private void Tilt_Click(object sender, RoutedEventArgs e) => TiltRequested?.Invoke(this, e);
}
