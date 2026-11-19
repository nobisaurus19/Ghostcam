using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostcam.Core.Transport;

/// <summary>
/// High-performance embedded HTTPS & HTTP server for "Ghostcam Safari WebCam" mode.
/// Allows any smartphone (iPhone/Android) on the same Wi-Fi network to stream live camera
/// video directly via Safari/Chrome without installing the Camo mobile app.
/// Supports 4K (3840x2160), 60 FPS, real-time WebSocket binary streaming with HTTP fallback,
/// and bidirectional PC ↔ Phone control synchronization.
/// </summary>
public sealed class WebCameraServer : IDisposable
{
    private TcpListener? _httpsListener;
    private TcpListener? _httpListener;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _serverCts;
    private long _framesLogged;
    private long _unknownBinaryLogged;
    private bool _isDisposed;
    private byte[]? _latestPendingFrame;
    private int _dispatchProcessing = 0;

    public event Action<byte[]>? JpegFrameReceived;
    public event Action<byte[]>? H264ChunkReceived;
    public event Action<byte[]>? AudioPacketReceived;
    public event Action<string>? LogMessage;
    public event Action<string>? ClientConnected;
    public event Action<string>? ClientTelemetryReceived;

    public string LanIp { get; private set; } = "127.0.0.1";
    public int HttpsPort { get; } = 9443;
    public int HttpPort { get; } = 9442;

    public string HttpsUrl => $"https://{LanIp}:{HttpsPort}";
    public string HttpUrl => $"http://{LanIp}:{HttpPort}";
    public bool IsRunning => _serverCts != null && !_serverCts.IsCancellationRequested;

    private readonly object _stateLock = new();
    public string DesiredLens { get; private set; } = "back-wide";
    public int DesiredWidth { get; private set; } = 1280;
    public int DesiredHeight { get; private set; } = 720;
    public int DesiredFps { get; private set; } = 60;
    public bool TorchEnabled { get; private set; } = false;
    public bool IsStreamPaused { get; private set; } = false;
    public bool MicEnabled { get; private set; } = false;
    public double DesiredIso { get; private set; } = 100;
    public double DesiredShutter { get; private set; } = 60;
    public double DesiredWb { get; private set; } = 5400;
    public double DesiredFocus { get; private set; } = 0.5;
    public int CommandVersion { get; private set; } = 1;

    private readonly ConcurrentDictionary<long, WebSocket> _activeWebSockets = new();
    private long _nextWsId;
    private volatile bool _forceJpegFallback;

    public bool HasActiveWebSocketClients => !_activeWebSockets.IsEmpty;
    public bool ForceJpegFallback => _forceJpegFallback;

    public string BuildControlJson()
    {
        lock (_stateLock)
        {
            return $"{{\"v\":{CommandVersion},\"lens\":\"{DesiredLens}\",\"w\":{DesiredWidth},\"h\":{DesiredHeight},\"fps\":{DesiredFps},\"torch\":{(TorchEnabled ? "true" : "false")},\"paused\":{(IsStreamPaused ? "true" : "false")},\"mic\":{(MicEnabled ? "true" : "false")},\"forceJpeg\":{(ForceJpegFallback ? "true" : "false")},\"iso\":{DesiredIso.ToString("0", CultureInfo.InvariantCulture)},\"shutter\":{DesiredShutter.ToString("0", CultureInfo.InvariantCulture)},\"wb\":{DesiredWb.ToString("0", CultureInfo.InvariantCulture)},\"focus\":{DesiredFocus.ToString("0.##", CultureInfo.InvariantCulture)}}}";
        }
    }

    public void RequestForceJpegFallback()
    {
        lock (_stateLock)
        {
            if (_forceJpegFallback) return;
            _forceJpegFallback = true;
            CommandVersion++;
            LogMessage?.Invoke($"[WebCam] Requesting automatic H.264 -> JPEG fallback (v{CommandVersion})");
        }
        _ = BroadcastControlStateAsync();
    }

    public void UpdateDesiredState(
        string? lens = null,
        int? width = null,
        int? height = null,
        int? fps = null,
        bool? torch = null,
        bool? paused = null,
        double? iso = null,
        double? shutter = null,
        double? wb = null,
        double? focus = null,
        bool? mic = null,
        bool notifyClient = true)
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (lens != null && lens != DesiredLens) { DesiredLens = lens; changed = true; }
            if (width.HasValue && width.Value > 0 && width.Value != DesiredWidth) { DesiredWidth = width.Value; changed = true; }
            if (height.HasValue && height.Value > 0 && height.Value != DesiredHeight) { DesiredHeight = height.Value; changed = true; }
            if (fps.HasValue && fps.Value > 0 && fps.Value != DesiredFps) { DesiredFps = fps.Value; changed = true; }
            if (torch.HasValue && torch.Value != TorchEnabled) { TorchEnabled = torch.Value; changed = true; }
            if (paused.HasValue && paused.Value != IsStreamPaused) { IsStreamPaused = paused.Value; changed = true; }
            if (mic.HasValue && mic.Value != MicEnabled) { MicEnabled = mic.Value; changed = true; }
            if (iso.HasValue && Math.Abs(iso.Value - DesiredIso) > 0.001) { DesiredIso = iso.Value; changed = true; }
            if (shutter.HasValue && Math.Abs(shutter.Value - DesiredShutter) > 0.001) { DesiredShutter = shutter.Value; changed = true; }
            if (wb.HasValue && Math.Abs(wb.Value - DesiredWb) > 0.001) { DesiredWb = wb.Value; changed = true; }
            if (focus.HasValue && Math.Abs(focus.Value - DesiredFocus) > 0.001) { DesiredFocus = focus.Value; changed = true; }

