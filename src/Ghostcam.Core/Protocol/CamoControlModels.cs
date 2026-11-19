using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostcam.Core.Protocol;

/// <summary>
/// QR Pairing payload encoded as JSON in the QR code shown on the desktop.
/// Scanned by the Camo mobile app to negotiate TLS-PSK encrypted pairing.
/// </summary>
public sealed class QrPairingPayload
{
    [JsonPropertyName("compatibilityVersion")]
    public int CompatibilityVersion { get; set; } = 1;

    [JsonPropertyName("pairingID")]
    public string PairingId { get; set; } = string.Empty;

    /// <summary>
    /// Base64 encoded 16-byte random key used for TLS-PSK identity "PairingKey".
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("customUrl")]
    public string? CustomUrl { get; set; }
}

/// <summary>
/// Announcement sent from desktop to phone over TLS-PSK stream during initial pairing.
/// </summary>
public sealed class PairingAnnouncementRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("malheurID")]
    public string MalheurId { get; set; } = string.Empty;
}

/// <summary>
/// RSA/EC public key details received from paired phone.
/// </summary>
public sealed class PairedPublicKey
{
    [JsonPropertyName("modulus")]
    public string Modulus { get; set; } = string.Empty;

    [JsonPropertyName("exponent")]
    public string Exponent { get; set; } = string.Empty;
}

/// <summary>
/// Persistent record of a paired Camo phone device stored in paired_devices.json.
/// </summary>
public sealed class PairedDeviceRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("publicKey")]
    public PairedPublicKey? PublicKey { get; set; }

    [JsonPropertyName("lastKnownIp")]
    public string? LastKnownIp { get; set; }

    [JsonPropertyName("lastKnownPort")]
    public int? LastKnownPort { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("malheurID")]
    public string? MalheurId { get; set; }

    [JsonPropertyName("malheurKey")]
    public string? MalheurKey { get; set; }

    [JsonPropertyName("lastConnected")]
    public DateTime? LastConnected { get; set; }
}

/// <summary>
/// Final acknowledgement sent over TLS-PSK stream to complete pairing.
/// </summary>
public sealed class PairingSuccessResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;
}

/// <summary>
/// Encrypted cloud rendezvous entry returned by Malheur service.
/// </summary>
public sealed class MalheurDeviceEntry
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    [JsonPropertyName("iv")]
    public string Iv { get; set; } = string.Empty;
}

public sealed class MalheurResponse
{
    [JsonPropertyName("data")]
    public List<MalheurDeviceEntry>? Data { get; set; }
}

public sealed class MalheurDecryptedEndpoint
{
    [JsonPropertyName("ips")]
    public List<string>? Ips { get; set; }

    [JsonPropertyName("port")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int Port { get; set; }
}

/// <summary>
/// Authentication payload sent over TLS as first packet when connecting via Wi-Fi.
/// </summary>
public sealed class WifiAuthenticatePayload
{
    [JsonPropertyName("wifiVersion")]
    public int WifiVersion { get; set; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

/// <summary>
/// Handshake payload sent by the Camo Mobile client upon initiating a session.
/// </summary>
public sealed class Hello2Payload
{
    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = "Camo Mobile";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "2.0";

    [JsonPropertyName("buildNumber")]
    public string BuildNumber { get; set; } = "1";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 2;

    [JsonPropertyName("deviceModel")]
    public string DeviceModel { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("supportedCodecs")]
    public List<string> SupportedCodecs { get; set; } = new();
}

/// <summary>
/// Server response choosing the protocol version (packet type SelectProtocolVersion = 22).
/// Wire format verified from CamoStudio.dll: {"selectedVersion": 11, "clientID": "windows", "clientVersion": ""}
/// </summary>
public sealed class SelectProtocolVersionPayload
{
    [JsonPropertyName("selectedVersion")]
    public int SelectedVersion { get; set; } = 11;

    [JsonPropertyName("clientID")]
    public string ClientId { get; set; } = "windows";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "";

    [JsonIgnore]
    public int Version
    {
        get => SelectedVersion;
        set => SelectedVersion = value;
    }

    [JsonIgnore]
    public int ProtocolVersion
    {
        get => SelectedVersion;
        set => SelectedVersion = value;
    }
}

/// <summary>
/// Authentication request/result payload (Authenticate = 17, AuthenticationResult = 18).
/// </summary>
public sealed class AuthenticationPayload
{
    [JsonPropertyName("result")]
    public int Result { get; set; } = 0;

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("authVersion")]
    public int AuthVersion { get; set; } = 1;
}

/// <summary>
/// Specification of a resolution supported by a device camera.
/// </summary>
public sealed class CameraResolution
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public List<int> Fps { get; set; } = new();

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// Specification of an individual camera on the phone (back, front, ultra-wide, telephoto).
/// </summary>
public class CameraDeviceSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public string Position { get; set; } = "back";

