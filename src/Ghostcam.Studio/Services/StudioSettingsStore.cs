using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ghostcam.Studio.Services;

/// <summary>
/// Serializable snapshot of camera, transform, color, and effect settings for the current session or a custom profile (P1/P2/P3).
/// </summary>
public sealed class StudioProfileData
{
    public string SelectedLens { get; set; } = "Wide 1x";
    public string SelectedResolution { get; set; } = "1280x720 (HD)";
    public string SelectedFps { get; set; } = "60 FPS (Ultra Smooth)";
    public double HardwareIso { get; set; } = 100;
    public double HardwareShutter { get; set; } = 60;
    public double HardwareWbKelvin { get; set; } = 5400;
    public double HardwareFocus { get; set; } = 0.5;
    public float Zoom { get; set; } = 1.0f;
    public float PanX { get; set; } = 0.0f;
    public float PanY { get; set; } = 0.0f;
    public int RotationDegrees { get; set; } = 0;
    public bool MirrorHorizontal { get; set; } = false;
    public float Brightness { get; set; } = 0.0f;
    public float Contrast { get; set; } = 0.0f;
    public float Saturation { get; set; } = 0.0f;
    public float Temperature { get; set; } = 0.0f;
    public float Tint { get; set; } = 0.0f;
    public float Sharpness { get; set; } = 0.0f;
    public float Vignette { get; set; } = 0.0f;
    public float BackgroundBlur { get; set; } = 0.0f;
    public bool ChromaKeyEnabled { get; set; } = false;
    public float ChromaKeyThreshold { get; set; } = 40.0f;
    public bool AutoFramingEnabled { get; set; } = false;
    public string ColorPreset { get; set; } = "None";
    public bool ShowHudOverlay { get; set; } = false;
    public string? CustomLutPath { get; set; }

    public StudioProfileData Clone() => (StudioProfileData)MemberwiseClone();
}

/// <summary>
/// Root persisted studio configuration including theme, active source mode, current settings, and P1/P2/P3 profiles.
/// </summary>
public sealed class StudioSavedConfig
{
    public bool IsDarkTheme { get; set; } = false;
    public string SourceMode { get; set; } = "SafariWebCam";
    public StudioProfileData Current { get; set; } = new();
    public Dictionary<string, StudioProfileData> Profiles { get; set; } = new();
}

/// <summary>
/// Fault-tolerant JSON persistence store for Ghostcam Studio settings in %LocalAppData%\Ghostcam\settings.json.
/// </summary>
public static class StudioSettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string SettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghostcam",
        "settings.json");

    public static StudioSavedConfig Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new StudioSavedConfig();
            }

            string json = File.ReadAllText(SettingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new StudioSavedConfig();
            }

            StudioSavedConfig? loaded = JsonSerializer.Deserialize<StudioSavedConfig>(json, s_jsonOptions);
            if (loaded is null)
            {
                return new StudioSavedConfig();
            }

            loaded.Current ??= new StudioProfileData();
            loaded.Profiles ??= new Dictionary<string, StudioProfileData>();
            if (string.IsNullOrWhiteSpace(loaded.SourceMode))
            {
                loaded.SourceMode = "SafariWebCam";
            }

            return loaded;
        }
        catch
        {
            return new StudioSavedConfig();
        }
    }

    public static void Save(StudioSavedConfig config)
    {
        if (config is null)
        {
            return;
        }

        string tempPath = SettingsFilePath + $".{Environment.ProcessId}.tmp";
        try
        {
            string? directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(config, s_jsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }
}
