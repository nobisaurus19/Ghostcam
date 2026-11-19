using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostcam.Core.Transport;

public enum UsbPlatform
{
    Ios,
    Android
}

public sealed record UsbDeviceInfo(
    UsbPlatform Platform,
    string DeviceId,
    string Serial,
    string Model,
    bool IsConnected);

/// <summary>
/// Bridges USB connections for both iOS (via Apple Mobile Device Service usbmuxd or libusbmuxd-2.0.dll)
/// and Android (via adb reverse / forward).
/// </summary>
public sealed class UsbDeviceBridge : IAsyncDisposable
{
    private readonly CamoMobileSessionServer _sessionServer;
    private readonly ConcurrentDictionary<string, UsbDeviceInfo> _connectedDevices = new();
    private CancellationTokenSource? _bridgeCts;
    private Task? _iosMonitorTask;
    private Task? _androidMonitorTask;
    private string? _resolvedAdbPath;

    public event Action<UsbDeviceInfo>? DeviceConnected;
    public event Action<UsbDeviceInfo>? DeviceDisconnected;
    public event Action<string>? LogMessage;

    public IReadOnlyCollection<UsbDeviceInfo> ConnectedDevices => (IReadOnlyCollection<UsbDeviceInfo>)_connectedDevices.Values;

    public UsbDeviceBridge(CamoMobileSessionServer sessionServer)
    {
        _sessionServer = sessionServer ?? throw new ArgumentNullException(nameof(sessionServer));
    }

    /// <summary>
    /// Starts background discovery and tunneling for iOS and Android USB devices.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _iosMonitorTask = Task.Run(() => MonitorIosDevicesAsync(_bridgeCts.Token), _bridgeCts.Token);
        _androidMonitorTask = Task.Run(() => MonitorAndroidDevicesAsync(_bridgeCts.Token), _bridgeCts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops all background monitoring tasks.
    /// </summary>
    public Task StopAsync()
    {
        _bridgeCts?.Cancel();
        return Task.CompletedTask;
    }

    #region iOS usbmuxd Bridge

    private async Task MonitorIosDevicesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? usbmuxClient = null;
            try
            {
                usbmuxClient = new TcpClient();
                await usbmuxClient.ConnectAsync("127.0.0.1", 27015, cancellationToken).ConfigureAwait(false);
                NetworkStream stream = usbmuxClient.GetStream();

                // Send Listen plist
                string listenXml =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\">\n<dict>\n" +
                    "  <key>MessageType</key><string>Listen</string>\n" +
                    "  <key>ClientVersionString</key><string>Ghostcam</string>\n" +
                    "  <key>ProgName</key><string>Ghostcam</string>\n" +
                    "</dict>\n</plist>";

                await SendUsbmuxPacketAsync(stream, listenXml, cancellationToken).ConfigureAwait(false);

                // Receive response loop
                while (!cancellationToken.IsCancellationRequested)
                {
                    string xml = await ReadUsbmuxPacketAsync(stream, cancellationToken).ConfigureAwait(false);
                    HandleUsbmuxMessage(xml);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[iOS Usbmuxd] Monitor disconnected: {ex.Message}. Retrying in 3s...");
                await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                usbmuxClient?.Dispose();
            }
        }
    }

