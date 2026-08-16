using Microsoft.UI.Xaml;
using System.Runtime.Versioning;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.UI.ViewManagement;

namespace WinUIEx.Maps;

public sealed partial class MapControl
{
    private UISettings? _uiSettings;
    private bool _animationsEnabled = true;
    private double _effectiveTextScaleFactor = 1;

    internal double EffectiveTextScaleFactor => _effectiveTextScaleFactor;

    private void InitializeTextScaling()
    {
        RegisterPropertyChangedCallback(
            IsTextScaleFactorEnabledProperty,
            OnIsTextScaleFactorEnabledChanged);
    }

    private void AttachSystemAccessibilitySettings()
    {
        if (_uiSettings is null)
        {
            _uiSettings = new UISettings();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                AttachAnimationsEnabledChanged(_uiSettings);
            }
            _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        }
        ApplyAnimationsEnabled(_uiSettings.AnimationsEnabled);
        ApplyTextScaleFactor(_uiSettings.TextScaleFactor);
    }

    private void DetachSystemAccessibilitySettings()
    {
        if (_uiSettings is null)
        {
            return;
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            DetachAnimationsEnabledChanged(_uiSettings);
        }
        _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        _uiSettings = null;
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private void AttachAnimationsEnabledChanged(UISettings settings)
    {
        settings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private void DetachAnimationsEnabledChanged(UISettings settings)
    {
        settings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args)
    {
        bool animationsEnabled = sender.AnimationsEnabled;
        DispatcherQueue.TryEnqueue(() =>
            ApplyAnimationsEnabled(animationsEnabled));
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

    internal void ApplyAnimationsEnabled(bool animationsEnabled)
    {
        EnsureUiThread();
        if (_animationsEnabled == animationsEnabled)
        {
            return;
        }

        _animationsEnabled = animationsEnabled;
        if (!animationsEnabled)
        {
            UpdateCameraTarget(
                forceImmediate: true,
                preservePendingViewChange: true);
        }

        PublishLayerSnapshots();
        UpdateFocusVisualState();
        MapControlEventSource.Log.AnimationsEnabledChanged(animationsEnabled);
    }
}