    [JsonPropertyName("focalLength")]
    public double FocalLength { get; set; }

    [JsonPropertyName("minISO")]
    public double MinIso { get; set; }

    [JsonPropertyName("maxISO")]
    public double MaxIso { get; set; }

    [JsonPropertyName("supportedResolutions")]
    public List<CameraResolution> SupportedResolutions { get; set; } = new();
}

/// <summary>
/// Camera descriptor model alias for iOS Camo 2.4.1 cameraInfo.cameras array.
/// </summary>
public sealed class CamoCameraDescriptor : CameraDeviceSpec
{
}

/// <summary>
/// Device status and camera capabilities reported in UpdateDeviceInfo (3).
/// </summary>
public sealed class DeviceInfoPayload
{
    [JsonPropertyName("batteryLevel")]
    public double BatteryLevel { get; set; } = 1.0;

    [JsonPropertyName("batteryState")]
    public string BatteryState { get; set; } = "unplugged";

    [JsonPropertyName("thermalState")]
    public string ThermalState { get; set; } = "nominal";

    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = "portrait";

    [JsonPropertyName("cameras")]
    public List<CameraDeviceSpec> Cameras { get; set; } = new();

    [JsonPropertyName("deviceModel")]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }
}

/// <summary>
/// Rational framerate fraction (Camo CamoFpsRational wire schema).
/// </summary>
public sealed class CamoFpsRational
{
    [JsonPropertyName("numerator")]
    public int Numerator { get; set; } = 30;

    [JsonPropertyName("denominator")]
    public int Denominator { get; set; } = 1;
}

/// <summary>
/// Desired framerate fraction used by Camo phone videoSettings (legacy backward compatibility).
/// </summary>
public sealed class DesiredFps
{
    [JsonPropertyName("d")]
    public int D { get; set; } = 30;

    [JsonPropertyName("n")]
    public int N { get; set; } = 1;

    [JsonPropertyName("Value")]
    public double Value { get; set; } = 30.0;
}

/// <summary>
/// Stream format and capture settings (Camo SettingsSynchronizer wire schema).
/// </summary>
public sealed class VideoSettings
{
    [JsonPropertyName("resolution")]
    public int[] Resolution { get; set; } = new[] { 1920, 1080 };

    [JsonPropertyName("mirrorVideo")]
    public bool? MirrorVideo { get; set; } = null;

    [JsonPropertyName("stabilisationEnabled")]
    public bool? StabilisationEnabled { get; set; } = null;

    [JsonPropertyName("cameraUniqueID")]
    public string? CameraUniqueId { get; set; } = null;

    [JsonPropertyName("desiredFPS")]
    public CamoFpsRational? DesiredFps { get; set; } = null;

    [JsonPropertyName("videoDelay")]
    public int? VideoDelay { get; set; } = null;

    // Helper property for backward compatibility with string assignments e.g. "1920x1080"
    [JsonIgnore]
    public string ResolutionString
    {
        get => Resolution != null && Resolution.Length >= 2
            ? $"{Resolution[0]}x{Resolution[1]}"
            : "1920x1080";
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                var parts = value.Split(new[] { 'x', 'X', '*', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    Resolution = new[] { w, h };
                    return;
                }
            }
            Resolution = new[] { 1920, 1080 };
        }
    }

    [JsonIgnore]
    public List<int> ResolutionArray
    {
        get => new List<int>(Resolution ?? Array.Empty<int>());
        set => Resolution = value?.ToArray() ?? new[] { 1920, 1080 };
    }

    [JsonIgnore]
    public string? CameraId
    {
        get => CameraUniqueId;
        set => CameraUniqueId = value;
    }
}

/// <summary>
/// Lens position control setting.
/// </summary>
public sealed class LensPositionSetting
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("Value")]
    public double Value { get; set; } = 0.0;
}

/// <summary>
/// Hardware camera controls (Camo SettingsSynchronizer wire schema).
/// </summary>
public sealed class HardwareSettings
{
    [JsonPropertyName("smartAutoFocus")]
    public bool SmartAutoFocus { get; set; } = true;

    [JsonPropertyName("lensPosition")]
    public LensPositionSetting LensPosition { get; set; } = new();

    [JsonPropertyName("torchEnabled")]
    public bool TorchEnabled { get; set; } = false;

