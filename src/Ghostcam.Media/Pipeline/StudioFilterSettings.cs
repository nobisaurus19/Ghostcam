using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// Predefined studio color grading presets.
/// </summary>
public enum StudioColorPreset
{
    None = 0,
    StudioWarm,
    CinematicTealOrange,
    CyberpunkNeon,
    NoirMono,
    VividPop
}

/// <summary>
/// Real-time studio camera filter, geometric transformation, and color grading parameters.
/// </summary>
public sealed class StudioFilterSettings : INotifyPropertyChanged
{
    private float _zoom = 1.0f;
    private float _panX = 0.0f;
    private float _panY = 0.0f;
    private int _rotationDegrees = 0;
    private bool _mirrorHorizontal = false;
    private float _brightness = 0.0f;
    private float _contrast = 0.0f;
    private float _saturation = 0.0f;
    private float _temperature = 0.0f;
    private float _tint = 0.0f;
    private float _sharpness = 0.0f;
    private float _vignette = 0.0f;
    private StudioColorPreset _colorPreset = StudioColorPreset.None;
    private bool _showHudOverlay = false;
    private float _hardwareIso = 100.0f;
    private float _hardwareShutter = 60.0f;
    private float _hardwareWbKelvin = 5400.0f;
    private float _hardwareFocus = 0.5f;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Optical/digital zoom factor: 1.0x (normal) to 4.0x.
    /// </summary>
    public float Zoom
    {
        get => _zoom;
        set => SetField(ref _zoom, Math.Clamp(value, 1.0f, 4.0f));
    }

    /// <summary>
    /// Horizontal pan offset: -1.0 (left) to +1.0 (right).
    /// </summary>
    public float PanX
    {
        get => _panX;
        set => SetField(ref _panX, Math.Clamp(value, -1.0f, 1.0f));
    }

    /// <summary>
    /// Vertical pan offset: -1.0 (up) to +1.0 (down).
    /// </summary>
    public float PanY
    {
        get => _panY;
        set => SetField(ref _panY, Math.Clamp(value, -1.0f, 1.0f));
    }

