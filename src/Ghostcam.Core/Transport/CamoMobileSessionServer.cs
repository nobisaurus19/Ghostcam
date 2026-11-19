using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ghostcam.Core.Protocol;

namespace Ghostcam.Core.Transport;

/// <summary>
/// Server and connection manager for Camo mobile device sessions.
/// Listens for inbound Wi-Fi / ADB connections on port 9430 and connects outbound to paired iPhones over TLS.
/// Handles the Camo 7-byte framed protocol, authentication, capability sync, settings updates,
/// and video/audio packet dispatching.
/// </summary>
public sealed class CamoMobileSessionServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoopTask;

    private Stream? _activeStream;
    private string? _activeRemoteEndpoint;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _connectLock = new();
    private CancellationTokenSource? _activeConnectCts;
    private ushort _sequenceCounter = 0;
    private Timer? _heartbeatTimer;
    private int _isConnecting = 0;

    public bool IsRunning => _listener != null;
    public bool IsClientConnected => _activeStream != null;
    public int Port { get; private set; } = 9430;
    public string? RemoteEndpoint => _activeRemoteEndpoint;

    public DeviceInfoPayload? CurrentDeviceInfo { get; private set; }
    public SyncSettingsPayload CurrentSettings { get; set; } = new();

    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    public event Action? HandshakeCompleted;
    public event Action<DeviceInfoPayload>? DeviceInfoUpdated;
    public event Action<VideoFramePacket>? VideoPacketReceived;
    public event Action<ReadOnlyMemory<byte>>? AudioPacketReceived;
    public event Action<CamoPacketHeader, ReadOnlyMemory<byte>>? PacketReceived;
    public event Action<string>? LogMessage;

    private string? _lastAuthHost;
    private int _lastAuthPort;
    private string? _lastAuthDesktopId;
    private string? _lastAuthPassword;

    /// <summary>
    /// Starts listening on the specified TCP port (default 9430).
    /// </summary>
    public Task StartAsync(int port = 9430, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        Port = port;
        _serverCts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        _acceptLoopTask = AcceptConnectionsAsync(_listener, _serverCts.Token);
        LogMessage?.Invoke($"[Server] TCP listener started on port {port}.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects to a paired iPhone over Wi-Fi, establishes TLS encryption with SslStream,
    /// and immediately transmits the initial CamoPacketType.Authenticate (17) packet.
    /// </summary>
    public async Task ConnectToPairedPhoneWifiAsync(
        string host,
        int port,
        string desktopId,
        string? password,
        PairedPublicKey? expectedPubKey = null,
        CancellationToken cancellationToken = default)
    {
        if (IsClientConnected)
        {
            return;
        }

        CancellationTokenSource connectCts;
        lock (_connectLock)
        {
            if (IsClientConnected)
            {
                return;
            }

            if (_isConnecting == 1)
            {
                if (_lastAuthPort != port)
                {
                    // If called with a new/different port while connect is pending, cancel the previous connect
                    try { _activeConnectCts?.Cancel(); } catch { }
                }
                else
                {
                    return;
                }
            }

            _isConnecting = 1;
            _lastAuthHost = host;
            _lastAuthPort = port;
            _lastAuthDesktopId = desktopId;
            _lastAuthPassword = password;

            _activeConnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeConnectCts.CancelAfter(TimeSpan.FromSeconds(2));
            connectCts = _activeConnectCts;
        }

        string endpointKey = $"{host}:{port}";
        LogMessage?.Invoke($"[Wi-Fi] Connecting to paired phone at {endpointKey}...");

        TcpClient client = new();
        client.NoDelay = true;
        try
        {
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            // Wrap stream in SSL with certificate callback validating RSA public key if available
            var sslStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (expectedPubKey != null && certificate is System.Security.Cryptography.X509Certificates.X509Certificate2 cert2)
                    {
                        try
                        {
                            using var rsa = cert2.GetRSAPublicKey();
                            if (rsa != null)
                            {
                                var rsaParams = rsa.ExportParameters(false);
                                if (rsaParams.Modulus != null && rsaParams.Exponent != null)
                                {
                                    string mod = Convert.ToBase64String(rsaParams.Modulus);
                                    string exp = Convert.ToBase64String(rsaParams.Exponent);
                                    if (string.Equals(mod, expectedPubKey.Modulus, StringComparison.Ordinal) &&
                                        string.Equals(exp, expectedPubKey.Exponent, StringComparison.Ordinal))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Fallback to true if key format differs
                        }
                    }
                    return true;
                });

            await sslStream.AuthenticateAsClientAsync(
                targetHost: host,
                clientCertificates: null,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: false).ConfigureAwait(false);

            LogMessage?.Invoke($"[Wi-Fi] TLS tunnel established with {endpointKey}. Sending Authenticate packet...");

            // First packet sent by desktop to phone: Authenticate (17)
            var authReq = new WifiAuthenticatePayload
            {
                WifiVersion = 1,
                Id = desktopId,
                Name = Environment.MachineName,
                Password = password
            };

            byte[] authBytes = CamoJson.SerializeToUtf8Bytes(authReq);
            ushort initialSeq = unchecked(++_sequenceCounter);
            await CamoPacketHeader.WritePacketAsync(sslStream, initialSeq, CamoPacketType.Authenticate, authBytes, cancellationToken).ConfigureAwait(false);

            // Begin active session loop
            _ = HandleSessionAsync(sslStream, endpointKey, client, cancellationToken);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Wi-Fi] Connection to {endpointKey} failed: {ex.Message}");
            client.Dispose();
        }
        finally
        {
            lock (_connectLock)
            {
                if (_activeConnectCts == connectCts)
                {
                    _isConnecting = 0;
                    _activeConnectCts = null;
                }
            }
            connectCts.Dispose();
        }
    }

    /// <summary>
    /// Connects directly to a Camo phone running on the local network (raw TCP fallback).
    /// </summary>
    public async Task ConnectToPhoneAsync(string host, int port = 9430, CancellationToken cancellationToken = default)
    {
        TcpClient client = new();
        client.NoDelay = true;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";

            _ = HandleSessionAsync(stream, remoteEndpoint, client, cancellationToken);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] Connection to {host}:{port} failed: {ex.Message}");
            client.Dispose();
        }
    }

    /// <summary>
    /// Attaches an arbitrary connected stream (e.g. from USB usbmuxd or ADB reverse tunnel).
    /// </summary>
    public Task AttachClientStreamAsync(Stream stream, string remoteEndpoint, IDisposable? owner = null, CancellationToken cancellationToken = default)
    {
        _ = HandleSessionAsync(stream, remoteEndpoint, owner, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

                _ = HandleSessionAsync(client.GetStream(), remoteEndpoint, client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleSessionAsync(Stream stream, string remoteEndpoint, IDisposable? connectionOwner, CancellationToken cancellationToken)
    {
        CloseActiveSession();

        _activeStream = stream;
        _activeRemoteEndpoint = remoteEndpoint;
        LogMessage?.Invoke($"[Session] Connected to {remoteEndpoint}.");
        ClientConnected?.Invoke(remoteEndpoint);

        // Background Heartbeat timer every 2 seconds
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                if (_activeStream != null)
                {
                    await SendPacketAsync(CamoPacketType.Heartbeat, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore transient heartbeat failures; read loop handles disconnection
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int videoPacketsLogged = 0;

        try
        {
            while (!sessionCts.IsCancellationRequested)
            {
                (CamoPacketHeader header, byte[] payload) = await CamoPacketHeader.ReadPacketAsync(stream, sessionCts.Token).ConfigureAwait(false);

                PacketReceived?.Invoke(header, payload);

                switch (header.PacketType)
                {
                    case CamoPacketType.AuthenticationResult:
                        string authJson = Encoding.UTF8.GetString(payload);
                        LogMessage?.Invoke($"[Auth] AuthenticationResult received: {authJson}");
                        bool authOk = true;
                        try
                        {
                            using var authDoc = JsonDocument.Parse(payload);
                            if (authDoc.RootElement.TryGetProperty("success", out var sProp))
                            {
                                authOk = sProp.GetBoolean();
                            }
                        }
                        catch { }

                        if (!authOk && !string.IsNullOrEmpty(_lastAuthPassword))
                        {
                            LogMessage?.Invoke("[Auth] Authentication failed with password. Retrying with null password...");
                            string lastHost = _lastAuthHost ?? "127.0.0.1";
                            int lastPort = _lastAuthPort;
                            string lastDevId = _lastAuthDesktopId ?? "";
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(500).ConfigureAwait(false);
                                await ConnectToPairedPhoneWifiAsync(lastHost, lastPort, lastDevId, null).ConfigureAwait(false);
                            });
                        }
                        break;

                    case CamoPacketType.Hello:
                        await HandleHelloAsync(payload, sessionCts.Token).ConfigureAwait(false);
                        break;

                    case CamoPacketType.Hello2:
                        await HandleHello2Async(payload, sessionCts.Token).ConfigureAwait(false);
                        break;

                    case CamoPacketType.UpdateDeviceInfo:
                        HandleDeviceInfo(payload);
                        break;

                    case CamoPacketType.Video:
                    case CamoPacketType.VideoV2:
                    case (CamoPacketType)24:
                    case (CamoPacketType)25:
                        if (videoPacketsLogged++ < 5)
                            LogMessage?.Invoke($"[Video] Received {header.PacketType} len={payload.Length} seq={header.SequenceId}");
                        var videoFrame = VideoFramePacket.Parse(header, payload);
                        VideoPacketReceived?.Invoke(videoFrame);
                        break;

                    case CamoPacketType.Audio:
                    case CamoPacketType.AudioV2:
                        AudioPacketReceived?.Invoke(payload);
                        break;

                    case CamoPacketType.Heartbeat:
                        break;

                    case CamoPacketType.Bye:
                        LogMessage?.Invoke("[Session] Bye packet received from mobile device.");
                        sessionCts.Cancel();
                        break;

                    default:
                        LogMessage?.Invoke($"[Session] Received packet type={(byte)header.PacketType} len={payload.Length}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] Stream terminated: {ex.Message}");
        }
        finally
        {
            CloseActiveSession();
            connectionOwner?.Dispose();
            ClientDisconnected?.Invoke(remoteEndpoint);
        }
    }

    private async Task HandleHelloAsync(byte[] payload, CancellationToken cancellationToken)
    {
        string helloJson = Encoding.UTF8.GetString(payload);
        LogMessage?.Invoke($"[Session] Received Hello (0): {helloJson}");
        try
        {
            using var doc = JsonDocument.Parse(payload);
            string camId = ExtractCameraUniqueId(doc.RootElement);
            if (!string.IsNullOrEmpty(camId))
            {
                CurrentSettings.VideoSettings ??= new VideoSettings();
                CurrentSettings.VideoSettings.CameraUniqueId = camId;
            }
            HandleDeviceInfoJson(doc.RootElement);

            // Send SelectProtocolVersion (22) with selectedVersion = 11, clientID = "windows", clientVersion = ""
            var protoSelect = new SelectProtocolVersionPayload
            {
                SelectedVersion = 11,
                ClientId = "windows",
                ClientVersion = ""
            };
            byte[] protoBytes = CamoJson.SerializeToUtf8Bytes(protoSelect);
            LogMessage?.Invoke($"[Session] Sending SelectProtocolVersion (22): {Encoding.UTF8.GetString(protoBytes)}");
            await SendPacketAsync(CamoPacketType.SelectProtocolVersion, protoBytes, cancellationToken).ConfigureAwait(false);

            // Check if Hello contains "supportedVersions"
            bool hasSupportedVersions = doc.RootElement.TryGetProperty("supportedVersions", out _);
            if (!hasSupportedVersions)
            {
                // Older protocol fallback: directly send Wake & Resume
                await SendWakeAsync(cancellationToken).ConfigureAwait(false);
            }
            // If it HAS supportedVersions, wait for phone to respond with Hello2 (23)
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] Error in HandleHelloAsync: {ex.Message}");
        }
    }

    private async Task HandleHello2Async(byte[] payload, CancellationToken cancellationToken)
    {
        string hello2Json = Encoding.UTF8.GetString(payload);
        LogMessage?.Invoke($"[Session] Received Hello2 (23): {hello2Json}");
        try
        {
            using var doc = JsonDocument.Parse(payload);
            string camId = ExtractCameraUniqueId(doc.RootElement);
            if (!string.IsNullOrEmpty(camId))
            {
                CurrentSettings.VideoSettings ??= new VideoSettings();
                CurrentSettings.VideoSettings.CameraUniqueId = camId;
            }
            HandleDeviceInfoJson(doc.RootElement);

            await SendWakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] Error in HandleHello2Async: {ex.Message}");
        }
    }

    private static string ExtractCameraUniqueId(JsonElement root)
    {
        var candidateList = new List<JsonElement> { root };
        if (root.TryGetProperty("deviceInfo", out var devInfo))
        {
            candidateList.Add(devInfo);
            if (devInfo.TryGetProperty("cameraInfo", out var devCamInfo))
                candidateList.Add(devCamInfo);
        }
        if (root.TryGetProperty("cameraInfo", out var rootCamInfo))
        {
            candidateList.Add(rootCamInfo);
        }

        foreach (var el in candidateList)
        {
            if (el.TryGetProperty("preferredDefaultCameraID", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? s = prop.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (el.TryGetProperty("selectedCameraID", out prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? s = prop.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (el.TryGetProperty("cameraUniqueID", out prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? s = prop.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (el.TryGetProperty("uniqueID", out prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? s = prop.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }

        foreach (var el in candidateList)
        {
            if (el.TryGetProperty("cameras", out var cameras) && cameras.ValueKind == JsonValueKind.Array)
            {
                string? fallbackId = null;
                foreach (var cam in cameras.EnumerateArray())
                {
                    string? idStr = null;
                    if (cam.TryGetProperty("uniqueID", out var uid) ||
                        cam.TryGetProperty("cameraUniqueID", out uid) ||
                        cam.TryGetProperty("id", out uid))
                    {
                        idStr = uid.GetString();
                    }

                    if (string.IsNullOrEmpty(idStr)) continue;
                    fallbackId ??= idStr;

                    if (cam.TryGetProperty("position", out var pos) &&
                        pos.GetString()?.Contains("back", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return idStr;
                    }
                }

                if (!string.IsNullOrEmpty(fallbackId)) return fallbackId;
            }
        }

        return "com.apple.avfoundation.avcapturedevice.built-in_video:0::zoom=1.0";
    }

    private void HandleDeviceInfoJson(JsonElement root)
    {
        JsonElement target = root.TryGetProperty("deviceInfo", out var dev) ? dev : root;
        try
        {
            var info = CamoJson.Deserialize<DeviceInfoPayload>(target.GetRawText());
            if (info != null)
            {
                if (info.Cameras.Count == 0)
                {
                    JsonElement camInfoEl = default;
                    bool hasCamInfo = false;
                    if (target.TryGetProperty("cameraInfo", out var cInfo))
                    {
                        camInfoEl = cInfo;
                        hasCamInfo = true;
                    }
                    else if (root.TryGetProperty("cameraInfo", out var rInfo))
                    {
                        camInfoEl = rInfo;
                        hasCamInfo = true;
                    }

                    if (hasCamInfo && camInfoEl.TryGetProperty("cameras", out var camsEl) && camsEl.ValueKind == JsonValueKind.Array)
                    {
                        var camsList = CamoJson.Deserialize<List<CameraDeviceSpec>>(camsEl.GetRawText());
                        if (camsList != null && camsList.Count > 0)
                        {
                            info.Cameras = camsList;
                        }
                    }
                }

                CurrentDeviceInfo = info;
                DeviceInfoUpdated?.Invoke(info);
                LogMessage?.Invoke($"[Device] Updated info: battery={info.BatteryLevel * 100:0}%, orientation={info.Orientation}, cameras={info.Cameras.Count}");
            }
        }
        catch { }
    }

    private void HandleDeviceInfo(byte[] payload)
    {
        if (payload.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            HandleDeviceInfoJson(doc.RootElement);
        }
        catch { }
    }

    /// <summary>
    /// Sends a Camo packet over the active connection with thread-safe serialization.
    /// </summary>
    public async Task SendPacketAsync(CamoPacketType packetType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        Stream? stream = _activeStream;
        if (stream == null)
        {
            throw new InvalidOperationException("No active client connected.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ushort seq = unchecked(++_sequenceCounter);
            await CamoPacketHeader.WritePacketAsync(stream, seq, packetType, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Updates camera and video settings on the phone.
    /// </summary>
    public async Task SendSyncSettingsAsync(SyncSettingsPayload settings, CancellationToken cancellationToken = default)
    {
        CurrentSettings = settings;
        if (_activeStream == null) return;
        byte[] jsonBytes = CamoJson.SerializeToUtf8Bytes(settings);
        await SendPacketAsync(CamoPacketType.SyncSettings, jsonBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wakes up the camera feed on the phone with full reflection-verified WakePayload JSON.
    /// </summary>
    public async Task SendWakeAsync(CancellationToken cancellationToken = default)
    {
        Stream? stream = _activeStream;
        if (stream == null)
        {
            LogMessage?.Invoke("[Session] Cannot SendWake: no active stream.");
            return;
        }

        try
        {
            LogMessage?.Invoke("[Session] Activating camera stream (Wake -> Resume)...");
            string? camId = CurrentSettings.VideoSettings?.CameraUniqueId;
            if (string.IsNullOrEmpty(camId))
                camId = "com.apple.avfoundation.avcapturedevice.built-in_video:0::zoom=1.0";

            var wakePayload = new WakePayload
            {
                ClientInfo = new WakeClientInfo
                {
                    Name = Environment.MachineName,
                    LicenseInfo = null
                },
                Settings = new SyncSettingsPayload
                {
                    VideoSettings = new VideoSettings
                    {
                        CameraUniqueId = camId,
                        Resolution = CurrentSettings.VideoSettings?.Resolution ?? new[] { 1280, 720 }
                    }
                },
                PacketTypesSupported = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27 }
            };

            byte[] wakeBytes = CamoJson.SerializeToUtf8Bytes(wakePayload);
            LogMessage?.Invoke($"[Session] Wake JSON: {Encoding.UTF8.GetString(wakeBytes)}");
            await SendPacketAsync(CamoPacketType.Wake, wakeBytes, cancellationToken).ConfigureAwait(false);

            // Send Resume (10) (0 bytes)
            await SendPacketAsync(CamoPacketType.Resume, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

            LogMessage?.Invoke("[Session] Sent Wake (5) and Resume (10). Streaming activated!");
            HandshakeCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] SendWakeAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pauses or sleeps camera feed on the phone to save battery/thermals.
    /// </summary>
    public async Task SendSleepAsync(CancellationToken cancellationToken = default)
    {
        if (!IsClientConnected) return;
        try
        {
            await SendPacketAsync(CamoPacketType.Sleep, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Session] SendSleepAsync failed: {ex.Message}");
        }
    }

    private void CloseActiveSession()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        if (_activeStream != null)
        {
            try { _activeStream.Dispose(); } catch { }
            _activeStream = null;
        }

        _activeRemoteEndpoint = null;
    }

    /// <summary>
    /// Stops the TCP listener and terminates active sessions.
    /// </summary>
    public Task StopAsync()
    {
        _serverCts?.Cancel();

        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
            _listener = null;
        }

        CloseActiveSession();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _serverCts?.Dispose();
    }
}