    [JsonPropertyName("torchLevel")]
    public double TorchLevel { get; set; } = 0.0;

    [JsonPropertyName("focusPointOfInterest")]
    public List<double> FocusPointOfInterest { get; set; } = new() { 0.5, 0.5 };

    // Backward compatibility helper properties for Studio MainViewModel
    [JsonIgnore]
    public string? CameraId { get; set; }

    [JsonIgnore]
    public string? CameraUniqueId { get; set; }

    [JsonIgnore]
    public double? Iso { get; set; }

    [JsonIgnore]
    public double? ShutterSpeed { get; set; }

    [JsonIgnore]
    public string? WhiteBalance { get; set; }

    [JsonIgnore]
    public double? Temperature { get; set; }

    [JsonIgnore]
    public double? Tint { get; set; }

    [JsonIgnore]
    public double? FocusDistance
    {
        get => LensPosition.Value;
        set
        {
            LensPosition.Value = value ?? 0.0;
            LensPosition.Enabled = value.HasValue && value.Value > 0.0;
        }
    }

    [JsonIgnore]
    public double? Torch
    {
        get => TorchLevel;
        set
        {
            TorchLevel = value ?? 0.0;
            TorchEnabled = TorchLevel > 0.0;
        }
    }
}

/// <summary>
/// Audio controls for Camo phone session.
/// </summary>
public sealed class AudioSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Complete camera and pipeline sync settings payload (SyncSettings = 4).
/// Exactly matches Camo Studio SettingsSynchronizer wire format.
/// </summary>
public sealed class SyncSettingsPayload
{
    [JsonPropertyName("customModeID")]
    public string? CustomModeId { get; set; }

    [JsonPropertyName("$clear")]
    public string[]? Clear { get; set; }

    [JsonPropertyName("audioSettings")]
    public AudioSettings? AudioSettings { get; set; }

    [JsonPropertyName("hardwareSettings")]
    public HardwareSettings? HardwareSettings { get; set; }

    [JsonPropertyName("postProcessingSettings")]
    public object? PostProcessingSettings { get; set; }

    [JsonPropertyName("videoSettings")]
    public VideoSettings? VideoSettings { get; set; }

    [JsonPropertyName("appSettings")]
    public object? AppSettings { get; set; }

    [JsonPropertyName("uvcSettings")]
    public object? UvcSettings { get; set; }

    public void SetResolution(int width, int height)
    {
        VideoSettings ??= new VideoSettings();
        VideoSettings.Resolution = new[] { width, height };
    }

    public void SetFps(int fps)
    {
        VideoSettings ??= new VideoSettings();
        VideoSettings.DesiredFps = new CamoFpsRational { Numerator = fps, Denominator = 1 };
    }

    public void SetTorch(bool enabled, double level = 1.0)
    {
        HardwareSettings ??= new HardwareSettings();
        HardwareSettings.TorchEnabled = enabled;
        HardwareSettings.TorchLevel = enabled ? Math.Clamp(level, 0.0, 1.0) : 0.0;
    }

    public void SetLensPosition(bool manual, double position)
    {
        HardwareSettings ??= new HardwareSettings();
        HardwareSettings.SmartAutoFocus = !manual;
        HardwareSettings.LensPosition.Enabled = manual;
        HardwareSettings.LensPosition.Value = manual ? Math.Clamp(position, 0.0, 1.0) : 0.0;
    }
}

/// <summary>
/// Client info embedded in Wake payload (Wake = 5).
/// Matches CamoStudio.Model.Camo.ClientInfo.
/// </summary>
public sealed class WakeClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = Environment.MachineName;

    [JsonPropertyName("licenseInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? LicenseInfo { get; set; } = null;
}

/// <summary>
/// Reflection-verified Wake payload sent from desktop to phone (Wake = 5).
/// Exactly matches CamoStudio.Model.Camo.Wake.
/// </summary>
public sealed class WakePayload
{
    [JsonPropertyName("clientInfo")]
    public WakeClientInfo ClientInfo { get; set; } = new();

    [JsonPropertyName("settings")]
    public SyncSettingsPayload Settings { get; set; } = new()
    {
        VideoSettings = new VideoSettings
        {
            Resolution = new[] { 1920, 1080 }
        }
    };

    [JsonPropertyName("packetTypesSupported")]
    public int[] PacketTypesSupported { get; set; } =
        new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };
}

/// <summary>
/// Centralized JSON serialization helpers with Camo protocol conventions.
/// </summary>
public static class CamoJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    public static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, SerializerOptions);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions);
}