            if (changed)
            {
                CommandVersion++;
                LogMessage?.Invoke($"[WebCam] Desired state updated (v{CommandVersion}): lens={DesiredLens}, {DesiredWidth}x{DesiredHeight}@{DesiredFps}fps, torch={TorchEnabled}, paused={IsStreamPaused}, mic={MicEnabled}, iso={DesiredIso:F0}, shutter={DesiredShutter:F0}, wb={DesiredWb:F0}, focus={DesiredFocus:F2}");
            }
        }

        if (changed && notifyClient)
        {
            _ = BroadcastControlStateAsync();
        }
    }

    public void ApplyClientPreferences(int? width = null, int? height = null, int? fps = null, string? lens = null, bool? mic = null)
    {
        lock (_stateLock)
        {
            if (width.HasValue && width.Value > 0) DesiredWidth = width.Value;
            if (height.HasValue && height.Value > 0) DesiredHeight = height.Value;
            if (fps.HasValue && fps.Value > 0) DesiredFps = fps.Value;
            if (!string.IsNullOrEmpty(lens)) DesiredLens = lens;
            if (mic.HasValue) MicEnabled = mic.Value;
            LogMessage?.Invoke($"[WebCam] Phone applied preferences: lens={DesiredLens}, {DesiredWidth}x{DesiredHeight}@{DesiredFps}fps, mic={MicEnabled}");
        }
    }

    public async Task BroadcastControlStateAsync()
    {
        if (_activeWebSockets.IsEmpty) return;
        string json = BuildControlJson();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var seg = new ArraySegment<byte>(bytes);

        foreach (var kvp in _activeWebSockets)
        {
            var ws = kvp.Value;
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    public void RefreshLanIp()
    {
        LanIp = DetectPrimaryLanIpv4();
    }

    public WebCameraServer()
    {
        LanIp = DetectPrimaryLanIpv4();
    }

    /// <summary>
    /// Starts both HTTPS (9443) and HTTP redirect (9442) listeners.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        LanIp = DetectPrimaryLanIpv4();

        try
        {
            _certificate = GenerateSelfSignedCertificate(LanIp);
            LogMessage?.Invoke($"[WebCam] Generated self-signed SSL certificate for {LanIp} & localhost.");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[WebCam] Warning: Certificate generation issue: {ex.Message}");
        }

        try
        {
            _httpsListener = new TcpListener(IPAddress.Any, HttpsPort);
            _httpsListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _httpsListener.Start();
            LogMessage?.Invoke($"[WebCam] HTTPS Live Stream listening on {HttpsUrl}");

            _ = AcceptHttpsConnectionsAsync(_httpsListener, _serverCts.Token);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[WebCam] Error starting HTTPS listener on {HttpsPort}: {ex.Message}");
        }

        try
        {
            _httpListener = new TcpListener(IPAddress.Any, HttpPort);
            _httpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _httpListener.Start();
            LogMessage?.Invoke($"[WebCam] HTTP Redirect listening on {HttpUrl}");

            _ = AcceptHttpConnectionsAsync(_httpListener, _serverCts.Token);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[WebCam] Error starting HTTP listener on {HttpPort}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        _serverCts?.Cancel();
        _serverCts?.Dispose();
        _serverCts = null;

        foreach (var kvp in _activeWebSockets)
        {
            try
            {
                kvp.Value.Abort();
                kvp.Value.Dispose();
            }
            catch { }
        }
        _activeWebSockets.Clear();

        try { _httpsListener?.Stop(); } catch { }
        try { _httpListener?.Stop(); } catch { }
        _httpsListener = null;
        _httpListener = null;

        _certificate?.Dispose();
        _certificate = null;

        LogMessage?.Invoke("[WebCam] Server stopped.");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }

    private async Task AcceptHttpsConnectionsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = HandleHttpsClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    LogMessage?.Invoke($"[WebCam] Accept HTTPS error: {ex.Message}");
            }
        }
    }

    private async Task AcceptHttpConnectionsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = HandleHttpClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    LogMessage?.Invoke($"[WebCam] Accept HTTP error: {ex.Message}");
            }
        }
    }

    private async Task HandleHttpsClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            SslStream? sslStream = null;
            try
            {
                sslStream = new SslStream(client.GetStream(), false);
                if (_certificate != null)
                {
                    await sslStream.AuthenticateAsServerAsync(
                        _certificate,
                        false,
                        SslProtocols.Tls12 | SslProtocols.Tls13,
                        false).ConfigureAwait(false);
                }

                ClientConnected?.Invoke(remoteEp);
                await ProcessHttpStreamAsync(sslStream, remoteEp, isHttps: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    LogMessage?.Invoke($"[WebCam] HTTPS connection error with {remoteEp}: {ex.Message} -> {ex.InnerException?.Message} ({ex.InnerException?.InnerException?.Message})");
            }
            finally
            {
                sslStream?.Dispose();
            }
        }
    }

    private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            string remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
            try
            {
                NetworkStream stream = client.GetStream();
                await ProcessHttpStreamAsync(stream, remoteEp, isHttps: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    LogMessage?.Invoke($"[WebCam] HTTP connection error with {remoteEp}: {ex.Message}");
            }
        }
    }

    private async Task ProcessHttpStreamAsync(Stream stream, string remoteEp, bool isHttps, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        int bufferCount = 0;

        while (!ct.IsCancellationRequested)
        {
            // Read until headers end \r\n\r\n
            int headerEnd = FindHeaderEnd(buffer, bufferCount);
            while (headerEnd < 0)
            {
                if (bufferCount >= buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int read = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), ct).ConfigureAwait(false);
                if (read <= 0) return; // connection closed
                bufferCount += read;
                headerEnd = FindHeaderEnd(buffer, bufferCount);
            }

            string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            string[] headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (headerLines.Length == 0) return;

            string requestLine = headerLines[0];
            string[] reqParts = requestLine.Split(' ');
            if (reqParts.Length < 2) return;

            string method = reqParts[0].ToUpperInvariant();
            string path = reqParts[1];

            // Extract Content-Length
            int contentLength = 0;
            foreach (var line in headerLines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line.Substring(15).Trim(), out contentLength);
                }
            }

            int bodyStartIndex = headerEnd + 4;
            int bodyAvailable = bufferCount - bodyStartIndex;

            // WebSocket Upgrade Handler (for real-time binary JPEG streaming & control push)
            if (method == "GET" && path.StartsWith("/ws"))
            {
                string? wsKey = null;
                foreach (var line in headerLines)
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        wsKey = line.Substring(18).Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(wsKey))
                {
                    string badReq = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(badReq), ct).ConfigureAwait(false);
                    return;
                }

                string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                string upgradeResp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(upgradeResp), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                Stream wsStream = bodyAvailable > 0
                    ? new PrefixStream(buffer[bodyStartIndex..(bodyStartIndex + bodyAvailable)], stream)
                    : stream;

                long wsId = Interlocked.Increment(ref _nextWsId);
                using WebSocket ws = WebSocket.CreateFromStream(wsStream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(10));
                _activeWebSockets[wsId] = ws;
                LogMessage?.Invoke($"[WebCam] WebSocket #{wsId} client connected from {remoteEp}");

                try
                {
                    // Send initial control state
                    string initControl = BuildControlJson();
                    byte[] initBytes = Encoding.UTF8.GetBytes(initControl);
                    await ws.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

                    byte[] recvBuf = new byte[65536];
                    using var ms = new MemoryStream();
                    const int maxMessageSize = 16 * 1024 * 1024; // 16 MB limit

                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            LogMessage?.Invoke($"[WebCam] WS #{wsId} receive error: {ex.Message}");
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try
                            {
                                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct).ConfigureAwait(false);
                            }
                            catch { }
                            break;
                        }

                        if (ms.Length + result.Count > maxMessageSize)
                        {
                            LogMessage?.Invoke($"[WebCam] WS #{wsId} message exceeded 16MB limit, discarding");
                            ms.SetLength(0);
                            continue;
                        }

                        ms.Write(recvBuf, 0, result.Count);

                        if (result.EndOfMessage)
                        {
                            byte[] messageBytes = ms.ToArray();
                            ms.SetLength(0);

                            if (result.MessageType == WebSocketMessageType.Binary)
                            {
                                if (messageBytes.Length > 0)
                                {
                                    if (messageBytes.Length > 4 &&
                                        messageBytes[0] == (byte)'A' &&
                                        messageBytes[1] == (byte)'U' &&
                                        messageBytes[2] == (byte)'D' &&
                                        messageBytes[3] == (byte)'0')
                                    {
                                        byte[] pcm = messageBytes[4..];
                                        AudioPacketReceived?.Invoke(pcm);
                                    }
                                    else if ((messageBytes.Length >= 4 && messageBytes[0] == 0 && messageBytes[1] == 0 && messageBytes[2] == 0 && messageBytes[3] == 1) ||
                                             (messageBytes.Length >= 3 && messageBytes[0] == 0 && messageBytes[1] == 0 && messageBytes[2] == 1))
                                    {
                                        long count = Interlocked.Increment(ref _framesLogged);
                                        if (count <= 5 || count % 150 == 0)
                                        {
                                            LogMessage?.Invoke($"[WebCam] WS H264 chunk #{count} ({messageBytes.Length} bytes) from {remoteEp}");
                                        }
                                        H264ChunkReceived?.Invoke(messageBytes);
                                    }
                                    else if (messageBytes.Length >= 2 && messageBytes[0] == 0xFF && messageBytes[1] == 0xD8)
                                    {
                                        long count = Interlocked.Increment(ref _framesLogged);
                                        if (count <= 5 || count % 150 == 0)
                                        {
                                            LogMessage?.Invoke($"[WebCam] WS JPEG frame #{count} ({messageBytes.Length} bytes) from {remoteEp}");
                                        }
                                        DispatchLatestFrame(messageBytes);
                                    }
                                    else
                                    {
                                        long unknownCount = Interlocked.Increment(ref _unknownBinaryLogged);
                                        if (unknownCount <= 5 || unknownCount % 60 == 0)
                                        {
                                            string headHex = BitConverter.ToString(messageBytes, 0, Math.Min(8, messageBytes.Length));
                                            LogMessage?.Invoke($"[WebCam] Ignored unknown binary frame #{unknownCount} ({messageBytes.Length} bytes, head={headHex}) from {remoteEp}");
                                        }
                                    }
                                }
                            }
                            else if (result.MessageType == WebSocketMessageType.Text)
                            {
                                string text = Encoding.UTF8.GetString(messageBytes);
                                ClientTelemetryReceived?.Invoke(text);
                                try
                                {
                                    using var doc = JsonDocument.Parse(text);
                                    if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "userPrefs")
                                    {
                                        int? w = doc.RootElement.TryGetProperty("w", out var wp) && wp.TryGetInt32(out int wi) ? wi : null;
                                        int? h = doc.RootElement.TryGetProperty("h", out var hp) && hp.TryGetInt32(out int hi) ? hi : null;
                                        int? fps = doc.RootElement.TryGetProperty("fps", out var fp) && fp.TryGetInt32(out int fi) ? fi : null;
                                        string? lens = doc.RootElement.TryGetProperty("lens", out var lp) ? lp.GetString() : null;
                                        bool? mic = doc.RootElement.TryGetProperty("mic", out var mp) && (mp.ValueKind == JsonValueKind.True || mp.ValueKind == JsonValueKind.False) ? mp.GetBoolean() : null;
                                        ApplyClientPreferences(w, h, fps, lens, mic);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                finally
                {
                    _activeWebSockets.TryRemove(wsId, out _);
                    LogMessage?.Invoke($"[WebCam] WS #{wsId} client disconnected ({remoteEp})");
                }

                return;
            }

            // Handle HTTP Redirection to HTTPS if accessed over plain HTTP
            if (!isHttps)
            {
                string redirectHtml = GetHttpRedirectHtml();
                byte[] htmlBytes = Encoding.UTF8.GetBytes(redirectHtml);
                string response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                await stream.WriteAsync(htmlBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }

            // CORS preflight
            if (method == "OPTIONS")
            {
                string corsResp = "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, Content-Length\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(corsResp), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                int consumed = bodyStartIndex;
                bufferCount -= consumed;
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, bufferCount);
                }
                continue;
            }

            // Serve Camera Transmitter Web Page
            if (method == "GET" && (path == "/" || path.StartsWith("/?")))
            {
                string html = GetMobileHtml();
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                string resp = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct).ConfigureAwait(false);
                await stream.WriteAsync(htmlBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                int consumed = bodyStartIndex;
                bufferCount -= consumed;
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, bufferCount);
                }
                continue;
            }

            // Status poll endpoint for HTTP-fallback clients when stream is paused
            if (method == "GET" && path.StartsWith("/status"))
            {
                string controlJson = BuildControlJson();
                byte[] controlBytes = Encoding.UTF8.GetBytes(controlJson);
                string okResp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {controlBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(okResp), ct).ConfigureAwait(false);
                await stream.WriteAsync(controlBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                int consumed = bodyStartIndex;
                bufferCount -= consumed;
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, bufferCount);
                }
                continue;
            }

            // Ingest JPEG frame from Safari / Chrome (HTTP fallback)
            if (method == "POST" && path.StartsWith("/frame"))
            {
                if (contentLength <= 0 || contentLength > 32 * 1024 * 1024)
                {
                    string badReq = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(badReq), ct).ConfigureAwait(false);
                    return;
                }

                byte[] frameBytes = new byte[contentLength];
                int copied = Math.Min(bodyAvailable, contentLength);
                if (copied > 0)
                {
                    Buffer.BlockCopy(buffer, bodyStartIndex, frameBytes, 0, copied);
                }

                int remaining = contentLength - copied;
                int frameOffset = copied;
                while (remaining > 0)
                {
                    int r = await stream.ReadAsync(frameBytes.AsMemory(frameOffset, remaining), ct).ConfigureAwait(false);
                    if (r <= 0) return;
                    frameOffset += r;
                    remaining -= r;
                }

                // Dispatch frame asynchronously so socket write is not blocked
                if (frameBytes.Length > 0)
                {
                    long count = Interlocked.Increment(ref _framesLogged);
                    if (count <= 5 || count % 150 == 0)
                    {
                        LogMessage?.Invoke($"[WebCam] Received JPEG frame #{count} ({frameBytes.Length} bytes) from {remoteEp}");
                    }
                    DispatchLatestFrame(frameBytes);
                }

                // Send fast 200 OK with JSON desired state
                string controlJson = BuildControlJson();
                byte[] controlBytes = Encoding.UTF8.GetBytes(controlJson);
                string okResp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {controlBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(okResp), ct).ConfigureAwait(false);
                await stream.WriteAsync(controlBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                // Slide leftover buffer if pipelined
                int totalConsumed = bodyStartIndex + Math.Min(bodyAvailable, contentLength);
                bufferCount -= totalConsumed;
                if (bufferCount > 0)
                {
                    Buffer.BlockCopy(buffer, totalConsumed, buffer, 0, bufferCount);
                }
                continue;
            }

            // Default 404
            string notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(notFound), ct).ConfigureAwait(false);
            return;
        }
    }

    private void DispatchLatestFrame(byte[] frameBytes)
    {
        if (frameBytes.Length >= 2 && frameBytes[0] == 0xFF && frameBytes[1] == 0xD8)
        {
            _forceJpegFallback = false;
        }
        Interlocked.Exchange(ref _latestPendingFrame, frameBytes);
        if (Interlocked.CompareExchange(ref _dispatchProcessing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        byte[]? frame = Interlocked.Exchange(ref _latestPendingFrame, null);
                        if (frame != null)
                        {
                            try
                            {
                                JpegFrameReceived?.Invoke(frame);
                            }
                            catch (Exception ex)
                            {
                                LogMessage?.Invoke($"[WebCam] Frame dispatch error: {ex.Message}");
                            }
                            continue;
                        }

                        Interlocked.Exchange(ref _dispatchProcessing, 0);
                        if (Volatile.Read(ref _latestPendingFrame) != null &&
                            Interlocked.CompareExchange(ref _dispatchProcessing, 1, 0) == 0)
                        {
                            continue;
                        }

                        break;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _dispatchProcessing, 0);
                }
            });
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i < length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static string DetectPrimaryLanIpv4()
    {
        try
        {
            foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netInterface.OperationalStatus != OperationalStatus.Up ||
                    netInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                string desc = netInterface.Description;
                string name = netInterface.Name;
                if (desc.Contains("Hamachi", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Npcap", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hamachi", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ipProps = netInterface.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }

        return "192.168.0.110";
    }

    private static X509Certificate2 GenerateSelfSignedCertificate(string lanIp)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string ghostcamDir = Path.Combine(appData, "Ghostcam");
        Directory.CreateDirectory(ghostcamDir);
        string pfxPath = Path.Combine(ghostcamDir, $"webcam_cert_{lanIp}.pfx");

        if (File.Exists(pfxPath))
        {
            try
            {
                var existingCert = new X509Certificate2(
                    pfxPath,
                    "ghostcam",
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                // Verify validity: not expired and valid for at least 30 more days
                if (existingCert.NotAfter > DateTime.UtcNow.AddDays(30) && existingCert.NotBefore <= DateTime.UtcNow)
                {
                    return existingCert;
                }
                existingCert.Dispose();
            }
            catch
            {
                // File corrupted, regenerate
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN=Ghostcam-{lanIp}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(lanIp, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);

        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        byte[] pfxBytes = cert.Export(X509ContentType.Pfx, "ghostcam");
        try
        {
            File.WriteAllBytes(pfxPath, pfxBytes);
        }
        catch { }

        return new X509Certificate2(
            pfxBytes,
            "ghostcam",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private string GetHttpRedirectHtml()
    {
        return $@"<!DOCTYPE html>
<html lang=""th"">
<head>
<meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1,user-scalable=no"">
<title>Ghostcam Studio Mobile</title>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #f8fafc; text-align: center; padding: 24px 16px; margin: 0; }}
.card {{ background: #1e293b; border-radius: 20px; padding: 28px 20px; max-width: 420px; margin: 20px auto; box-shadow: 0 10px 25px rgba(0,0,0,0.4); border: 1px solid #334155; }}
h1 {{ color: #38bdf8; font-size: 22px; margin-bottom: 8px; }}
p {{ color: #94a3b8; font-size: 15px; line-height: 1.5; margin: 12px 0 24px; }}
.btn {{ display: block; background: linear-gradient(135deg, #0284c7, #0369a1); color: #fff; text-decoration: none; padding: 16px; border-radius: 14px; font-weight: 600; font-size: 17px; box-shadow: 0 4px 14px rgba(2,132,199,0.4); }}
.tip {{ font-size: 13px; color: #64748b; margin-top: 20px; text-align: left; line-height: 1.6; background: #0f172a; padding: 12px 14px; border-radius: 10px; border-left: 3px solid #f59e0b; }}
</style>
<script>
setTimeout(function() {{ window.location.href = '{HttpsUrl}'; }}, 1500);
</script>
</head>
<body>
<div class=""card"">
  <h1>📷 Ghostcam Studio WebCam</h1>
  <p>Safari บน iPhone ต้องการ HTTPS เพื่อเข้าถึงกล้อง กำลังนำท่านไปยังลิงก์ปลอดภัย...</p>
  <a href=""{HttpsUrl}"" class=""btn"">แตะที่นี่เพื่อเปิดกล้อง (HTTPS)</a>
  <div class=""tip"">
    <strong>⚠️ หาก Safari ขึ้นเตือนความปลอดภัย:</strong><br>
    แตะ <em>'Show Details' (ดูรายละเอียด)</em> ด้านล่าง แล้วแตะ <em>'visit this website' (เข้าชมเว็บไซต์นี้)</em> เพื่ออนุญาตให้ใช้งานกล้องได้ทันที
  </div>
</div>
</body>
</html>";
    }

    private string GetMobileHtml()
    {
        int w, h, fps, v;
        string lens;
        lock (_stateLock)
        {
            w = DesiredWidth;
            h = DesiredHeight;
            fps = DesiredFps;
            lens = DesiredLens;
            v = CommandVersion;
        }

        return MobileHtmlTemplate
            .Replace("__INIT_W__", w.ToString())
            .Replace("__INIT_H__", h.ToString())
            .Replace("__INIT_FPS__", fps.ToString())
            .Replace("__INIT_LENS__", lens)
            .Replace("__INIT_CMD_V__", v.ToString());
    }

    private const string MobileHtmlTemplate = @"<!DOCTYPE html>
<html lang=""th"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,user-scalable=no,viewport-fit=cover"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<title>Ghostcam Studio Mobile</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
<link href=""https://fonts.googleapis.com/css2?family=DotGothic16&family=IBM+Plex+Mono:wght@500;700&family=IBM+Plex+Sans+Thai:wght@400;500;600;700&display=swap"" rel=""stylesheet"">
<style>
:root {
  --bg: #ffffff;
  --grid: #e2e4ea;
  --card: #ffffff;
  --input: #f6f8fb;
  --border: #e2e4ea;
  --border-strong: #000000;
  --ink: #000000;
  --muted: #858890;
  --cta-bg: #000000;
  --cta-fg: #ffffff;
  --hud-bg: rgba(255,255,255,0.94);
  --green: #00b995;
  --red: #ff4130;
  --yellow: #eeff5a;
  --font-pixel: 'DotGothic16', 'IBM Plex Mono', monospace;
  --font-mono: 'IBM Plex Mono', ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  --font-sans: 'IBM Plex Sans Thai', -apple-system, BlinkMacSystemFont, sans-serif;
}
body.yoyu-dark {
  --bg: #0b0b0e;
  --grid: #191920;
  --card: #111115;
  --input: #18181e;
  --border: #26262f;
  --border-strong: #ffffff;
  --ink: #ffffff;
  --muted: #858890;
  --cta-bg: #ffffff;
  --cta-fg: #000000;
  --hud-bg: rgba(17,17,21,0.94);
}
* { box-sizing: border-box; margin: 0; padding: 0; user-select: none; -webkit-user-select: none; border-radius: 0; }
body { background: #000; color: var(--ink); font-family: var(--font-sans); position: relative; height: 100vh; width: 100vw; overflow: hidden; }
header { position: absolute; top: 0; left: 0; right: 0; z-index: 50; padding: calc(8px + env(safe-area-inset-top)) 10px 8px; background: var(--hud-bg); border-bottom: 1px solid var(--border-strong); color: var(--ink); display: flex; align-items: center; justify-content: space-between; }
.logo { font-family: var(--font-pixel); font-weight: 400; font-size: 12px; letter-spacing: 0.06em; color: var(--ink); display: flex; align-items: center; gap: 6px; white-space: nowrap; flex-shrink: 0; }
.header-actions { display: flex; align-items: center; gap: 4px; flex-shrink: 0; }
.badge { background: var(--card); color: var(--green); font-family: var(--font-mono); font-size: 10px; padding: 3px 7px; border-radius: 0; font-weight: 700; border: 1px solid var(--green); white-space: nowrap; }
.preview-container { position: relative; height: 100vh; width: 100vw; background: #000; display: flex; align-items: center; justify-content: center; overflow: hidden; }
video { width: 100%; height: 100%; object-fit: cover; }
video.oled-sleep { width: 2px !important; height: 2px !important; opacity: 0.01 !important; }
canvas { display: none; }
.overlay-stats { position: absolute; top: calc(52px + env(safe-area-inset-top)); left: 50%; transform: translateX(-50%); background: var(--hud-bg); padding: 4px 10px; border-radius: 0; font-size: 10px; font-family: var(--font-mono); letter-spacing: 0.04em; color: var(--ink); border: 1px solid var(--border-strong); white-space: nowrap; z-index: 20; pointer-events: none; }
.lens-bar { position: absolute; bottom: calc(74px + env(safe-area-inset-bottom)); left: 50%; transform: translateX(-50%); background: var(--hud-bg); padding: 2px; border-radius: 0; border: 1px solid var(--border-strong); display: flex; gap: 2px; z-index: 30; }
.lens-btn { background: transparent; color: var(--ink); border: none; padding: 6px 12px; border-radius: 0; font-family: var(--font-mono); font-size: 11px; font-weight: 700; cursor: pointer; transition: all 0.12s; flex: none; }
.lens-btn.active { background: var(--cta-bg); color: var(--cta-fg); }
.controls { position: absolute; bottom: 0; left: 0; right: 0; z-index: 50; border-radius: 0; padding: 8px 8px calc(8px + env(safe-area-inset-bottom)); display: flex; gap: 6px; justify-content: space-around; background: var(--hud-bg); border-top: 1px solid var(--border-strong); }
button { background: var(--input); color: var(--ink); border: 1px solid var(--border); padding: 10px 4px; border-radius: 0; font-family: var(--font-sans); font-size: 11px; font-weight: 700; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 4px; transition: all 0.12s; white-space: nowrap; flex: 1; min-width: 0; }
button:active { transform: scale(0.96); opacity: 0.88; }
.btn-mini, .btn-cool, .btn-oled { background: var(--input); border: 1px solid var(--border-strong); color: var(--ink); border-radius: 0; flex: none; padding: 4px 7px; font-family: var(--font-mono); font-size: 10px; font-weight: 700; }
.btn-cool { background: var(--cta-bg); border-color: var(--cta-bg); color: var(--cta-fg); }
.btn-cool.off { background: var(--input); border-color: var(--border); color: var(--muted); }
.btn-torch.active { background: var(--yellow); border-color: #000000; color: #000000; }
#btnCodec.active { background: var(--cta-bg); border-color: var(--border-strong); color: var(--cta-fg); }
#btnMic.active { background: var(--green); border-color: #000000; color: #000000; }
.start-overlay { position: absolute; inset: 0; background-color: var(--bg); background-image: linear-gradient(to right, var(--grid) 1px, transparent 1px), linear-gradient(to bottom, var(--grid) 1px, transparent 1px); background-size: 8px 8px; display: flex; flex-direction: column; align-items: center; justify-content: center; z-index: 80; padding: 24px; text-align: center; }
.start-card { max-width: 340px; width: 100%; background: var(--card); border-radius: 0; padding: 28px 22px; border: 1px solid var(--border-strong); color: var(--ink); }
.start-icon { display: flex; align-items: center; justify-content: center; color: var(--ink); margin-bottom: 14px; }
.start-title { font-family: var(--font-pixel); font-size: 18px; color: var(--ink); font-weight: 400; margin-bottom: 8px; letter-spacing: 0.04em; }
.start-desc { font-size: 13px; color: var(--muted); line-height: 1.55; margin-bottom: 22px; }
.btn-large { width: 100%; justify-content: center; padding: 14px; font-size: 14px; font-family: var(--font-pixel); letter-spacing: 0.04em; border-radius: 0; background: var(--cta-bg); border: 1px solid var(--border-strong); color: var(--cta-fg); font-weight: 400; }
.oled-overlay { position: fixed; inset: 0; background: #000; z-index: 99999; display: none; flex-direction: column; align-items: center; justify-content: center; text-align: center; cursor: pointer; padding: 24px; font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace; }
.oled-icon { font-size: 32px; margin-bottom: 12px; opacity: 0.4; }
.oled-title { font-family: var(--font-pixel); font-size: 14px; font-weight: 700; color: #ffffff; opacity: 0.5; margin-bottom: 6px; letter-spacing: 0.04em; }
.oled-desc { font-family: var(--font-mono); font-size: 11px; color: #64748b; line-height: 1.6; max-width: 280px; }
.oled-hint { font-family: var(--font-mono); margin-top: 16px; font-size: 10px; color: var(--green); opacity: 0.65; }
.pause-banner { position: absolute; inset: 0; background: rgba(0,0,0,0.85); display: none; align-items: center; justify-content: center; z-index: 40; font-family: var(--font-pixel); font-size: 16px; font-weight: 700; letter-spacing: 0.06em; color: var(--yellow); }
@keyframes yoyuGhostBob { 0%, 100% { transform: translateY(0px); } 25% { transform: translateY(-2px); } 75% { transform: translateY(1px); } }
@keyframes yoyuGhostWave { 0%, 100% { transform: translateX(0px); } 50% { transform: translateX(1px); } }
@keyframes yoyuGhostBlink { 0%, 86%, 94%, 100% { opacity: 0; } 88%, 92% { opacity: 1; } }
@keyframes yoyuPixelTwinkle { 0%, 45%, 100% { opacity: 0; } 50%, 80% { opacity: 1; } }
.logo svg { animation: yoyuGhostBob 1.6s steps(1, end) infinite; overflow: visible; }
.start-icon svg { animation: yoyuGhostBob 1.6s steps(1, end) infinite; overflow: visible; }
.ghost-feet { animation: yoyuGhostWave 0.8s steps(1, end) infinite; }
.ghost-blink { animation: yoyuGhostBlink 3.2s linear infinite; }
.ghost-spark-g { animation: yoyuPixelTwinkle 2.2s steps(1, end) infinite; }
.ghost-spark-c { animation: yoyuPixelTwinkle 2.2s steps(1, end) 1.1s infinite; }
</style>
</head>
<body class=""cool-mode"">
<header>
  <div class=""logo"">
    <svg width=""18"" height=""18"" viewBox=""0 0 12 12"" shape-rendering=""crispEdges"" fill=""currentColor"" aria-hidden=""true"">
      <rect class=""ghost-spark-g"" x=""11"" y=""1"" width=""1"" height=""1"" fill=""var(--green)""/>
      <rect class=""ghost-spark-c"" x=""0"" y=""2"" width=""1"" height=""1"" fill=""#00b4d8""/>
      <rect x=""4"" y=""1"" width=""4"" height=""1""/>
      <rect x=""3"" y=""2"" width=""6"" height=""1""/>
      <rect x=""2"" y=""3"" width=""8"" height=""1""/>
      <rect x=""2"" y=""4"" width=""2"" height=""2""/>
      <rect x=""5"" y=""4"" width=""2"" height=""2""/>
      <rect x=""8"" y=""4"" width=""2"" height=""2""/>
      <rect class=""ghost-blink"" x=""3"" y=""4"" width=""6"" height=""2""/>
      <rect x=""2"" y=""6"" width=""8"" height=""4""/>
      <g class=""ghost-feet"">
        <rect x=""2"" y=""10"" width=""2"" height=""1""/>
        <rect x=""5"" y=""10"" width=""2"" height=""1""/>
        <rect x=""8"" y=""10"" width=""2"" height=""1""/>
      </g>
    </svg>
    <span>GHOSTCAM</span>
  </div>
  <div class=""header-actions"">
    <button id=""btnTheme"" class=""btn-mini"" title=""สลับธีม Light / Dark"">◐</button>
    <button id=""btnCool"" class=""btn-cool"" title=""ลดความร้อนและประหยัดพลังงาน iPhone"">❄️ ลดร้อน: ON</button>
    <button id=""btnCodec"" class=""btn-mini"" title=""สลับโหมดส่งภาพ JPEG / H.264"">JPEG</button>
    <button id=""btnOled"" class=""btn-oled"" title=""ดับหน้าจอมืดประหยัดแบตเตอรี่"">🌙 พักหน้าจอ</button>
    <div class=""badge"" id=""statusBadge"">● READY</div>
  </div>
</header>
<div class=""preview-container"">
  <div class=""start-overlay"" id=""startOverlay"">
    <div class=""start-card"">
      <div class=""start-icon"">
        <svg width=""52"" height=""52"" viewBox=""0 0 12 12"" shape-rendering=""crispEdges"" fill=""currentColor"" aria-hidden=""true"">
          <rect class=""ghost-spark-g"" x=""11"" y=""1"" width=""1"" height=""1"" fill=""var(--green)""/>
          <rect class=""ghost-spark-c"" x=""0"" y=""2"" width=""1"" height=""1"" fill=""#00b4d8""/>
          <rect x=""4"" y=""1"" width=""4"" height=""1""/>
          <rect x=""3"" y=""2"" width=""6"" height=""1""/>
          <rect x=""2"" y=""3"" width=""8"" height=""1""/>
          <rect x=""2"" y=""4"" width=""2"" height=""2""/>
          <rect x=""5"" y=""4"" width=""2"" height=""2""/>
          <rect x=""8"" y=""4"" width=""2"" height=""2""/>
          <rect class=""ghost-blink"" x=""3"" y=""4"" width=""6"" height=""2""/>
          <rect x=""2"" y=""6"" width=""8"" height=""4""/>
          <g class=""ghost-feet"">
            <rect x=""2"" y=""10"" width=""2"" height=""1""/>
            <rect x=""5"" y=""10"" width=""2"" height=""1""/>
            <rect x=""8"" y=""10"" width=""2"" height=""1""/>
          </g>
        </svg>
      </div>
      <div class=""start-title"">GHOSTCAM WEBCAM</div>
      <div class=""start-desc"">แตะเพื่อเริ่มถ่ายทอดสดกล้องไปยัง Ghostcam Studio บนพีซีของคุณ (ไม่ต้องลงแอพเพิ่มเติม)</div>
      <button id=""btnStart"" class=""btn-large"">⚡ เริ่มถ่ายทอดสดกล้อง (START)</button>
    </div>
  </div>
  <div class=""pause-banner"" id=""pauseBanner"">⏸️ SENSOR SLEEP (PAUSED BY PC)</div>
  <div class=""overlay-stats"" id=""stats"">1280x720 • 0.0 FPS • READY</div>
  <div class=""lens-bar"" id=""lensBar"">
    <button class=""lens-btn"" data-lens=""back-ultra"" id=""btnLensUltra"">0.5x</button>
    <button class=""lens-btn active"" data-lens=""back-wide"" id=""btnLensWide"">1x</button>
    <button class=""lens-btn"" data-lens=""back-tele"" id=""btnLensTele"">2x</button>
    <button class=""lens-btn"" data-lens=""front"" id=""btnLensFront"">🤳 Selfie</button>
  </div>
  <video id=""v"" autoplay playsinline muted></video>
  <canvas id=""c""></canvas>
</div>
<div class=""controls"">
  <button id=""btnSwitch"">🔄 กล้อง</button>
  <button id=""btnTorch"" class=""btn-torch"">🔦 ไฟ</button>
  <button id=""btnRes"">📺 720p</button>
  <button id=""btnQuality"" style=""background:var(--cta-bg);color:var(--cta-fg);border-color:var(--border-strong);"">⚡ 60 FPS</button>
  <button id=""btnMic"">🎙️ ไมค์</button>
  <button id=""btnFs"">⛶ เต็มจอ</button>
</div>

<div class=""oled-overlay"" id=""oledOverlay"">
  <div class=""oled-icon"">🌙</div>
  <div class=""oled-title"">โหมดพักหน้าจอดำ OLED</div>
  <div class=""oled-desc"">กำลังส่งภาพสดความเร็วสูงไปยัง Ghostcam Studio หน้าจอถูกดับเพื่อถนอมแบตเตอรี่และความร้อน</div>
  <div class=""oled-hint"">💡 แตะที่ใดก็ได้บนหน้าจอเพื่อเปิดภาพพรีวิวกลับมา</div>
</div>

<script>
let targetWidth = __INIT_W__;
let targetHeight = __INIT_H__;
let targetFps = __INIT_FPS__;
let currentLens = '__INIT_LENS__';
let serverCommandVersion = __INIT_CMD_V__;
let currentFacing = (currentLens === 'front') ? 'user' : 'environment';

let coolMode = true;
let isStreaming = false;
let isPaused = false;
let torchOn = false;
let wakeLock = null;
let frameCount = 0;
let lastFpsTime = performance.now();
let activeFps = '0.0';

let isRestartingCamera = false;
let pendingRestart = false;
let softwareCropZoom = 1.0;
let videoDeviceList = [];
let activePhysicalLens = null;

let useWebCodecs = false;
let videoEncoder = null;
let encoderConfigKey = '';
let encoderInitPromise = null;
let h264FrameIndex = 0;
let h264Description = null;
let h264EnableTime = 0;
let lastH264OutputTime = 0;

let micActive = false;
let micStream = null;
let audioCtx = null;
let micSourceNode = null;
let micProcessorNode = null;
let micGainNode = null;

let videoEl = document.getElementById('v');
let canvasEl = document.getElementById('c');
let canvasEl2 = document.createElement('canvas');
let encodeCanvases = [canvasEl, canvasEl2];
let encodeCtxs = encodeCanvases.map(c => c.getContext('2d', { alpha: false, desynchronized: true }) || c.getContext('2d'));
encodeCtxs.forEach(c => { if (c) c.imageSmoothingEnabled = false; });
let ctx = encodeCtxs[0];
let canvasBusy = [false, false];
let encodesInFlight = 0;
let nextEncodeSeq = 0;
let lastDispatchedSeq = 0;
let statsEl = document.getElementById('stats');
let statusBadge = document.getElementById('statusBadge');
let startOverlay = document.getElementById('startOverlay');
let pauseBanner = document.getElementById('pauseBanner');
let oledOverlay = document.getElementById('oledOverlay');
let btnCool = document.getElementById('btnCool');
let btnTorch = document.getElementById('btnTorch');
let btnCodec = document.getElementById('btnCodec');
let btnMic = document.getElementById('btnMic');
let currentTrack = null;

if (btnCodec && typeof VideoEncoder === 'undefined') {
  btnCodec.style.display = 'none';
}

function findPhysicalLensDeviceId(lens) {
  if (!videoDeviceList || videoDeviceList.length === 0) return null;
  if (lens !== 'back-ultra' && lens !== 'back-tele') return null;

  for (const dev of videoDeviceList) {
    const label = (dev.label || '').toLowerCase();
    if (!label) continue;
    if (label.includes('front') || label.includes('selfie') || label.includes('user')) continue;
    if (lens === 'back-ultra' && (/ultra|0\.5x?/.test(label))) {
      return dev.deviceId;
    }
    if (lens === 'back-tele' && (/tele|telephoto|2x|3x|5x|periscope/.test(label))) {
      return dev.deviceId;
    }
  }
  return null;
}

let ws = null;
let wsReconnectTimer = null;
let frameLoopGen = 0;
let lastFrameSendTime = 0;
let lastFrameTickTime = 0;
let frameWatchdogTimer = null;
let httpFallbackInFlight = 0;
let statusPollTimer = null;

async function acquireWakeLock() {
  try {
    if ('wakeLock' in navigator) {
      wakeLock = await navigator.wakeLock.request('screen');
      wakeLock.addEventListener('release', () => { wakeLock = null; });
    }
  } catch (err) {}
}

document.addEventListener('visibilitychange', async () => {
  if (document.visibilityState === 'visible') {
    await acquireWakeLock();
  }
});

async function startCamera() {
  if (isRestartingCamera) {
    pendingRestart = true;
    return;
  }
  isRestartingCamera = true;

  try {
    if (videoEl.srcObject) {
      try {
        videoEl.srcObject.getTracks().forEach(t => t.stop());
      } catch (e) {}
      videoEl.srcObject = null;
    }

    const isFront = (currentLens === 'front');
    currentFacing = isFront ? 'user' : 'environment';
    activePhysicalLens = null;
    softwareCropZoom = 1.0;
    videoEl.style.transform = '';

    const matchedId = findPhysicalLensDeviceId(currentLens);
    let constraints = {
      audio: false,
      video: matchedId
        ? {
            deviceId: { exact: matchedId },
            width: { ideal: targetWidth },
            height: { ideal: targetHeight },
            frameRate: { ideal: targetFps }
          }
        : {
            facingMode: currentFacing,
            width: { ideal: targetWidth },
            height: { ideal: targetHeight },
            frameRate: { ideal: targetFps }
          }
    };

    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia(constraints);
      if (matchedId) activePhysicalLens = currentLens;
    } catch (err) {
      if (matchedId) {
        constraints.video = {
          facingMode: currentFacing,
          width: { ideal: targetWidth },
          height: { ideal: targetHeight },
          frameRate: { ideal: targetFps }
        };
        try {
          stream = await navigator.mediaDevices.getUserMedia(constraints);
        } catch (err2) {
          if (targetFps > 30 || targetWidth > 1920) {
            constraints.video.frameRate = { ideal: 30 };
            stream = await navigator.mediaDevices.getUserMedia(constraints);
          } else {
            throw err2;
          }
        }
      } else if (targetFps > 30 || targetWidth > 1920) {
        constraints.video.frameRate = { ideal: 30 };
        stream = await navigator.mediaDevices.getUserMedia(constraints);
      } else {
        throw err;
      }
    }

    try {
      if (navigator.mediaDevices && navigator.mediaDevices.enumerateDevices) {
        const devs = await navigator.mediaDevices.enumerateDevices();
        videoDeviceList = devs.filter(d => d.kind === 'videoinput');
      }
    } catch (e) {}

    let track = stream.getVideoTracks()[0];
    let s = track.getSettings ? track.getSettings() : {};

    // iOS Safari fallback: WebKit on iOS often refuses 4K at 60 FPS and gives 1080p/720p.
    // If 4K was requested with 60 FPS and negotiated long side < 2500, retry once with ideal 30 FPS.
    if (targetWidth >= 3840 && targetFps >= 60 && Math.max(s.width || 0, s.height || 0) < 2500) {
      try {
        stream.getTracks().forEach(t => t.stop());
        const retryConstraints = {
          audio: false,
          video: Object.assign({}, constraints.video, {
            width: { ideal: 3840 },
            height: { ideal: 2160 },
            frameRate: { ideal: 30 }
          })
        };
        const retryStream = await navigator.mediaDevices.getUserMedia(retryConstraints);
        const retryTrack = retryStream.getVideoTracks()[0];
        const retrySettings = retryTrack.getSettings ? retryTrack.getSettings() : {};
        stream = retryStream;
        track = retryTrack;
        s = retrySettings;
      } catch (retryErr) {
        stream = await navigator.mediaDevices.getUserMedia(constraints);
        track = stream.getVideoTracks()[0];
        s = track.getSettings ? track.getSettings() : {};
      }
    }

    videoEl.srcObject = stream;
    currentTrack = track;
    await videoEl.play();

    applyLensZoom(currentLens);
    if (torchOn) applyTorch(true);
    if (lastHardwareCmd) applyHardwareConstraints(lastHardwareCmd);

    statusBadge.textContent = '🟢 STREAMING';
    statusBadge.style.color = 'var(--green)';
    statusBadge.style.borderColor = 'var(--green)';
    startOverlay.style.display = 'none';

    await acquireWakeLock();

    const caps = track.getCapabilities ? track.getCapabilities() : {};
    sendWsTelemetry({
      type: 'caps',
      w: s.width,
      h: s.height,
      fps: s.frameRate,
      maxW: caps.width ? caps.width.max : undefined,
      maxH: caps.height ? caps.height.max : undefined,
      maxFps: caps.frameRate ? caps.frameRate.max : undefined,
      reqW: targetWidth,
      reqH: targetHeight,
      reqFps: targetFps
    });

    updateResAndFpsButtonsUI();
    updateLensButtonsUI(currentLens);

    if (!isStreaming) {
      isStreaming = true;
      connectWs();
      startStatusPollingFallback();
    }
    ensureFrameWatchdog();
    scheduleNextFrame(++frameLoopGen);
  } catch (err) {
    statusBadge.textContent = '❌ ERROR';
    statusBadge.style.color = 'var(--red)';
    statusBadge.style.borderColor = 'var(--red)';
    alert('ไม่สามารถเปิดกล้องได้: ' + err.message + '\nโปรดอนุญาตให้ Safari เข้าถึงกล้องในการตั้งค่า Settings > Safari > Camera');
  } finally {
    isRestartingCamera = false;
    if (pendingRestart) {
      pendingRestart = false;
      startCamera();
    }
  }
}

function applyLensZoom(lens) {
  softwareCropZoom = 1.0;
  videoEl.style.transform = '';
  if (!currentTrack || !currentTrack.getCapabilities) {
    if (lens === 'back-tele' && activePhysicalLens !== 'back-tele') {
      softwareCropZoom = 2.0;
      videoEl.style.transform = 'scale(2)';
    }
    return;
  }
  const caps = currentTrack.getCapabilities();
  if (lens === 'back-tele') {
    if (activePhysicalLens === 'back-tele') {
      return;
    }
    if (caps.zoom && caps.zoom.max >= 2.0) {
      currentTrack.applyConstraints({
        advanced: [{ zoom: 2.0 }]
      }).catch(() => {});
    } else if (caps.zoom && caps.zoom.max >= 1.5) {
      currentTrack.applyConstraints({
        advanced: [{ zoom: caps.zoom.max }]
      }).catch(() => {});
    } else {
      softwareCropZoom = 2.0;
      videoEl.style.transform = 'scale(2)';
    }
    return;
  }
  if (caps.zoom) {
    let zoomVal = 1.0;
    if (lens === 'back-ultra' && activePhysicalLens !== 'back-ultra') {
      zoomVal = caps.zoom.min || 0.5;
    }
    currentTrack.applyConstraints({
      advanced: [{ zoom: zoomVal }]
    }).catch(() => {});
  }
}

function applyTorch(state) {
  torchOn = state;
  if (btnTorch) {
    if (torchOn) btnTorch.classList.add('active');
    else btnTorch.classList.remove('active');
  }
  if (!currentTrack || !currentTrack.getCapabilities) return;
  const caps = currentTrack.getCapabilities();
  if (caps.torch) {
    currentTrack.applyConstraints({
      advanced: [{ torch: torchOn }]
    }).catch(() => {});
  }
}

let lastHardwareCmd = null;

function applyHardwareConstraints(cmd) {
  if (!cmd) return;
  lastHardwareCmd = Object.assign(lastHardwareCmd || {}, cmd);
  if (!currentTrack || typeof currentTrack.applyConstraints !== 'function') return;
  try {
    const caps = currentTrack.getCapabilities ? currentTrack.getCapabilities() : {};
    const adv = {};

    const isoNum = (cmd.iso !== undefined) ? Number(cmd.iso) : 100;
    const shutterNum = (cmd.shutter !== undefined) ? Number(cmd.shutter) : 60;
    const manualExposure = Math.abs(isoNum - 100) > 1 || Math.abs(shutterNum - 60) > 1;

    if (manualExposure) {
      if (caps.iso && !isNaN(isoNum)) {
        let isoVal = isoNum;
        if (typeof caps.iso.min === 'number') isoVal = Math.max(caps.iso.min, isoVal);
        if (typeof caps.iso.max === 'number') isoVal = Math.min(caps.iso.max, isoVal);
        adv.iso = isoVal;
      }
      if (caps.exposureTime && !isNaN(shutterNum) && shutterNum > 0) {
        let expSec = 1.0 / shutterNum;
        if (typeof caps.exposureTime.min === 'number' && typeof caps.exposureTime.max === 'number') {
          let targetExp = (caps.exposureTime.max <= 10) ? expSec : expSec * 1000;
          targetExp = Math.max(caps.exposureTime.min, Math.min(caps.exposureTime.max, targetExp));
          adv.exposureTime = targetExp;
        } else {
          adv.exposureTime = expSec;
        }
      }
      if (caps.exposureMode && Array.isArray(caps.exposureMode) && caps.exposureMode.includes('manual')) {
        adv.exposureMode = 'manual';
      }
    } else if (caps.exposureMode && Array.isArray(caps.exposureMode) && caps.exposureMode.includes('continuous')) {
      adv.exposureMode = 'continuous';
    }

    const wbNum = (cmd.wb !== undefined) ? Number(cmd.wb) : 5400;
    if (Math.abs(wbNum - 5400) > 15) {
      if (!isNaN(wbNum) && caps.colorTemperature) {
        let wbVal = wbNum;
        if (typeof caps.colorTemperature.min === 'number') wbVal = Math.max(caps.colorTemperature.min, wbVal);
        if (typeof caps.colorTemperature.max === 'number') wbVal = Math.min(caps.colorTemperature.max, wbVal);
        adv.colorTemperature = wbVal;
      }
      if (caps.whiteBalanceMode && Array.isArray(caps.whiteBalanceMode) && caps.whiteBalanceMode.includes('manual')) {
        adv.whiteBalanceMode = 'manual';
      }
    } else if (caps.whiteBalanceMode && Array.isArray(caps.whiteBalanceMode) && caps.whiteBalanceMode.includes('continuous')) {
      adv.whiteBalanceMode = 'continuous';
    }

    const focusNum = (cmd.focus !== undefined) ? Number(cmd.focus) : 0.5;
    if (Math.abs(focusNum - 0.5) > 0.02) {
      if (!isNaN(focusNum) && caps.focusDistance) {
        if (typeof caps.focusDistance.min === 'number' && typeof caps.focusDistance.max === 'number') {
          let fDist = caps.focusDistance.min + focusNum * (caps.focusDistance.max - caps.focusDistance.min);
          adv.focusDistance = Math.max(caps.focusDistance.min, Math.min(caps.focusDistance.max, fDist));
        } else {
          adv.focusDistance = focusNum;
        }
      }
      if (caps.focusMode && Array.isArray(caps.focusMode) && caps.focusMode.includes('manual')) {
        adv.focusMode = 'manual';
      }
    } else if (caps.focusMode && Array.isArray(caps.focusMode) && caps.focusMode.includes('continuous')) {
      adv.focusMode = 'continuous';
    }

    if (Object.keys(adv).length > 0) {
      currentTrack.applyConstraints({ advanced: [adv] }).catch(() => {});
    }
  } catch (e) {}
}

function updateLensButtonsUI(lens) {
  document.querySelectorAll('.lens-btn').forEach(btn => {
    if (btn.getAttribute('data-lens') === lens) {
      btn.classList.add('active');
    } else {
      btn.classList.remove('active');
    }
  });
}

function updateResAndFpsButtonsUI() {
  const btnRes = document.getElementById('btnRes');
  if (btnRes) {
    if (targetWidth >= 3840) {
      btnRes.textContent = '📺 4K';
      btnRes.style.background = 'var(--cta-bg)';
      btnRes.style.color = 'var(--cta-fg)';
      btnRes.style.borderColor = 'var(--border-strong)';
    } else if (targetWidth >= 1920) {
      btnRes.textContent = '📺 1080p';
      btnRes.style.background = 'var(--cta-bg)';
      btnRes.style.color = 'var(--cta-fg)';
      btnRes.style.borderColor = 'var(--border-strong)';
    } else {
      btnRes.textContent = '📺 720p';
      btnRes.style.background = 'var(--input)';
      btnRes.style.color = 'var(--ink)';
      btnRes.style.borderColor = 'var(--border)';
    }
  }

  const btnQuality = document.getElementById('btnQuality');
  if (btnQuality) {
    btnQuality.textContent = '⚡ ' + targetFps + ' FPS';
    if (targetFps >= 60) {
      btnQuality.style.background = 'var(--cta-bg)';
      btnQuality.style.color = 'var(--cta-fg)';
      btnQuality.style.borderColor = 'var(--border-strong)';
    } else {
      btnQuality.style.background = 'var(--input)';
      btnQuality.style.color = 'var(--ink)';
      btnQuality.style.borderColor = 'var(--border)';
    }
  }
}

function switchLens(lens) {
  if (currentLens === lens && isStreaming) return;
  currentLens = lens;
  updateLensButtonsUI(currentLens);
  sendWsTelemetry({
    type: 'userPrefs',
    w: targetWidth,
    h: targetHeight,
    fps: targetFps,
    lens: currentLens
  });
  startCamera();
}

function handleServerCommand(cmd) {
  if (cmd.forceJpeg === true && useWebCodecs) {
    useWebCodecs = false;
    closeVideoEncoder();
    updateCodecButtonUI('สลับกลับ JPEG อัตโนมัติ');
  }

  if (typeof cmd.paused === 'boolean') {
    isPaused = cmd.paused;
    pauseBanner.style.display = isPaused ? 'flex' : 'none';
    statusBadge.textContent = isPaused ? '⏸️ PAUSED' : '🟢 STREAMING';
    statusBadge.style.color = isPaused ? '#f59e0b' : 'var(--green)';
    statusBadge.style.borderColor = isPaused ? '#f59e0b' : 'var(--green)';
  }

  if (typeof cmd.torch === 'boolean' && cmd.torch !== torchOn) {
    applyTorch(cmd.torch);
  }

  if (typeof cmd.mic === 'boolean' && cmd.mic !== micActive) {
    setMicState(cmd.mic);
  }

  if (currentTrack && typeof currentTrack.applyConstraints === 'function' &&
      (cmd.iso !== undefined || cmd.shutter !== undefined || cmd.wb !== undefined || cmd.focus !== undefined)) {
    applyHardwareConstraints(cmd);
  }

  let needCameraRestart = false;

  if (cmd.w && cmd.h && (cmd.w !== targetWidth || cmd.h !== targetHeight)) {
    targetWidth = cmd.w;
    targetHeight = cmd.h;
    needCameraRestart = true;
    updateResAndFpsButtonsUI();
  }

  if (cmd.fps && cmd.fps > 0 && cmd.fps !== targetFps) {
    targetFps = cmd.fps;
    needCameraRestart = true;
    updateResAndFpsButtonsUI();
  }

  if (cmd.lens && cmd.lens !== currentLens) {
    currentLens = cmd.lens;
    needCameraRestart = true;
  }

  if (needCameraRestart && isStreaming) {
    startCamera();
  }
}

function connectWs() {
  if (ws && (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN)) {
    return;
  }
  try {
    const proto = (location.protocol === 'https:') ? 'wss://' : 'ws://';
    ws = new WebSocket(proto + location.host + '/ws');
    ws.binaryType = 'blob';

    ws.onopen = () => {
      console.log('[Ghostcam] WS connected');
    };

    ws.onmessage = (evt) => {
      if (typeof evt.data === 'string') {
        try {
          const data = JSON.parse(evt.data);
          if (data && typeof data.v === 'number') {
            if (data.v >= serverCommandVersion) {
              serverCommandVersion = data.v;
              handleServerCommand(data);
            }
          }
        } catch (e) {}
      }
    };

    ws.onclose = () => {
      scheduleWsReconnect();
    };

    ws.onerror = () => {
      try { ws.close(); } catch (e) {}
    };
  } catch (e) {
    scheduleWsReconnect();
  }
}

function scheduleWsReconnect() {
  if (wsReconnectTimer) return;
  wsReconnectTimer = setTimeout(() => {
    wsReconnectTimer = null;
    if (isStreaming) connectWs();
  }, 1500);
}

function sendWsTelemetry(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    try {
      ws.send(JSON.stringify(obj));
    } catch (e) {}
  }
}

function startStreamingLoop() {
  connectWs();
  startStatusPollingFallback();
  scheduleNextFrame(++frameLoopGen);
}

function ensureFrameWatchdog() {
  if (frameWatchdogTimer) return;
  frameWatchdogTimer = setInterval(() => {
    if (isStreaming && !isPaused && videoEl && videoEl.readyState >= 2 && (performance.now() - lastFrameTickTime > 140)) {
      onVideoFrame(++frameLoopGen);
    }
  }, 60);
}

function scheduleNextFrame(gen) {
  if (!isStreaming || gen !== frameLoopGen) return;
  if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) {
    videoEl.requestVideoFrameCallback(() => onVideoFrame(gen));
  } else {
    requestAnimationFrame(() => onVideoFrame(gen));
  }
}

function updateCodecButtonUI(customTitle) {
  if (!btnCodec) return;
  if (useWebCodecs) {
    btnCodec.textContent = 'H264';
    btnCodec.classList.add('active');
    btnCodec.title = customTitle || 'กำลังส่งภาพแบบ H.264 WebCodecs (แตะเพื่อสลับเป็น JPEG)';
  } else {
    btnCodec.textContent = 'JPEG';
    btnCodec.classList.remove('active');
    btnCodec.title = customTitle || 'สลับโหมดส่งภาพ JPEG / H.264';
  }
}

function closeVideoEncoder() {
  encoderConfigKey = '';
  h264Description = null;
  h264FrameIndex = 0;
  h264EnableTime = 0;
  lastH264OutputTime = 0;
  if (videoEncoder) {
    try {
      if (videoEncoder.state !== 'closed') videoEncoder.close();
    } catch (e) {}
    videoEncoder = null;
  }
}

function startsWithAnnexB(u8) {
  return (u8.length >= 4 && u8[0] === 0 && u8[1] === 0 && u8[2] === 0 && u8[3] === 1)
      || (u8.length >= 3 && u8[0] === 0 && u8[1] === 0 && u8[2] === 1);
}

function startsWithAnnexBSps(u8) {
  if (u8.length >= 5 && u8[0] === 0 && u8[1] === 0 && u8[2] === 0 && u8[3] === 1) {
    return (u8[4] & 0x1f) === 7;
  }
  if (u8.length >= 4 && u8[0] === 0 && u8[1] === 0 && u8[2] === 1) {
    return (u8[3] & 0x1f) === 7;
  }
  return false;
}

function extractAnnexBPrefix(desc) {
  if (!desc) return null;
  const raw = (desc instanceof Uint8Array) ? desc : new Uint8Array(desc);
  if (raw.length === 0) return null;
  if (startsWithAnnexB(raw)) return raw;
  if (raw[0] === 1 && raw.length >= 8) {
    const parts = [];
    let total = 0;
    let offset = 5;
    const numSps = raw[offset++] & 0x1f;
    for (let i = 0; i < numSps && offset + 2 <= raw.length; i++) {
      const len = (raw[offset] << 8) | raw[offset + 1];
      offset += 2;
      if (offset + len <= raw.length) {
        parts.push(raw.subarray(offset, offset + len));
        total += 4 + len;
      }
      offset += len;
    }
    if (offset < raw.length) {
      const numPps = raw[offset++];
      for (let i = 0; i < numPps && offset + 2 <= raw.length; i++) {
        const len = (raw[offset] << 8) | raw[offset + 1];
        offset += 2;
        if (offset + len <= raw.length) {
          parts.push(raw.subarray(offset, offset + len));
          total += 4 + len;
        }
        offset += len;
      }
    }
    if (total > 0) {
      const out = new Uint8Array(total);
      let p = 0;
      for (const nalu of parts) {
        out[p++] = 0; out[p++] = 0; out[p++] = 0; out[p++] = 1;
        out.set(nalu, p);
        p += nalu.length;
      }
      return out;
    }
  }
  return null;
}

function convertAvccToAnnexB(u8) {
  if (!u8 || u8.length < 5) return null;
  const chunks = [];
  let total = 0;
  let offset = 0;
  while (offset + 4 <= u8.length) {
    const len = ((u8[offset] << 24) | (u8[offset + 1] << 16) | (u8[offset + 2] << 8) | u8[offset + 3]) >>> 0;
    offset += 4;
    if (len === 0 || offset + len > u8.length) {
      return null;
    }
    chunks.push(u8.subarray(offset, offset + len));
    total += 4 + len;
    offset += len;
  }
  if (offset !== u8.length || total === 0) return null;
  const out = new Uint8Array(total);
  let p = 0;
  for (const nalu of chunks) {
    out[p++] = 0; out[p++] = 0; out[p++] = 0; out[p++] = 1;
    out.set(nalu, p);
    p += nalu.length;
  }
  return out;
}

function ensureVideoEncoder(w, h) {
  if (typeof VideoEncoder === 'undefined') {
    useWebCodecs = false;
    updateCodecButtonUI('เบราว์เซอร์ไม่รองรับ WebCodecs — ใช้ JPEG');
    return null;
  }
  const key = `${w}x${h}@${targetFps}`;
  if (videoEncoder && videoEncoder.state === 'configured' && encoderConfigKey === key) {
    return Promise.resolve(videoEncoder);
  }
  if (encoderInitPromise) return encoderInitPromise;

  closeVideoEncoder();
  const bitrate = w >= 3840 ? 12000000 : (w >= 1920 ? 5000000 : 2500000);
  const candidates = ['avc1.42002a', 'avc1.42001f'];

  encoderInitPromise = (async () => {
    try {
      let chosenConfig = null;
      for (const codec of candidates) {
        const baseCfg = {
          codec: codec,
          width: w,
          height: h,
          framerate: targetFps,
          bitrate: bitrate,
          latencyMode: 'realtime'
        };
        const cfg = Object.assign({}, baseCfg, { avc: { format: 'annexb' } });
        try {
          const support = await VideoEncoder.isConfigSupported(cfg);
          if (support && support.supported) {
            chosenConfig = Object.assign({}, support.config || cfg, { avc: { format: 'annexb' } });
            break;
          }
        } catch (e) {}
        try {
          const support2 = await VideoEncoder.isConfigSupported(baseCfg);
          if (support2 && support2.supported) {
            chosenConfig = support2.config || baseCfg;
            break;
          }
        } catch (e) {}
      }

      if (!chosenConfig || !useWebCodecs) {
        useWebCodecs = false;
        updateCodecButtonUI('H.264 ไม่รองรับบนอุปกรณ์นี้ — สลับกลับ JPEG');
        return null;
      }

      const enc = new VideoEncoder({
        output: (chunk, meta) => {
          lastH264OutputTime = performance.now();
          const rawChunk = new Uint8Array(chunk.byteLength);
          chunk.copyTo(rawChunk);
          if (meta && meta.decoderConfig && meta.decoderConfig.description) {
            const parsedDesc = extractAnnexBPrefix(meta.decoderConfig.description);
            if (parsedDesc) h264Description = parsedDesc;
          }
          let chunkBytes = rawChunk;
          if (!startsWithAnnexB(chunkBytes)) {
            const converted = convertAvccToAnnexB(chunkBytes);
            if (converted) chunkBytes = converted;
          }
          if (!startsWithAnnexB(chunkBytes)) {
            return;
          }
          let outBytes = chunkBytes;
          if (chunk.type === 'key' && h264Description && !startsWithAnnexBSps(chunkBytes)) {
            outBytes = new Uint8Array(h264Description.length + chunkBytes.length);
            outBytes.set(h264Description, 0);
            outBytes.set(chunkBytes, h264Description.length);
          }
          if (!startsWithAnnexB(outBytes)) {
            return;
          }
          const maxBuffer = targetWidth >= 3840 ? 3500000 : 1500000;
          if (ws && ws.readyState === WebSocket.OPEN && ws.bufferedAmount < maxBuffer) {
            ws.send(outBytes);
            recordFrameStats(w, h, 'H264');
          }
        },
        error: () => {
          useWebCodecs = false;
          closeVideoEncoder();
          updateCodecButtonUI('H.264 Encoder ขัดข้อง — สลับกลับ JPEG');
        }
      });
      h264EnableTime = performance.now();
      lastH264OutputTime = 0;
      enc.configure(chosenConfig);
      videoEncoder = enc;
      encoderConfigKey = key;
      h264FrameIndex = 0;
      h264Description = null;
      return enc;
    } catch (e) {
      useWebCodecs = false;
      closeVideoEncoder();
      updateCodecButtonUI('H.264 ไม่รองรับ — ใช้ JPEG');
      return null;
    } finally {
      encoderInitPromise = null;
    }
  })();

  return encoderInitPromise;
}

function onVideoFrame(gen) {
  if (!isStreaming || gen !== frameLoopGen) return;
  lastFrameTickTime = performance.now();
  scheduleNextFrame(gen);
  const maxEncodes = (targetFps >= 60 && !coolMode) ? 2 : 1;
  if (document.visibilityState !== 'visible' || isPaused || (!useWebCodecs && encodesInFlight >= maxEncodes)) return;
  if (videoEl.readyState < 2) return;

  const minGap = (1000 / targetFps) * 0.85;
  const now = performance.now();
  if (useWebCodecs && videoEncoder && videoEncoder.state === 'configured' && !lastH264OutputTime && h264EnableTime > 0 && (now - h264EnableTime > 1500)) {
    useWebCodecs = false;
    closeVideoEncoder();
    updateCodecButtonUI('H.264 ไม่ส่งเฟรม — สลับกลับ JPEG');
  }
  if (now - lastFrameSendTime < minGap) return;

  const w = videoEl.videoWidth || targetWidth;
  const h = videoEl.videoHeight || targetHeight;
  const vw = videoEl.videoWidth || w;
  const vh = videoEl.videoHeight || h;

  if (useWebCodecs && ws && ws.readyState === WebSocket.OPEN) {
    const encW = w & ~1;
    const encH = h & ~1;
    if (encW < 2 || encH < 2) return;
    const key = `${encW}x${encH}@${targetFps}`;
    if (!videoEncoder || videoEncoder.state !== 'configured' || encoderConfigKey !== key) {
      ensureVideoEncoder(encW, encH);
      return;
    }
    if (videoEncoder.encodeQueueSize >= 4) return;

    let frameSource = videoEl;
    if (softwareCropZoom > 1.01) {
      const slot = !canvasBusy[0] ? 0 : (!canvasBusy[1] ? 1 : -1);
      if (slot < 0) return;
      const c = encodeCanvases[slot];
      const cCtx = encodeCtxs[slot];
      if (c.width !== encW || c.height !== encH) {
        c.width = encW;
        c.height = encH;
        if (cCtx) cCtx.imageSmoothingEnabled = false;
      }
      const sw = vw / softwareCropZoom;
      const sh = vh / softwareCropZoom;
      const sx = (vw - sw) * 0.5;
      const sy = (vh - sh) * 0.5;
      cCtx.drawImage(videoEl, sx, sy, sw, sh, 0, 0, encW, encH);
      frameSource = c;
    }

    lastFrameSendTime = now;
    let frame = null;
    try {
      frame = new VideoFrame(frameSource, { timestamp: Math.round(now * 1000) });
      const isKey = (h264FrameIndex % 60 === 0);
      h264FrameIndex++;
      videoEncoder.encode(frame, { keyFrame: isKey });
    } catch (e) {
      useWebCodecs = false;
      closeVideoEncoder();
      updateCodecButtonUI('H.264 Encode ล้มเหลว — ใช้ JPEG');
    } finally {
      if (frame) frame.close();
    }
    return;
  }

  const slot = !canvasBusy[0] ? 0 : (!canvasBusy[1] ? 1 : -1);
  if (slot < 0) return;

  const c = encodeCanvases[slot];
  const cCtx = encodeCtxs[slot];
  if (c.width !== w || c.height !== h) {
    c.width = w;
    c.height = h;
    if (cCtx) cCtx.imageSmoothingEnabled = false;
  }
  if (softwareCropZoom > 1.01) {
    const sw = vw / softwareCropZoom;
    const sh = vh / softwareCropZoom;
    const sx = (vw - sw) * 0.5;
    const sy = (vh - sh) * 0.5;
    cCtx.drawImage(videoEl, sx, sy, sw, sh, 0, 0, w, h);
  } else {
    cCtx.drawImage(videoEl, 0, 0, w, h);
  }

  canvasBusy[slot] = true;
  encodesInFlight++;
  lastFrameSendTime = now;
  const mySeq = ++nextEncodeSeq;

  const q = coolMode
    ? (targetWidth >= 3840 ? 0.58 : (targetWidth >= 1920 ? 0.64 : 0.72))
    : (targetWidth >= 3840 ? 0.72 : (targetWidth >= 1920 ? 0.80 : 0.85));
  c.toBlob(blob => {
    try {
      if (blob && mySeq > lastDispatchedSeq) {
        lastDispatchedSeq = mySeq;
        dispatchFrameBlob(blob, w, h);
      }
    } finally {
      canvasBusy[slot] = false;
      encodesInFlight = Math.max(0, encodesInFlight - 1);
    }
  }, 'image/jpeg', q);
}

function dispatchFrameBlob(blob, w, h) {
  const maxBuffer = targetWidth >= 3840 ? 3500000 : 1500000;
  if (ws && ws.readyState === WebSocket.OPEN) {
    if (ws.bufferedAmount < maxBuffer) {
      ws.send(blob);
      recordFrameStats(w, h, 'WS');
    }
    return;
  }

  // HTTP Fallback
  if (httpFallbackInFlight >= 1) return;
  httpFallbackInFlight++;

  fetch('/frame', {
    method: 'POST',
    body: blob,
    cache: 'no-store'
  }).then(async res => {
    if (res.ok) {
      recordFrameStats(w, h, 'HTTP');
      try {
        const data = await res.json();
        if (data && typeof data.v === 'number' && data.v >= serverCommandVersion) {
          serverCommandVersion = data.v;
          handleServerCommand(data);
        }
      } catch (e) {}
    }
  }).catch(() => {})
  .finally(() => {
    httpFallbackInFlight = Math.max(0, httpFallbackInFlight - 1);
  });
}

function recordFrameStats(w, h, transport) {
  frameCount++;
  const now = performance.now();
  if (now - lastFpsTime >= 1000) {
    activeFps = (frameCount * 1000 / (now - lastFpsTime)).toFixed(1);
    statsEl.textContent = `${w}x${h} • ${activeFps} FPS • ${transport}${coolMode ? ' • ❄️' : ''}`;
    frameCount = 0;
    lastFpsTime = now;

    if (transport === 'WS' || transport === 'H264') {
      sendWsTelemetry({
        type: 'stats',
        w: w,
        h: h,
        fps: parseFloat(activeFps)
      });
    }
  }
}

function startStatusPollingFallback() {
  if (statusPollTimer) clearInterval(statusPollTimer);
  statusPollTimer = setInterval(async () => {
    if (!isStreaming) return;
    if (ws && ws.readyState === WebSocket.OPEN) return;

    try {
      const res = await fetch('/status', { cache: 'no-store' });
      if (res.ok) {
        const data = await res.json();
        if (data && typeof data.v === 'number' && data.v >= serverCommandVersion) {
          serverCommandVersion = data.v;
          handleServerCommand(data);
        }
      }
    } catch (e) {}
  }, 1000);
}

document.getElementById('btnStart').addEventListener('click', () => {
  startCamera();
});

document.getElementById('btnSwitch').addEventListener('click', () => {
  const nextLens = (currentLens === 'front') ? 'back-wide' : 'front';
  switchLens(nextLens);
});

document.querySelectorAll('.lens-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    const l = btn.getAttribute('data-lens');
    if (l) switchLens(l);
  });
});

document.getElementById('btnRes').addEventListener('click', () => {
  if (targetWidth === 1280) {
    targetWidth = 1920;
    targetHeight = 1080;
  } else if (targetWidth === 1920) {
    targetWidth = 3840;
    targetHeight = 2160;
  } else {
    targetWidth = 1280;
    targetHeight = 720;
  }
  updateResAndFpsButtonsUI();
  sendWsTelemetry({
    type: 'userPrefs',
    w: targetWidth,
    h: targetHeight,
    fps: targetFps,
    lens: currentLens
  });
  if (isStreaming) startCamera();
});

document.getElementById('btnQuality').addEventListener('click', () => {
  targetFps = (targetFps === 60) ? 30 : 60;
  updateResAndFpsButtonsUI();
  sendWsTelemetry({
    type: 'userPrefs',
    w: targetWidth,
    h: targetHeight,
    fps: targetFps,
    lens: currentLens
  });
  if (isStreaming) startCamera();
});

document.getElementById('btnTorch').addEventListener('click', () => {
  applyTorch(!torchOn);
  sendWsTelemetry({
    type: 'userPrefs',
    torch: torchOn
  });
});

document.getElementById('btnOled').addEventListener('click', () => {
  oledOverlay.style.display = 'flex';
  videoEl.classList.add('oled-sleep');
});

oledOverlay.addEventListener('click', () => {
  oledOverlay.style.display = 'none';
  videoEl.classList.remove('oled-sleep');
});

const btnTheme = document.getElementById('btnTheme');
if (btnTheme) {
  btnTheme.addEventListener('click', () => {
    document.body.classList.toggle('yoyu-dark');
  });
}

if (btnCool) {
  btnCool.addEventListener('click', () => {
    coolMode = !coolMode;
    if (coolMode) {
      document.body.classList.add('cool-mode');
      btnCool.classList.remove('off');
      btnCool.textContent = '❄️ ลดร้อน: ON';
    } else {
      document.body.classList.remove('cool-mode');
      btnCool.classList.add('off');
      btnCool.textContent = '⚡ เต็มสปีด';
    }
  });
}

function updateMicButtonUI() {
  if (!btnMic) return;
  if (micActive) {
    btnMic.classList.add('active');
    btnMic.textContent = '🎙️ ไมค์ ON';
  } else {
    btnMic.classList.remove('active');
    btnMic.textContent = '🎙️ ไมค์';
  }
}

async function setMicState(enable) {
  if (!enable) {
    micActive = false;
    updateMicButtonUI();
    try { if (micProcessorNode) { micProcessorNode.onaudioprocess = null; micProcessorNode.disconnect(); } } catch (e) {}
    try { if (micSourceNode) micSourceNode.disconnect(); } catch (e) {}
    try { if (micGainNode) micGainNode.disconnect(); } catch (e) {}
    micProcessorNode = null;
    micSourceNode = null;
    micGainNode = null;
    if (micStream) {
      try { micStream.getTracks().forEach(t => t.stop()); } catch (e) {}
      micStream = null;
    }
    if (audioCtx) {
      try { await audioCtx.close(); } catch (e) {}
      audioCtx = null;
    }
    return;
  }

  if (micActive && micStream) return;
  micActive = true;
  updateMicButtonUI();

  try {
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: true,
          noiseSuppression: true,
          channelCount: 1,
          sampleRate: 24000
        }
      });
    } catch (e) {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    }

    if (!micActive) {
      stream.getTracks().forEach(t => t.stop());
      return;
    }

    micStream = stream;
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    try {
      audioCtx = new AudioCtx({ sampleRate: 24000 });
    } catch (e) {
      audioCtx = new AudioCtx();
    }
    if (audioCtx.state === 'suspended') {
      await audioCtx.resume();
    }

    micSourceNode = audioCtx.createMediaStreamSource(micStream);
    micProcessorNode = audioCtx.createScriptProcessor(2048, 1, 1);
    micGainNode = audioCtx.createGain();
    micGainNode.gain.value = 0;

    micProcessorNode.onaudioprocess = (evt) => {
      if (!micActive || !ws || ws.readyState !== WebSocket.OPEN) return;
      const input = evt.inputBuffer.getChannelData(0);
      const srcRate = (audioCtx && audioCtx.sampleRate) ? audioCtx.sampleRate : 24000;
      let samples = input;
      if (srcRate !== 24000 && srcRate > 0) {
        const ratio = srcRate / 24000;
        const outLen = Math.max(1, Math.floor(input.length / ratio));
        const resampled = new Float32Array(outLen);
        for (let i = 0; i < outLen; i++) {
          const pos = i * ratio;
          const idx = Math.floor(pos);
          const frac = pos - idx;
          const s0 = input[idx] || 0;
          const s1 = input[Math.min(idx + 1, input.length - 1)] || s0;
          resampled[i] = s0 + (s1 - s0) * frac;
        }
        samples = resampled;
      }

      const pkt = new Uint8Array(4 + samples.length * 2);
      pkt[0] = 0x41; // 'A'
      pkt[1] = 0x55; // 'U'
      pkt[2] = 0x44; // 'D'
      pkt[3] = 0x30; // '0'
      const view = new DataView(pkt.buffer);
      for (let i = 0; i < samples.length; i++) {
        let s = samples[i];
        if (s > 1) s = 1;
        else if (s < -1) s = -1;
        view.setInt16(4 + i * 2, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
      }

      if (ws.bufferedAmount < 524288) {
        ws.send(pkt);
      }
    };

    micSourceNode.connect(micProcessorNode);
    micProcessorNode.connect(micGainNode);
    micGainNode.connect(audioCtx.destination);
  } catch (err) {
    await setMicState(false);
    alert('ไม่สามารถเปิดไมโครโฟนได้: ' + (err && err.message ? err.message : err));
  }
}

if (btnCodec) {
  btnCodec.addEventListener('click', () => {
    if (typeof VideoEncoder === 'undefined') {
      btnCodec.style.display = 'none';
      return;
    }
    useWebCodecs = !useWebCodecs;
    if (!useWebCodecs) {
      closeVideoEncoder();
    } else if (isStreaming) {
      const w = (videoEl.videoWidth || targetWidth) & ~1;
      const h = (videoEl.videoHeight || targetHeight) & ~1;
      if (w >= 2 && h >= 2) ensureVideoEncoder(w, h);
    }
    updateCodecButtonUI();
  });
}

if (btnMic) {
  btnMic.addEventListener('click', async () => {
    await setMicState(!micActive);
    sendWsTelemetry({
      type: 'userPrefs',
      mic: micActive
    });
  });
}

document.getElementById('btnFs').addEventListener('click', () => {
  if (!document.fullscreenElement) {
    document.documentElement.requestFullscreen().catch(()=>{});
  } else {
    document.exitFullscreen().catch(()=>{});
  }
});

updateResAndFpsButtonsUI();
updateLensButtonsUI(currentLens);
updateCodecButtonUI();
updateMicButtonUI();

navigator.permissions?.query({ name: 'camera' }).then(res => {
  if (res.state === 'granted') {
    startCamera();
  }
}).catch(()=>{});
</script>
</body>
</html>";

    private sealed class PrefixStream : Stream
    {
        private readonly MemoryStream _prefix;
        private readonly Stream _tail;

        public PrefixStream(byte[] prefixBytes, Stream tail)
        {
            _prefix = new MemoryStream(prefixBytes);
            _tail = tail;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => _tail.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _tail.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _tail.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int r = _prefix.Read(buffer, offset, count);
            if (r > 0) return r;
            return _tail.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int r = await _prefix.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (r > 0) return r;
            return await _tail.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count) => _tail.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _tail.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
