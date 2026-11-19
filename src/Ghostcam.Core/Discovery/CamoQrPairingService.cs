using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Makaretu.Dns;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using QRCoder;
using Ghostcam.Core.Protocol;

namespace Ghostcam.Core.Discovery;

/// <summary>
/// Manages Camo Wi-Fi pairing via ISO/IEC QR codes, active mDNS service discovery (_camopairing._tcp.local
/// and _camo._tcp.local), and automated TLS-PSK pairing handshakes.
/// </summary>
public sealed class CamoQrPairingService : IAsyncDisposable
{
    private readonly string _storageDir;
    private readonly string _desktopIdFile;
    private readonly string _malheurIdFile;
    private readonly string _pairedDevicesFile;

    private readonly ConcurrentDictionary<string, PairedDeviceRecord> _pairedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _activePairingAttempts = new(StringComparer.OrdinalIgnoreCase);

    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private Timer? _mdnsQueryTimer;
    private Timer? _directConnectTimer;
    private Timer? _malheurPollTimer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2.5) };
    private bool _isDiscovering;

    public string DesktopId { get; }
    public string MalheurId { get; }
    public QrPairingPayload CurrentPayload { get; }
    public string CurrentJson { get; }
    public byte[] AesKeyBytes { get; }
    public string PairingId => CurrentPayload.PairingId;
    public string Base64Key => CurrentPayload.Key;

    public IReadOnlyCollection<PairedDeviceRecord> PairedDevices => (IReadOnlyCollection<PairedDeviceRecord>)_pairedDevices.Values;
    public IReadOnlyList<PairedDeviceRecord> GetPairedDevices() => _pairedDevices.Values.ToList();

    public event Action<PairedDeviceRecord, IPEndPoint>? DevicePaired;
    public event Action<PairedDeviceRecord, IPEndPoint>? StreamingEndpointDiscovered;
    public event Action<string>? LogMessage;

    public CamoQrPairingService(string? persistentPairingId = null, byte[]? existingAesKey = null)
    {
        _storageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghostcam");
        Directory.CreateDirectory(_storageDir);

        _desktopIdFile = Path.Combine(_storageDir, "desktop_id.txt");
        _malheurIdFile = Path.Combine(_storageDir, "malheur_id.txt");
        _pairedDevicesFile = Path.Combine(_storageDir, "paired_devices.json");

        DesktopId = InitializeDesktopId();
        MalheurId = InitializeMalheurId();
        LoadPairedDevices();

        string pairingId = persistentPairingId ?? Guid.NewGuid().ToString().ToLowerInvariant();

        // 16 cryptographically random bytes for TLS-PSK "PairingKey"
        byte[] keyBytes = existingAesKey != null && existingAesKey.Length == 16
            ? existingAesKey
            : RandomNumberGenerator.GetBytes(16);

        AesKeyBytes = keyBytes;
        string base64Key = Convert.ToBase64String(keyBytes);

        CurrentPayload = new QrPairingPayload
        {
            CompatibilityVersion = 1,
            PairingId = pairingId,
            Key = base64Key
        };

        CurrentJson = CamoJson.Serialize(CurrentPayload);
    }

    private string InitializeDesktopId()
    {
        if (File.Exists(_desktopIdFile))
        {
            string stored = File.ReadAllText(_desktopIdFile).Trim();
            if (Guid.TryParse(stored, out _)) return stored;
        }

        try
        {
            // Attempt to read from Camo Studio registry entry if available
            string? regId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Reincubate\Camo Studio", "Identifier", null) as string;
            if (!string.IsNullOrWhiteSpace(regId) && Guid.TryParse(regId, out _))
            {
                File.WriteAllText(_desktopIdFile, regId);
                return regId;
            }
        }
        catch { }

        string newId = Guid.NewGuid().ToString().ToLowerInvariant();
        File.WriteAllText(_desktopIdFile, newId);
        return newId;
    }

    private string InitializeMalheurId()
    {
        if (File.Exists(_malheurIdFile))
        {
            string stored = File.ReadAllText(_malheurIdFile).Trim();
            if (Guid.TryParse(stored, out _)) return stored;
        }

        try
        {
            string? regId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Reincubate\Camo Studio\Malheur", "StudioMalheurId", null) as string;
            if (!string.IsNullOrWhiteSpace(regId) && Guid.TryParse(regId, out _))
            {
                File.WriteAllText(_malheurIdFile, regId);
                return regId;
            }
        }
        catch { }

        string newId = Guid.NewGuid().ToString().ToLowerInvariant();
        File.WriteAllText(_malheurIdFile, newId);
        return newId;
    }

    private void LoadPairedDevices()
    {
        if (!File.Exists(_pairedDevicesFile)) return;

        try
        {
            string json = File.ReadAllText(_pairedDevicesFile);
            var list = CamoJson.Deserialize<List<PairedDeviceRecord>>(json);
            if (list != null)
            {
                foreach (var device in list)
                {
                    if (!string.IsNullOrEmpty(device.Id))
                    {
                        _pairedDevices[device.Id] = device;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to load paired devices: {ex.Message}");
        }
    }

    public void SavePairedDevice(PairedDeviceRecord device)
    {
        _pairedDevices[device.Id] = device;
        try
        {
            string json = CamoJson.Serialize(_pairedDevices.Values.ToList());
            File.WriteAllText(_pairedDevicesFile, json);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to persist paired devices: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a 2D boolean matrix representing a scannable QR code of the current pairing payload
    /// using QRCoder with Error Correction Level Q (matching Camo Studio).
    /// </summary>
    public bool[,] GenerateQrMatrix(int quietZone = 4) =>
        GenerateQrMatrix(CurrentJson, quietZone);

    public static bool[,] GenerateQrMatrix(string text, int quietZone = 4)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);

        var moduleMatrix = qrCodeData.ModuleMatrix;
        int rawHeight = moduleMatrix.Count;
        int rawWidth = rawHeight > 0 ? moduleMatrix[0].Length : 0;

        // QRCoder built-in quiet zone is 4 modules
        const int qrCoderQuietZone = 4;
        int delta = quietZone - qrCoderQuietZone;

        if (delta == 0)
        {
            bool[,] matrix = new bool[rawWidth, rawHeight];
            for (int y = 0; y < rawHeight; y++)
            {
                var row = moduleMatrix[y];
                for (int x = 0; x < rawWidth; x++)
                {
                    matrix[x, y] = row[x];
                }
            }
            return matrix;
        }

        int coreWidth = rawWidth - (qrCoderQuietZone * 2);
        int coreHeight = rawHeight - (qrCoderQuietZone * 2);

        int targetWidth = coreWidth + (quietZone * 2);
        int targetHeight = coreHeight + (quietZone * 2);
        bool[,] targetMatrix = new bool[targetWidth, targetHeight];

        for (int y = 0; y < coreHeight; y++)
        {
            var row = moduleMatrix[y + qrCoderQuietZone];
            for (int x = 0; x < coreWidth; x++)
            {
                targetMatrix[x + quietZone, y + quietZone] = row[x + qrCoderQuietZone];
            }
        }

        return targetMatrix;
    }

    private readonly ConcurrentDictionary<string, IPAddress> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts active mDNS discovery for _camopairing._tcp.local and _camo._tcp.local.
    /// </summary>
    public void StartDiscovery()
    {
        if (_isDiscovering) return;
        _isDiscovering = true;

        try
        {
            _mdns = new MulticastService(nics => nics.Where(nic =>
                nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                !nic.Description.Contains("Hamachi", StringComparison.OrdinalIgnoreCase) &&
                !nic.Description.Contains("Npcap", StringComparison.OrdinalIgnoreCase) &&
                !nic.Name.Contains("Hamachi", StringComparison.OrdinalIgnoreCase)));
            _serviceDiscovery = new ServiceDiscovery(_mdns);

            _serviceDiscovery.ServiceDiscovered += (s, serviceName) =>
            {
                if (serviceName.ToString().Contains("camo", StringComparison.OrdinalIgnoreCase))
                {
                    _mdns?.SendQuery(serviceName, DnsClass.IN, DnsType.PTR);
                }
            };

            _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            _mdns.AnswerReceived += OnMdnsAnswerReceived;

            _mdns.Start();

            // Periodic 1.0 second active query timer
            _mdnsQueryTimer = new Timer(_ =>
            {
                try
                {
                    if (_isDiscovering && _mdns != null && _serviceDiscovery != null)
                    {
                        _serviceDiscovery.QueryAllServices();
                        _mdns.SendQuery(new DomainName("_camopairing._tcp.local"), type: DnsType.PTR);
                        _mdns.SendQuery(new DomainName("_camo._tcp.local"), type: DnsType.PTR);

                        // Query each paired device specifically so dynamic SRV/A records are discovered
                        foreach (var device in _pairedDevices.Values)
                        {
                            if (!string.IsNullOrEmpty(device.Id))
                            {
                                _mdns.SendQuery(new DomainName($"_{device.Id}._camo._tcp.local"), DnsClass.IN, DnsType.ANY);
                            }
                        }
                    }
                }
                catch { }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1.0));

            // Periodic 3-second direct Wi-Fi auto-connect timer for paired devices with known IP and port
            _directConnectTimer = new Timer(_ =>
            {
                try
                {
                    if (_isDiscovering)
                    {
                        foreach (var device in _pairedDevices.Values)
                        {
                            if (!string.IsNullOrEmpty(device.LastKnownIp) && IPAddress.TryParse(device.LastKnownIp, out var ip))
                            {
                                if (device.LastKnownPort is > 0 and not 9430)
                                {
                                    StreamingEndpointDiscovered?.Invoke(device, new IPEndPoint(ip, device.LastKnownPort.Value));
                                }
                            }
                        }
                    }
                }
                catch { }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3.0));

            // Periodic 2.0-second Malheur cloud polling timer
            _malheurPollTimer = new Timer(async _ =>
            {
                try
                {
                    if (_isDiscovering)
                    {
                        await PollMalheurAsync().ConfigureAwait(false);
                    }
                }
                catch { }
            }, null, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(2.0));

            LogMessage?.Invoke("[mDNS] Started Wi-Fi pairing and streaming service discovery.");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[mDNS] Error starting multicast service: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops active mDNS service discovery.
    /// </summary>
    public void StopDiscovery()
    {
        _isDiscovering = false;
        _mdnsQueryTimer?.Dispose();
        _mdnsQueryTimer = null;
        _directConnectTimer?.Dispose();
        _directConnectTimer = null;
        _malheurPollTimer?.Dispose();
        _malheurPollTimer = null;

        if (_serviceDiscovery != null)
        {
            _serviceDiscovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _serviceDiscovery.Dispose();
            _serviceDiscovery = null;
        }

        if (_mdns != null)
        {
            _mdns.AnswerReceived -= OnMdnsAnswerReceived;
            _mdns.Stop();
            _mdns.Dispose();
            _mdns = null;
        }
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        ProcessDnsMessage(e.Message, null);
    }

    private void OnMdnsAnswerReceived(object? sender, MessageEventArgs e)
    {
        ProcessDnsMessage(e.Message, e.RemoteEndPoint);
    }

    private void ProcessDnsMessage(Message message, EndPoint? remoteEndPoint)
    {
        var allRecords = message.Answers.Concat(message.AdditionalRecords).ToList();

        // Cache host addresses (preferring IPv4)
        foreach (var a in allRecords.OfType<ARecord>())
        {
            if (a.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                _hostAddresses[a.Name.ToString().TrimEnd('.')] = a.Address;
            }
        }
        foreach (var aaaa in allRecords.OfType<AAAARecord>())
        {
            string key = aaaa.Name.ToString().TrimEnd('.');
            if (!_hostAddresses.ContainsKey(key))
            {
                _hostAddresses[key] = aaaa.Address;
            }
        }

        // 1. Check for pairing service: _camopairing._tcp.local
        var pairingSrvs = allRecords.OfType<SRVRecord>()
            .Where(s => s.Name.ToString().Contains("camopairing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var srv in pairingSrvs)
        {
            int port = srv.Port > 0 ? srv.Port : 9430;
            IPAddress? ip = FindIpForTarget(srv.Target, allRecords, remoteEndPoint);
            if (ip != null)
            {
                var ep = new IPEndPoint(ip, port);
                _ = TryExecutePairingAsync(ep);
            }
        }

        // Check PTR records for pairing
        var pairingPtrs = allRecords.OfType<PTRRecord>()
            .Where(p => p.Name.ToString().Contains("camopairing", StringComparison.OrdinalIgnoreCase) ||
                        p.DomainName.ToString().Contains("camopairing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var ptr in pairingPtrs)
        {
            if (!allRecords.OfType<SRVRecord>().Any(s => s.Name == ptr.DomainName))
            {
                _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.ANY);
            }
        }



        // 2. Check for streaming service: _camo._tcp.local
        var camoSrvs = allRecords.OfType<SRVRecord>()
            .Where(s => s.Name.ToString().Contains("_camo._tcp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var srv in camoSrvs)
        {
            int port = srv.Port > 0 ? srv.Port : 9430;
            IPAddress? ip = FindIpForTarget(srv.Target, allRecords, remoteEndPoint);
            if (ip == null)
            {
                _mdns?.SendQuery(srv.Target, DnsClass.IN, DnsType.A);
            }
            else
            {
                var ep = new IPEndPoint(ip, port);
                NotifyStreamingEndpoint(srv.Name.ToString(), ep);
            }
        }

        // Check PTR records for streaming service; query SRV if not present
        var camoPtrs = allRecords.OfType<PTRRecord>()
            .Where(p => p.Name.ToString().Contains("_camo._tcp", StringComparison.OrdinalIgnoreCase) ||
                        p.DomainName.ToString().Contains("_camo._tcp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var ptr in camoPtrs)
        {
            if (!allRecords.OfType<SRVRecord>().Any(s => s.Name == ptr.DomainName))
            {
                _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.ANY);
            }
        }
    }

    private IPAddress? FindIpForTarget(DomainName target, IEnumerable<ResourceRecord> records, EndPoint? remoteEndPoint)
    {
        // 1. Check ARecord in current message
        var a = records.OfType<ARecord>().FirstOrDefault(r => r.Name == target);
        if (a != null && a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address;

        // 2. Check cached host addresses (preferring IPv4)
        string hostKey = target.ToString().TrimEnd('.');
        if (_hostAddresses.TryGetValue(hostKey, out var cachedIp) && cachedIp.AddressFamily == AddressFamily.InterNetwork)
        {
            return cachedIp;
        }

        // 3. Fallback to sender's IPv4 address
        if (remoteEndPoint is IPEndPoint rep && rep.AddressFamily == AddressFamily.InterNetwork)
        {
            return rep.Address;
        }

        // 4. Fallback: If only 1 paired device exists with an IPv4 address, use it
        if (_pairedDevices.Count == 1)
        {
            var singleDevice = _pairedDevices.Values.First();
            if (!string.IsNullOrEmpty(singleDevice.LastKnownIp) && IPAddress.TryParse(singleDevice.LastKnownIp, out var devIp) && devIp.AddressFamily == AddressFamily.InterNetwork)
            {
                return devIp;
            }
        }

        // 5. Any cached IP
        if (cachedIp != null) return cachedIp;

        // 6. AAAARecord
        var aaaa = records.OfType<AAAARecord>().FirstOrDefault(r => r.Name == target);
        if (aaaa != null) return aaaa.Address;

        return null;
    }

    private async Task TryExecutePairingAsync(IPEndPoint phoneEndpoint)
    {
        string epKey = phoneEndpoint.ToString();
        if (!_activePairingAttempts.TryAdd(epKey, true)) return;

        try
        {
            LogMessage?.Invoke($"[Pairing] Initiating TLS-PSK handshake with {phoneEndpoint}...");
            var pairedDevice = await ExecuteTlsPskPairingAsync(phoneEndpoint).ConfigureAwait(false);
            LogMessage?.Invoke($"[Pairing] Successfully paired device '{pairedDevice.Name}' ({pairedDevice.Id})!");

            pairedDevice.LastKnownIp = phoneEndpoint.Address.ToString();
            pairedDevice.LastConnected = DateTime.UtcNow;
            SavePairedDevice(pairedDevice);

            DevicePaired?.Invoke(pairedDevice, phoneEndpoint);

            // Query dynamic streaming SRV for this paired device immediately
            _mdns?.SendQuery(new DomainName($"_{pairedDevice.Id}._camo._tcp.local"), DnsClass.IN, DnsType.ANY);
            _mdns?.SendQuery(new DomainName("_camo._tcp.local"), type: DnsType.PTR);

            // Probe adjacent streaming ports on the phone right after pairing (Camo phone streams on pairingPort - 2 or similar)
            _ = Task.Run(async () =>
            {
                await Task.Delay(250).ConfigureAwait(false);
                int pPort = phoneEndpoint.Port;
                int[] candidates = { pPort - 2, pPort - 1, pPort + 1, pPort - 3, 9430, pPort };
                bool found = false;

                foreach (int cand in candidates)
                {
                    if (cand <= 0 || cand > 65535) continue;
                    try
                    {
                        using var tcp = new TcpClient();
                        using var cts = new CancellationTokenSource(500);
                        await tcp.ConnectAsync(phoneEndpoint.Address, cand, cts.Token).ConfigureAwait(false);
                        if (tcp.Connected)
                        {
                            LogMessage?.Invoke($"[Pairing] Probed open streaming port {cand} on phone {phoneEndpoint.Address}!");
                            pairedDevice.LastKnownPort = cand;
                            SavePairedDevice(pairedDevice);
                            StreamingEndpointDiscovered?.Invoke(pairedDevice, new IPEndPoint(phoneEndpoint.Address, cand));
                            found = true;
                            break;
                        }
                    }
                    catch { }
                }

                if (!found && pairedDevice.LastKnownPort is null or 0)
                {
                    int fallbackPort = pPort > 2 ? pPort - 2 : 9430;
                    pairedDevice.LastKnownPort = fallbackPort;
                    SavePairedDevice(pairedDevice);
                    StreamingEndpointDiscovered?.Invoke(pairedDevice, new IPEndPoint(phoneEndpoint.Address, fallbackPort));
                }
            });

            // Rapid Malheur polling immediately after pairing completes to pick up dynamic streaming port
            _ = Task.Run(async () =>
            {
                int[] delays = { 100, 500, 900, 1500 };
                foreach (int delay in delays)
                {
                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        await PollMalheurNowAsync().ConfigureAwait(false);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[Pairing] Pairing handshake failed with {phoneEndpoint}: {ex.Message}");
        }
        finally
        {
            _activePairingAttempts.TryRemove(epKey, out _);
        }
    }

    private void NotifyStreamingEndpoint(string serviceName, IPEndPoint endpoint)
    {
        // Try to match device ID from service name (e.g. "_<deviceId>._camo._tcp.local")
        foreach (var device in _pairedDevices.Values)
        {
            if (serviceName.Contains(device.Id, StringComparison.OrdinalIgnoreCase))
            {
                device.LastKnownIp = endpoint.Address.ToString();
                device.LastKnownPort = endpoint.Port;
                device.LastConnected = DateTime.UtcNow;
                SavePairedDevice(device);
                StreamingEndpointDiscovered?.Invoke(device, endpoint);
                return;
            }
        }

        // Fallback: If only 1 paired device exists, associate it
        if (_pairedDevices.Count == 1)
        {
            var singleDevice = _pairedDevices.Values.First();
            singleDevice.LastKnownIp = endpoint.Address.ToString();
            singleDevice.LastKnownPort = endpoint.Port;
            singleDevice.LastConnected = DateTime.UtcNow;
            SavePairedDevice(singleDevice);
            StreamingEndpointDiscovered?.Invoke(singleDevice, endpoint);
        }
    }

    /// <summary>
    /// Executes the TLS-PSK pairing transaction over TCP with the iPhone.
    /// </summary>
    public async Task<PairedDeviceRecord> ExecuteTlsPskPairingAsync(IPEndPoint phoneEndpoint, CancellationToken cancellationToken = default)
    {
        using TcpClient tcpClient = new();
        tcpClient.NoDelay = true;
        await tcpClient.ConnectAsync(phoneEndpoint.Address, phoneEndpoint.Port, cancellationToken).ConfigureAwait(false);
        NetworkStream netStream = tcpClient.GetStream();

        // Initialize BouncyCastle TLS-PSK client with "PairingKey" identity and 16-byte AES secret
        var crypto = new BcTlsCrypto(new SecureRandom());
        var pskIdentity = new BasicTlsPskIdentity(Encoding.UTF8.GetBytes("PairingKey"), AesKeyBytes);
        var tlsClient = new CamoPskTlsClient(crypto, pskIdentity);
        var protocol = new TlsClientProtocol(netStream);

        protocol.Connect(tlsClient);
        Stream tlsStream = protocol.Stream;

        // 1. Send desktop announcement payload with 4-byte LE length prefix
        var req = new PairingAnnouncementRequest
        {
            Id = DesktopId,
            Name = Environment.MachineName,
            MalheurId = MalheurId
        };
        byte[] reqBytes = CamoJson.SerializeToUtf8Bytes(req);
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, reqBytes.Length);

        await tlsStream.WriteAsync(lenBuf, cancellationToken).ConfigureAwait(false);
        await tlsStream.WriteAsync(reqBytes, cancellationToken).ConfigureAwait(false);
        await tlsStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 2. Read phone paired device record response with 4-byte LE length prefix
        byte[] respLenBuf = new byte[4];
        await tlsStream.ReadExactlyAsync(respLenBuf, cancellationToken).ConfigureAwait(false);
        int respLen = BinaryPrimitives.ReadInt32LittleEndian(respLenBuf);
        if (respLen <= 0 || respLen > 65536)
        {
            throw new InvalidDataException($"Invalid response length received from phone: {respLen}");
        }

        byte[] respBytes = new byte[respLen];
        await tlsStream.ReadExactlyAsync(respBytes, cancellationToken).ConfigureAwait(false);

        var deviceRecord = CamoJson.Deserialize<PairedDeviceRecord>(respBytes);
        if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Id))
        {
            throw new InvalidDataException("Failed to deserialize valid PairedDeviceRecord from phone.");
        }

        deviceRecord.LastConnected = DateTime.UtcNow;
        SavePairedDevice(deviceRecord);

        // 3. Send final success acknowledgement: {"success":true}
        var ack = new PairingSuccessResponse { Success = true };
        byte[] ackBytes = CamoJson.SerializeToUtf8Bytes(ack);
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, ackBytes.Length);

        await tlsStream.WriteAsync(lenBuf, cancellationToken).ConfigureAwait(false);
        await tlsStream.WriteAsync(ackBytes, cancellationToken).ConfigureAwait(false);
        await tlsStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        try { protocol.Close(); } catch { }
        return deviceRecord;
    }

    /// <summary>
    /// Internal BouncyCastle TLS-PSK client configured for Camo protocol versions.
    /// </summary>
    internal sealed class CamoPskTlsClient : PskTlsClient
    {
        public CamoPskTlsClient(TlsCrypto crypto, TlsPskIdentity pskIdentity)
            : base(crypto, pskIdentity)
        {
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            new[] { ProtocolVersion.TLSv11, ProtocolVersion.TLSv12, ProtocolVersion.TLSv13 };
    }

    public static string ToAscii(bool[,] matrix)
    {
        int width = matrix.GetLength(0);
        int height = matrix.GetLength(1);
        StringBuilder sb = new();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                sb.Append(matrix[x, y] ? "██" : "  ");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToSvg(bool[,] matrix, int moduleSize = 8)
    {
        int width = matrix.GetLength(0);
        int height = matrix.GetLength(1);
        int pixelWidth = width * moduleSize;
        int pixelHeight = height * moduleSize;

        StringBuilder sb = new();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{pixelWidth}\" height=\"{pixelHeight}\" viewBox=\"0 0 {pixelWidth} {pixelHeight}\">");
        sb.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (matrix[x, y])
                {
                    sb.AppendLine($"<rect x=\"{x * moduleSize}\" y=\"{y * moduleSize}\" width=\"{moduleSize}\" height=\"{moduleSize}\" fill=\"#000000\"/>");
                }
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Manually triggers immediate Malheur polling for all paired devices and local IDs.
    /// </summary>
    public async Task PollMalheurNowAsync(CancellationToken cancellationToken = default)
    {
        await PollMalheurAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollMalheurAsync(CancellationToken cancellationToken = default)
    {
        if (_pairedDevices.IsEmpty && string.IsNullOrEmpty(MalheurId)) return;

        var malheurIdsToQuery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(MalheurId)) malheurIdsToQuery.Add(MalheurId);
        if (!string.IsNullOrEmpty(DesktopId)) malheurIdsToQuery.Add(DesktopId);

        foreach (var dev in _pairedDevices.Values)
        {
            if (!string.IsNullOrEmpty(dev.MalheurId)) malheurIdsToQuery.Add(dev.MalheurId);
            if (!string.IsNullOrEmpty(dev.Id)) malheurIdsToQuery.Add(dev.Id);
        }

        foreach (var id in malheurIdsToQuery)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://cf.reincubate.com/app_pairing_data/{id}");
                request.Headers.Clear();
                request.Headers.Add("User-Agent", "Camo Studio/2.5.0 (Windows NT 10.0; Win64; x64)");
                request.Headers.Add("Ri-Consent", "True");
                request.Headers.Add("Ri-Instance", DesktopId);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var malheurResp = CamoJson.Deserialize<MalheurResponse>(json);
                if (malheurResp?.Data == null || malheurResp.Data.Count == 0) continue;

                LogMessage?.Invoke($"[Malheur] Received {malheurResp.Data.Count} device entry(ies) for {id}.");

                foreach (var entry in malheurResp.Data)
                {
                    if (string.IsNullOrEmpty(entry.Data) || string.IsNullOrEmpty(entry.Iv)) continue;

                    PairedDeviceRecord? matchedDevice = null;
                    foreach (var dev in _pairedDevices.Values)
                    {
                        if (string.Equals(dev.MalheurId, entry.Uid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(dev.Id, entry.Uid, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedDevice = dev;
                            break;
                        }
                    }

                    if (matchedDevice == null && _pairedDevices.Count == 1)
                    {
                        matchedDevice = _pairedDevices.Values.First();
                    }

                    if (matchedDevice == null || string.IsNullOrEmpty(matchedDevice.MalheurKey)) continue;

                    var endpoint = DecryptMalheurPayload(entry.Data, entry.Iv, matchedDevice.MalheurKey, matchedDevice);
                    if (endpoint == null || endpoint.Ips == null || endpoint.Ips.Count == 0) continue;

                    int port = endpoint.Port > 0 ? endpoint.Port : 9430;
                    string? chosenIp = null;

                    // Prefer IPv4 address on local network (e.g. 192.168.x.x, 10.x.x.x, 172.16-31.x.x)
                    chosenIp = endpoint.Ips.FirstOrDefault(ipStr =>
                    {
                        if (IPAddress.TryParse(ipStr, out var ip))
                        {
                            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !ipStr.StartsWith("127.");
                        }
                        return false;
                    });

                    // Fallback to any valid IP string if no IPv4 candidate
                    chosenIp ??= endpoint.Ips.FirstOrDefault(ipStr => IPAddress.TryParse(ipStr, out _));
                    if (chosenIp == null)
                    {
                        LogMessage?.Invoke($"[Malheur] No valid IP address found among candidate IPs: {string.Join(", ", endpoint.Ips)}");
                        continue;
                    }

                    LogMessage?.Invoke($"[Malheur] Discovered endpoint {chosenIp}:{port} for device '{matchedDevice.Name}'!");

                    matchedDevice.LastKnownIp = chosenIp;
                    matchedDevice.LastKnownPort = port;
                    matchedDevice.LastConnected = DateTime.UtcNow;
                    SavePairedDevice(matchedDevice);

                    if (IPAddress.TryParse(chosenIp, out var targetAddress))
                    {
                        StreamingEndpointDiscovered?.Invoke(matchedDevice, new IPEndPoint(targetAddress, port));
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    LogMessage?.Invoke($"[Malheur] Polling exception for {id}: {ex.Message}");
                }
            }
        }
    }

    private MalheurDecryptedEndpoint? DecryptMalheurPayload(string cipherBase64, string ivBase64, string malheurKeyBase64, PairedDeviceRecord? dev = null)
    {
        var candidateKeyStrings = new List<string> { malheurKeyBase64 };
        if (dev != null && !string.IsNullOrEmpty(dev.Password)) candidateKeyStrings.Add(dev.Password);
        if (AesKeyBytes != null && AesKeyBytes.Length == 16) candidateKeyStrings.Add(Convert.ToBase64String(AesKeyBytes));

        byte[] cipher;
        byte[] iv;
        try
        {
            cipher = Convert.FromBase64String(cipherBase64);
            iv = Convert.FromBase64String(ivBase64);
        }
        catch
        {
            return null;
        }

        foreach (var keyBase64 in candidateKeyStrings.Distinct())
        {
            try
            {
                byte[] key = Convert.FromBase64String(keyBase64);
                if (key.Length != 16 && key.Length != 24 && key.Length != 32) continue;

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                byte[] plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                string json = Encoding.UTF8.GetString(plainBytes);
                LogMessage?.Invoke($"[Malheur] Decrypted plaintext JSON: {json}");

                return CamoJson.Deserialize<MalheurDecryptedEndpoint>(json);
            }
            catch { }
        }

        LogMessage?.Invoke("[Malheur] Decryption failed: Padding is invalid and cannot be removed with any candidate key.");
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        StopDiscovery();
        _httpClient.Dispose();
        await Task.CompletedTask;
    }
}