    private void HandleUsbmuxMessage(string xml)
    {
        try
        {
            if (xml.Contains("<string>Attached</string>"))
            {
                string devId = ExtractXmlValue(xml, "DeviceID") ?? "unknown";
                string serial = ExtractXmlValue(xml, "SerialNumber") ?? devId;
                string prodId = ExtractXmlValue(xml, "ProductID") ?? "Apple iPhone";

                var device = new UsbDeviceInfo(UsbPlatform.Ios, devId, serial, $"Apple iOS Device ({prodId})", true);
                if (_connectedDevices.TryAdd(serial, device))
                {
                    LogMessage?.Invoke($"[iOS] Device attached: {serial} (ID: {devId})");
                    DeviceConnected?.Invoke(device);

                    // Auto-bridge to Camo port 9430 on device
                    _ = ConnectAndAttachIosSessionAsync(int.Parse(devId), serial);
                }
            }
            else if (xml.Contains("<string>Detached</string>"))
            {
                string devId = ExtractXmlValue(xml, "DeviceID") ?? string.Empty;
                foreach (var kvp in _connectedDevices)
                {
                    if (kvp.Value.Platform == UsbPlatform.Ios && kvp.Value.DeviceId == devId)
                    {
                        if (_connectedDevices.TryRemove(kvp.Key, out var removed))
                        {
                            LogMessage?.Invoke($"[iOS] Device detached: {removed.Serial}");
                            DeviceDisconnected?.Invoke(removed);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[iOS] Error processing usbmux message: {ex.Message}");
        }
    }

    private async Task ConnectAndAttachIosSessionAsync(int deviceId, string serial)
    {
        try
        {
            // Give Camo app on device a brief moment to be active
            await Task.Delay(1000).ConfigureAwait(false);

            Stream? stream = await ConnectToIosDevicePortAsync(deviceId, 9430).ConfigureAwait(false);
            if (stream != null)
            {
                LogMessage?.Invoke($"[iOS] Connected to Camo mobile app on device {serial}:9430 via USB!");
                await _sessionServer.AttachClientStreamAsync(stream, $"iOS USB ({serial})").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[iOS] Failed to auto-connect to Camo app on {serial}: {ex.Message}");
        }
    }

    public async Task<Stream?> ConnectToIosDevicePortAsync(int deviceId, int targetPort, CancellationToken cancellationToken = default)
    {
        TcpClient client = new();
        client.NoDelay = true;
        await client.ConnectAsync("127.0.0.1", 27015, cancellationToken).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();

        ushort netPort = (ushort)IPAddress.HostToNetworkOrder((short)targetPort);
        string connectXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "  <key>MessageType</key><string>Connect</string>\n" +
            "  <key>ClientVersionString</key><string>Ghostcam</string>\n" +
            "  <key>ProgName</key><string>Ghostcam</string>\n" +
            $"  <key>DeviceID</key><integer>{deviceId}</integer>\n" +
            $"  <key>PortNumber</key><integer>{netPort}</integer>\n" +
            "</dict>\n</plist>";

        await SendUsbmuxPacketAsync(stream, connectXml, cancellationToken).ConfigureAwait(false);
        string replyXml = await ReadUsbmuxPacketAsync(stream, cancellationToken).ConfigureAwait(false);

        string? resultStr = ExtractXmlValue(replyXml, "Number");
        if (resultStr == "0")
        {
            return stream;
        }

        client.Dispose();
        return null;
    }

    private static async Task SendUsbmuxPacketAsync(Stream stream, string xmlPayload, CancellationToken ct)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(xmlPayload);
        byte[] header = new byte[16];

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)(16 + payloadBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1); // Version = 1 (plist)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 8); // Message = 8 (Plist)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 1); // Tag = 1

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadUsbmuxPacketAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[16];
        await stream.ReadExactlyAsync(header, 0, 16, ct).ConfigureAwait(false);

        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (totalLen < 16) throw new InvalidDataException("Invalid usbmux packet length");

        int payloadLen = (int)(totalLen - 16);
        byte[] payload = new byte[payloadLen];
        await stream.ReadExactlyAsync(payload, 0, payloadLen, ct).ConfigureAwait(false);

        return Encoding.UTF8.GetString(payload);
    }

    private static string? ExtractXmlValue(string xml, string keyName)
    {
        // Simple fast extraction for plist keys without XML parser overhead
        string pattern = $"<key>{Regex.Escape(keyName)}</key>\\s*<(string|integer)>([^<]+)</(string|integer)>";
        var match = Regex.Match(xml, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[2].Value.Trim() : null;
    }

    #endregion

    #region Android ADB Bridge

    private async Task MonitorAndroidDevicesAsync(CancellationToken cancellationToken)
    {
        string? adbPath = ResolveAdbPath();
        if (adbPath == null)
        {
            LogMessage?.Invoke("[Android ADB] adb.exe not found. Android USB bridge inactive.");
            return;
        }

        _resolvedAdbPath = adbPath;
        LogMessage?.Invoke($"[Android ADB] Using ADB executable: {adbPath}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var currentSerials = await PollAdbDevicesAsync(adbPath, cancellationToken).ConfigureAwait(false);

                // Check disconnected
                foreach (var kvp in _connectedDevices)
                {
                    if (kvp.Value.Platform == UsbPlatform.Android && !currentSerials.ContainsKey(kvp.Key))
                    {
                        if (_connectedDevices.TryRemove(kvp.Key, out var removed))
                        {
                            LogMessage?.Invoke($"[Android] Device disconnected: {removed.Serial}");
                            DeviceDisconnected?.Invoke(removed);
                        }
                    }
                }

                // Check newly connected
                foreach (var kvp in currentSerials)
                {
                    if (_connectedDevices.TryAdd(kvp.Key, kvp.Value))
                    {
                        LogMessage?.Invoke($"[Android] Device connected: {kvp.Value.Serial} ({kvp.Value.Model})");
                        DeviceConnected?.Invoke(kvp.Value);

                        // Configure reverse port tunnel: device 9430 -> desktop 9430
                        await SetupAndroidReverseTunnelAsync(kvp.Key, 9430, cancellationToken).ConfigureAwait(false);

                        // Configure forward port tunnel: desktop 9431 -> device 9430
                        await SetupAndroidForwardTunnelAsync(kvp.Key, 9431, 9430, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[Android ADB] Polling error: {ex.Message}");
            }

            await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? ResolveAdbPath()
    {
        // 1. Check local AppData cache (previously extracted)
        string appDataAdb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghostcam", "Adb", "adb.exe");
        if (File.Exists(appDataAdb)) return appDataAdb;

        // 2. Check Camo Studio packaged ADB directory
        string camoAppAdbDir = @"C:\Program Files\WindowsApps\ReincubateLtd.CamoStudio_2.9.3.0_x64__9bq3v28c93p4r\Adb";
        string camoAppAdbExe = Path.Combine(camoAppAdbDir, "adb.exe");
        if (File.Exists(camoAppAdbExe))
        {
            try
            {
                // Test direct execution
                var psi = new ProcessStartInfo(camoAppAdbExe, "version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
                if (p?.ExitCode == 0) return camoAppAdbExe;
            }
            catch
            {
                // Access denied due to WindowsApps ACL -> copy to %LOCALAPPDATA%\Ghostcam\Adb
                try
                {
                    string targetDir = Path.GetDirectoryName(appDataAdb)!;
                    Directory.CreateDirectory(targetDir);
                    foreach (string file in Directory.GetFiles(camoAppAdbDir))
                    {
                        string dest = Path.Combine(targetDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                    }
                    if (File.Exists(appDataAdb)) return appDataAdb;
                }
                catch
                {
                    // Fallthrough to system ADB
                }
            }
        }

        // 3. Check system PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(p, "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 4. Check Android SDK default path
        string sdkAdb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(sdkAdb)) return sdkAdb;

        return null;
    }

    private async Task<Dictionary<string, UsbDeviceInfo>> PollAdbDevicesAsync(string adbPath, CancellationToken ct)
    {
        var devices = new Dictionary<string, UsbDeviceInfo>();
        var (stdout, _) = await RunProcessAsync(adbPath, "devices -l", ct).ConfigureAwait(false);

        using StringReader reader = new(stdout);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && tokens[1].Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                string serial = tokens[0];
                string model = "Android Device";
                foreach (string t in tokens)
                {
                    if (t.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    {
                        model = t["model:".Length..].Replace('_', ' ');
                    }
                }

                devices[serial] = new UsbDeviceInfo(UsbPlatform.Android, serial, serial, model, true);
            }
        }

        return devices;
    }

    public async Task<bool> SetupAndroidReverseTunnelAsync(string deviceSerial, int port = 9430, CancellationToken ct = default)
    {
        if (_resolvedAdbPath == null) return false;

        string args = $"-s {deviceSerial} reverse tcp:{port} tcp:{port}";
        var (_, stderr) = await RunProcessAsync(_resolvedAdbPath, args, ct).ConfigureAwait(false);

        bool success = string.IsNullOrEmpty(stderr) || !stderr.Contains("error");
        if (success)
        {
            LogMessage?.Invoke($"[Android] Reversed port tcp:{port} for device {deviceSerial}");
        }
        else
        {
            LogMessage?.Invoke($"[Android] Reverse port failed for {deviceSerial}: {stderr}");
        }
        return success;
    }

    public async Task<bool> SetupAndroidForwardTunnelAsync(string deviceSerial, int localPort = 9431, int remotePort = 9430, CancellationToken ct = default)
    {
        if (_resolvedAdbPath == null) return false;

        string args = $"-s {deviceSerial} forward tcp:{localPort} tcp:{remotePort}";
        var (_, stderr) = await RunProcessAsync(_resolvedAdbPath, args, ct).ConfigureAwait(false);

        bool success = string.IsNullOrEmpty(stderr) || !stderr.Contains("error");
        if (success)
        {
            LogMessage?.Invoke($"[Android] Forwarded local port {localPort} -> device port {remotePort} for {deviceSerial}");
        }
        return success;
    }

    private static async Task<(string stdout, string stderr)> RunProcessAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    #endregion

    #region libusbmuxd Native P/Invoke Integration

    private static class LibusbmuxdNative
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct UsbmuxdDeviceInfoNative
        {
            public int Handle;
            public int ProductId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 41)]
            public string Udid;
            public int ConnType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 200)]
            public byte[] ConnData;
        }

        [DllImport("libusbmuxd-2.0.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "usbmuxd_get_device_list")]
        public static extern int UsbmuxdGetDeviceList(out IntPtr deviceList);

        [DllImport("libusbmuxd-2.0.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "usbmuxd_device_list_free")]
        public static extern int UsbmuxdDeviceListFree(IntPtr deviceList);

        [DllImport("libusbmuxd-2.0.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "usbmuxd_connect")]
        public static extern int UsbmuxdConnect(int handle, ushort port);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _bridgeCts?.Dispose();
    }
}