    /// <summary>
    /// Rotation in degrees clockwise: 0, 90, 180, or 270.
    /// </summary>
    public int RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            int normalized = ((value % 360) + 360) % 360;
            // Snap to nearest 90
            int snapped = (int)(Math.Round(normalized / 90.0) * 90) % 360;
            SetField(ref _rotationDegrees, snapped);
        }
    }

    /// <summary>
    /// Mirrors the image horizontally for selfie/cam preview orientation.
    /// </summary>
    public bool MirrorHorizontal
    {
        get => _mirrorHorizontal;
        set => SetField(ref _mirrorHorizontal, value);
    }

    /// <summary>
    /// Brightness adjustment from -100 to +100.
    /// </summary>
    public float Brightness
    {
        get => _brightness;
        set => SetField(ref _brightness, Math.Clamp(value, -100.0f, 100.0f));
    }

    /// <summary>
    /// Contrast adjustment from -100 to +100.
    /// </summary>
    public float Contrast
    {
        get => _contrast;
        set => SetField(ref _contrast, Math.Clamp(value, -100.0f, 100.0f));
    }

    /// <summary>
    /// Color saturation adjustment from -100 to +100.
    /// </summary>
    public float Saturation
    {
        get => _saturation;
        set => SetField(ref _saturation, Math.Clamp(value, -100.0f, 100.0f));
    }

    /// <summary>
    /// Color temperature from -100 (cool/blue) to +100 (warm/amber).
    /// </summary>
    public float Temperature
    {
        get => _temperature;
        set => SetField(ref _temperature, Math.Clamp(value, -100.0f, 100.0f));
    }

    /// <summary>
    /// Tint adjustment from -100 (green) to +100 (magenta).
    /// </summary>
    public float Tint
    {
        get => _tint;
        set => SetField(ref _tint, Math.Clamp(value, -100.0f, 100.0f));
    }

    /// <summary>
    /// Unsharp mask sharpness enhancement: 0 to 100.
    /// </summary>
    public float Sharpness
    {
        get => _sharpness;
        set => SetField(ref _sharpness, Math.Clamp(value, 0.0f, 100.0f));
    }

    /// <summary>
    /// Lens vignette effect strength: 0 to 100.
    /// </summary>
    public float Vignette
    {
        get => _vignette;
        set => SetField(ref _vignette, Math.Clamp(value, 0.0f, 100.0f));
    }

    /// <summary>
    /// LUT color grading preset.
    /// </summary>
    public StudioColorPreset ColorPreset
    {
        get => _colorPreset;
        set => SetField(ref _colorPreset, value);
    }

    /// <summary>
    /// Toggles on-screen studio HUD (framing grid, live stats, timecode).
    /// </summary>
    public bool ShowHudOverlay
    {
        get => _showHudOverlay;
        set => SetField(ref _showHudOverlay, value);
    }

    /// <summary>
    /// Hardware camera sensor ISO gain: 50.0 to 3200.0 (100 is base neutral).
    /// </summary>
    public float HardwareIso
    {
        get => _hardwareIso;
        set => SetField(ref _hardwareIso, Math.Clamp(value, 50.0f, 3200.0f));
    }

    /// <summary>
    /// Hardware shutter speed denominator in 1/s: 30.0 to 1000.0 (60 is base neutral 1/60s).
    /// </summary>
    public float HardwareShutter
    {
        get => _hardwareShutter;
        set => SetField(ref _hardwareShutter, Math.Clamp(value, 30.0f, 1000.0f));
    }

    /// <summary>
    /// Hardware white balance color temperature in Kelvin: 2500K to 7500K (5400K is base neutral daylight).
    /// </summary>
    public float HardwareWbKelvin
    {
        get => _hardwareWbKelvin;
        set => SetField(ref _hardwareWbKelvin, Math.Clamp(value, 2500.0f, 7500.0f));
    }

    /// <summary>
    /// Hardware manual focus distance: 0.0 to 1.0 (0.5 is base neutral focus).
    /// </summary>
    public float HardwareFocus
    {
        get => _hardwareFocus;
        set => SetField(ref _hardwareFocus, Math.Clamp(value, 0.0f, 1.0f));
    }

    /// <summary>
    /// Resets all values to default neutral settings.
    /// </summary>
    public void Reset()
    {
        Zoom = 1.0f;
        PanX = 0.0f;
        PanY = 0.0f;
        RotationDegrees = 0;
        MirrorHorizontal = false;
        Brightness = 0.0f;
        Contrast = 0.0f;
        Saturation = 0.0f;
        Temperature = 0.0f;
        Tint = 0.0f;
        Sharpness = 0.0f;
        Vignette = 0.0f;
        ColorPreset = StudioColorPreset.None;
        ShowHudOverlay = false;
        HardwareIso = 100.0f;
        HardwareShutter = 60.0f;
        HardwareWbKelvin = 5400.0f;
        HardwareFocus = 0.5f;
    }

    /// <summary>
    /// Creates a deep copy of the settings.
    /// </summary>
    public StudioFilterSettings Clone()
    {
        return new StudioFilterSettings
        {
            _zoom = this._zoom,
            _panX = this._panX,
            _panY = this._panY,
            _rotationDegrees = this._rotationDegrees,
            _mirrorHorizontal = this._mirrorHorizontal,
            _brightness = this._brightness,
            _contrast = this._contrast,
            _saturation = this._saturation,
            _temperature = this._temperature,
            _tint = this._tint,
            _sharpness = this._sharpness,
            _vignette = this._vignette,
            _colorPreset = this._colorPreset,
            _showHudOverlay = this._showHudOverlay,
            _hardwareIso = this._hardwareIso,
            _hardwareShutter = this._hardwareShutter,
            _hardwareWbKelvin = this._hardwareWbKelvin,
            _hardwareFocus = this._hardwareFocus
        };
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
