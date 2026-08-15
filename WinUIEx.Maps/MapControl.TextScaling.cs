using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.UI.ViewManagement;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private UISettings? _uiSettings;
    private double _effectiveTextScaleFactor = 1;

    internal double EffectiveTextScaleFactor => _effectiveTextScaleFactor;

    private void InitializeTextScaling()
    {
        RegisterPropertyChangedCallback(
            IsTextScaleFactorEnabledProperty,
            OnIsTextScaleFactorEnabledChanged);
    }

    private void AttachTextScaleSettings()
    {
        if (_uiSettings is null)
        {
            _uiSettings = new UISettings();
            _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        }
        ApplyTextScaleFactor(_uiSettings.TextScaleFactor);
    }

    private void DetachTextScaleSettings()
    {
        if (_uiSettings is null)
        {
            return;
        }
        _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        _uiSettings = null;
    }

    private void OnTextScaleFactorChanged(UISettings sender, object args)
    {
        double textScaleFactor = sender.TextScaleFactor;
        DispatcherQueue.TryEnqueue(() =>
            ApplyTextScaleFactor(textScaleFactor));
    }

    private void OnIsTextScaleFactorEnabledChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (_uiSettings is not null)
        {
            ApplyTextScaleFactor(_uiSettings.TextScaleFactor);
        }
        else
        {
            ApplyTextScaleFactor(1);
        }
    }

    internal void ApplyTextScaleFactor(double systemTextScaleFactor)
    {
        double effectiveTextScaleFactor =
            IsTextScaleFactorEnabled &&
            double.IsFinite(systemTextScaleFactor) &&
            systemTextScaleFactor > 0
                ? systemTextScaleFactor
                : 1;
        if (_effectiveTextScaleFactor == effectiveTextScaleFactor)
        {
            return;
        }

        _effectiveTextScaleFactor = effectiveTextScaleFactor;
        _renderer.SetTextScaleFactor(effectiveTextScaleFactor);
        MapControlEventSource.Log.TextScaleFactorChanged(
            effectiveTextScaleFactor,
            IsTextScaleFactorEnabled);
    }
}
