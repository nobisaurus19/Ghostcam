using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ghostcam.Core.Discovery;
using Ghostcam.Core.Protocol;
using Ghostcam.Core.Transport;
using Ghostcam.Media.Driver;
using Ghostcam.Media.Pipeline;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ghostcam.Studio.Services;

namespace Ghostcam.Studio.ViewModels;

public enum CamoSourceMode
{
    UsbMobile = 0,
    WifiQrPairing = 1,
    SyntheticTestCamera = 2,
    SafariWebCam = 3
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghostcam", "ghostcam.log");
    private static readonly object LogFileLock = new();

    public static void LogToFile(string message)
    {
        try
        {
            lock (LogFileLock)
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch { }
    }

    private readonly Dispatcher _dispatcher;

    // Core & Media Subsystems
    private readonly SyntheticStudioSource _syntheticSource;
    private readonly VideoFrameProcessor _frameProcessor;
    private readonly CamoVirtualCameraDriver _virtualCameraDriver;
    private readonly GhostCamMfVirtualCamera _ghostCamMf;
    private readonly CamoMobileSessionServer _sessionServer;
    private readonly UsbDeviceBridge _usbBridge;
    private readonly H264NalParser _nalParser;
    private readonly WebCameraServer _webCameraServer;
    private CamoQrPairingService _qrPairingService;

    // Viewport & Rendering State
    private WriteableBitmap? _viewportBitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private readonly object _renderLock = new();
    private byte[]? _latestFrameBgra;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private volatile bool _hasDecodedRealFrame = false;

    // Telemetry & FPS Counters
    private int _isFrameInFlight = 0;
    private int _isUiRenderPending = 0;
    private int _renderedFrameCount = 0;
    private long _webCamFramesDecoded = 0;
    private byte[]? _decodeBgraBuffer;
    private long _webCamBytesInWindow;
    private int _lastReportedWebWidth;
    private int _lastReportedWebHeight;
    private bool _suppressWebCamSync;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private double _currentRenderFps = 0.0;
    private long _lastRealFrameTimestamp;
    private DispatcherTimer? _telemetryTimer;

    // UI State Properties
    private CamoSourceMode _sourceMode = CamoSourceMode.SafariWebCam;
    private BitmapSource? _qrCodeImage;
    private string _qrPairingId = string.Empty;
    private string _qrAesKey = string.Empty;
    private bool _isSafariQrMode = true;
    private string _connectionStatusBadge = "🌐 MOBILE WEBCAM (READY)";
    private string _telemetryResolution = "1280 x 720";
    private string _telemetryFps = "60.0 FPS";
    private string _telemetryBitrate = "14.2 Mbps";
    private string _telemetryNals = "NALs: 0";
    private string _telemetryBattery = "🔋 95% • Charging";
    private string _telemetryThermal = "🌡️ 33.8°C Nominal";
    private string _telemetryClients = "0 Apps connected";
    private bool _isVirtualCamActive = false;
    private string _virtualCamStatusText = "Ghost Cam setup needed (click Start)";
    private string _virtualCamDevicePath = "Click START VIRTUAL CAMERA to register";
    private bool _isPhoneConnected = false;
    private string _connectedPhoneName = "No Mobile Connected";
    private string _usbStatusText = "Scanning USB tunnels (usbmuxd 27015 / adb 9430)...";
    private string _snapshotNotificationText = string.Empty;
    private bool _showSnapshotNotification = false;

    // Hardware Controls
    private string _selectedLens = "Wide 1x";
    private string _selectedResolution = "1280x720 (HD)";
    private string _selectedFps = "60 FPS (Ultra Smooth)";
    private double _hardwareIso = 100;
    private double _hardwareShutter = 60; // 1/60s
    private double _hardwareWbKelvin = 5400; // 5400K
    private double _hardwareFocus = 0.5;
    private bool _hardwareTorch = false;
    private bool _isSensorAwake = true;
    private bool _isDarkTheme = false;

    // Studio Filter Settings
    public StudioFilterSettings FilterSettings { get; } = new();

    // Commands
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand SetSourceModeCommand { get; }
    public RelayCommand ToggleVirtualCameraCommand { get; }
    public RelayCommand MirrorHorizontalCommand { get; }
    public RelayCommand Rotate90Command { get; }
    public RelayCommand ResetAllFiltersCommand { get; }
    public RelayCommand TakeSnapshotCommand { get; }
    public RelayCommand WakeSensorCommand { get; }
    public RelayCommand SleepSensorCommand { get; }
    public RelayCommand ToggleTorchCommand { get; }
    public RelayCommand SelectPresetCommand { get; }
    public RelayCommand ResetFramingCommand { get; }
    public RelayCommand ResetExposureCommand { get; }
    public RelayCommand ResetImageAdjustmentsCommand { get; }
    public RelayCommand RefreshUsbCommand { get; }
    public RelayCommand RegenerateQrCommand { get; }
    public RelayCommand RefreshQrCommand { get; }
    public RelayCommand ToggleSafariQrModeCommand { get; }

    public ObservableCollection<string> AvailableLenses { get; } = new()
    {
        "Wide 1x",
        "Ultra-Wide 0.5x",
        "Telephoto 2x",
        "Front Camera"
    };

    public ObservableCollection<string> AvailableResolutions { get; } = new()
    {
        "3840x2160 (4K UHD) - Unlocked!",
        "1920x1080 (Full HD)",
        "1280x720 (HD)"
    };

    public ObservableCollection<string> AvailableFps { get; } = new()
    {
        "60 FPS (Ultra Smooth)",
        "30 FPS (Standard)"
    };

    #region Bindable Properties

    public CamoSourceMode SourceMode
    {
        get => _sourceMode;
        set
        {
            if (SetProperty(ref _sourceMode, value))
            {
                OnPropertyChanged(nameof(IsUsbMode));
                OnPropertyChanged(nameof(IsWifiQrMode));
                OnPropertyChanged(nameof(IsSyntheticMode));
                OnPropertyChanged(nameof(IsWaitingForSignal));
                UpdateSourcePipeline();
            }
        }
    }

    public bool IsUsbMode => SourceMode == CamoSourceMode.UsbMobile;
    public bool IsWifiQrMode => SourceMode == CamoSourceMode.WifiQrPairing || SourceMode == CamoSourceMode.SafariWebCam;
    public bool IsSyntheticMode => SourceMode == CamoSourceMode.SyntheticTestCamera;
    public bool IsWaitingForSignal => !IsSyntheticMode && !IsPhoneConnected;

    public WriteableBitmap? ViewportBitmap
    {
        get => _viewportBitmap;
        private set => SetProperty(ref _viewportBitmap, value);
    }

    public BitmapSource? QrCodeImage
    {
        get => _qrCodeImage;
        private set => SetProperty(ref _qrCodeImage, value);
    }

    public string QrPairingId
    {
        get => _qrPairingId;
        private set => SetProperty(ref _qrPairingId, value);
    }

    public string QrAesKey
    {
        get => _qrAesKey;
        private set => SetProperty(ref _qrAesKey, value);
    }

    public bool IsSafariQrMode
    {
        get => _isSafariQrMode;
        set
        {
            if (SetProperty(ref _isSafariQrMode, value))
            {
                GenerateQrCodeImage();
                OnPropertyChanged(nameof(QrToggleText));
                OnPropertyChanged(nameof(QrSubtitleText));
                OnPropertyChanged(nameof(QrUrlText));
                OnPropertyChanged(nameof(QrBoxHeader));
            }
        }
    }

    public string QrBoxHeader => "📱 SCAN WITH PHONE CAMERA (SAFARI / CHROME)";

    public string QrToggleText => "🔄 รีเฟรช QR / อัปเดต IP เครือข่าย";

    public string QrSubtitleText => "เปิดแอพกล้องบน iPhone หรือ Android แล้วสแกน QR เพื่อถ่ายทอดสดกล้องได้ทันที (ไม่ต้องลงแอพ)";

    public string QrUrlText => _webCameraServer.HttpsUrl;

    public string ConnectionStatusBadge
    {
        get => _connectionStatusBadge;
        set => SetProperty(ref _connectionStatusBadge, value);
    }

    public string TelemetryResolution
    {
        get => _telemetryResolution;
        set => SetProperty(ref _telemetryResolution, value);
    }

    public string TelemetryFps
    {
        get => _telemetryFps;
        set => SetProperty(ref _telemetryFps, value);
    }

    public string TelemetryBitrate
    {
        get => _telemetryBitrate;
        set => SetProperty(ref _telemetryBitrate, value);
    }

    public string TelemetryNals
    {
        get => _telemetryNals;
        set => SetProperty(ref _telemetryNals, value);
    }

    public string TelemetryBattery
    {
        get => _telemetryBattery;
        set => SetProperty(ref _telemetryBattery, value);
    }

    public string TelemetryThermal
    {
        get => _telemetryThermal;
        set => SetProperty(ref _telemetryThermal, value);
    }

    public string TelemetryClients
    {
        get => _telemetryClients;
        set => SetProperty(ref _telemetryClients, value);
    }

    public bool IsVirtualCamActive
    {
        get => _isVirtualCamActive;
        set => SetProperty(ref _isVirtualCamActive, value);
    }

    public string VirtualCamStatusText
    {
        get => _virtualCamStatusText;
        set => SetProperty(ref _virtualCamStatusText, value);
    }

    public string VirtualCamDevicePath
    {
        get => _virtualCamDevicePath;
        set => SetProperty(ref _virtualCamDevicePath, value);
    }

    public bool IsPhoneConnected
    {
        get => _isPhoneConnected;
        set
        {
            if (SetProperty(ref _isPhoneConnected, value))
            {
                OnPropertyChanged(nameof(IsWaitingForSignal));
            }
        }
    }

    public string ConnectedPhoneName
    {
        get => _connectedPhoneName;
        set => SetProperty(ref _connectedPhoneName, value);
    }

    public string UsbStatusText
    {
        get => _usbStatusText;
        set => SetProperty(ref _usbStatusText, value);
    }

    public string SnapshotNotificationText
    {
        get => _snapshotNotificationText;
        set => SetProperty(ref _snapshotNotificationText, value);
    }

    public bool ShowSnapshotNotification
    {
        get => _showSnapshotNotification;
        set => SetProperty(ref _showSnapshotNotification, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                App.SetTheme(value);
            }
        }
    }

    // Hardware Controls
    public string SelectedLens
    {
        get => _selectedLens;
        set
        {
            if (SetProperty(ref _selectedLens, value))
            {
                string webLens = "back-wide";
                if (value.Contains("0.5") || value.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
                    webLens = "back-ultra";
                else if (value.Contains("2x") || value.Contains("Tele", StringComparison.OrdinalIgnoreCase))
                    webLens = "back-tele";
                else if (value.Contains("Front", StringComparison.OrdinalIgnoreCase))
                    webLens = "front";

                if (!_suppressWebCamSync)
                {
                    _webCameraServer.UpdateDesiredState(lens: webLens);
                    _ = SyncSettingsToPhoneAsync();
                }
            }
        }
    }

    public string SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (SetProperty(ref _selectedResolution, value))
            {
                ApplyResolutionChange();
                if (!_suppressWebCamSync)
                {
                    (int w, int h, _) = ParseSelectedResolutionAndFps();
                    _webCameraServer.UpdateDesiredState(width: w, height: h);
                    _ = SyncSettingsToPhoneAsync();
                }
            }
        }
    }

    public string SelectedFps
    {
        get => _selectedFps;
        set
        {
            if (SetProperty(ref _selectedFps, value))
            {
                ApplyFpsChange();
                if (!_suppressWebCamSync)
                {
                    (_, _, int fps) = ParseSelectedResolutionAndFps();
                    _webCameraServer.UpdateDesiredState(fps: fps);
                    _ = SyncSettingsToPhoneAsync();
                }
            }
        }
    }

    public double HardwareIso
    {
        get => _hardwareIso;
        set
        {
            if (SetProperty(ref _hardwareIso, value))
            {
                FilterSettings.HardwareIso = (float)value;
                _webCameraServer.UpdateDesiredState(iso: value);
                _ = SyncSettingsToPhoneAsync();
            }
        }
    }

    public double HardwareShutter
    {
        get => _hardwareShutter;
        set
        {
            if (SetProperty(ref _hardwareShutter, value))
            {
                FilterSettings.HardwareShutter = (float)value;
                _webCameraServer.UpdateDesiredState(shutter: value);
                _ = SyncSettingsToPhoneAsync();
            }
        }
    }

    public double HardwareWbKelvin
    {
        get => _hardwareWbKelvin;
        set
        {
            if (SetProperty(ref _hardwareWbKelvin, value))
            {
                FilterSettings.HardwareWbKelvin = (float)value;
                _webCameraServer.UpdateDesiredState(wb: value);
                _ = SyncSettingsToPhoneAsync();
            }
        }
    }

    public double HardwareFocus
    {
        get => _hardwareFocus;
        set
        {
            if (SetProperty(ref _hardwareFocus, value))
            {
                FilterSettings.HardwareFocus = (float)value;
                _webCameraServer.UpdateDesiredState(focus: value);
                _ = SyncSettingsToPhoneAsync();
            }
        }
    }

    public bool HardwareTorch
    {
        get => _hardwareTorch;
        set
        {
            if (SetProperty(ref _hardwareTorch, value))
            {
                _ = SyncSettingsToPhoneAsync();
            }
        }
    }

    public bool IsSensorAwake
    {
        get => _isSensorAwake;
        set => SetProperty(ref _isSensorAwake, value);
    }

    public StudioColorPreset ActivePreset
    {
        get => FilterSettings.ColorPreset;
        set
        {
            FilterSettings.ColorPreset = value;
            OnPropertyChanged();
        }
    }

    #endregion

    static MainViewModel()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                var assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    var assemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyName + ".dll");
                    if (File.Exists(assemblyPath))
                    {
                        return System.Reflection.Assembly.LoadFrom(assemblyPath);
                    }
                }
            }
            catch
            {
            }
            return null;
        };
    }

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // 1. Initialize Processing & Media Subsystems
        _frameProcessor = new VideoFrameProcessor();
        _syntheticSource = new SyntheticStudioSource();
        _virtualCameraDriver = new CamoVirtualCameraDriver();
        _ghostCamMf = new GhostCamMfVirtualCamera();
        _nalParser = new H264NalParser();

        // 2. Initialize Transport & Discovery Subsystems
        _sessionServer = new CamoMobileSessionServer();
        _usbBridge = new UsbDeviceBridge(_sessionServer);
        _qrPairingService = new CamoQrPairingService();
        _webCameraServer = new WebCameraServer();
        AttachQrPairingHandlers();

        // Setup Virtual Camera Status
        if (GhostCamMfVirtualCamera.IsComRegistered && GhostCamMfVirtualCamera.IsInstalledDllUpToDate)
        {
            (int w, int h, int fps) = ParseSelectedResolutionAndFps();
            bool started = _ghostCamMf.StartVirtualCamera(w, h, fps);
            _ghostCamMf.SetActiveStreaming(false);

            VirtualCamDevicePath = "Windows 11 Media Foundation (Ghost Cam)";
            if (started && _ghostCamMf.IsSessionRunning)
            {
                VirtualCamStatusText = "Ghost Cam ready (standby)";
                LogToFile($"[VirtualCam] Ghost Cam standby session initialized (IsComRegistered=true, started=true)");
            }
            else
            {
                VirtualCamStatusText = $"Ghost Cam standby failed: {_ghostCamMf.LastError}";
                LogToFile($"[VirtualCam] Ghost Cam standby failed: {_ghostCamMf.LastError}");
            }
        }
        else
        {
            VirtualCamStatusText = "Ghost Cam setup needed (click Start)";
            VirtualCamDevicePath = "Click START VIRTUAL CAMERA to register";
            LogToFile("[VirtualCam] Ghost Cam not registered in COM or DLL outdated; click Start to register via UAC");
        }

        // Initialize QR Pairing Image
        GenerateQrCodeImage();

        // 3. Setup Commands
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        SetSourceModeCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse<CamoSourceMode>(s, out var mode))
            {
                SourceMode = mode;
            }
            else if (p is CamoSourceMode m)
            {
                SourceMode = m;
            }
        });

        ToggleVirtualCameraCommand = new RelayCommand(ToggleVirtualCamera);
        MirrorHorizontalCommand = new RelayCommand(() => FilterSettings.MirrorHorizontal = !FilterSettings.MirrorHorizontal);
        Rotate90Command = new RelayCommand(() => FilterSettings.RotationDegrees = (FilterSettings.RotationDegrees + 90) % 360);
        ResetAllFiltersCommand = new RelayCommand(() =>
        {
            FilterSettings.Reset();
            ActivePreset = StudioColorPreset.None;
            HardwareIso = 100;
            HardwareShutter = 60;
            HardwareWbKelvin = 5400;
            HardwareFocus = 0.5;
        });
        TakeSnapshotCommand = new RelayCommand(ExecuteTakeSnapshot);
        WakeSensorCommand = new RelayCommand(async () =>
        {
            IsSensorAwake = true;
            _webCameraServer.UpdateDesiredState(paused: false);
            if (_sessionServer.IsClientConnected)
            {
                await _sessionServer.SendWakeAsync();
            }
            UsbStatusText = "Sensor awake. Streaming active.";
        });
        SleepSensorCommand = new RelayCommand(async () =>
        {
            IsSensorAwake = false;
            _webCameraServer.UpdateDesiredState(paused: true);
            if (_sessionServer.IsClientConnected)
            {
                await _sessionServer.SendSleepAsync();
            }
            UsbStatusText = "Sensor sleep requested.";
        });
        ToggleTorchCommand = new RelayCommand(() =>
        {
            HardwareTorch = !HardwareTorch;
            _webCameraServer.UpdateDesiredState(torch: HardwareTorch);
            if (_sessionServer.IsClientConnected)
            {
                _ = SyncSettingsToPhoneAsync();
            }
        });
        SelectPresetCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse<StudioColorPreset>(s, out var preset))
            {
                ActivePreset = preset;
            }
            else if (p is StudioColorPreset pr)
            {
                ActivePreset = pr;
            }
        });
        ResetFramingCommand = new RelayCommand(() =>
        {
            FilterSettings.Zoom = 1.0f;
            FilterSettings.PanX = 0.0f;
            FilterSettings.PanY = 0.0f;
        });
        ResetExposureCommand = new RelayCommand(() =>
        {
            HardwareIso = 100;
            HardwareShutter = 60;
            HardwareWbKelvin = 5400;
            HardwareFocus = 0.5;
        });
        ResetImageAdjustmentsCommand = new RelayCommand(() =>
        {
            FilterSettings.Brightness = 0.0f;
            FilterSettings.Contrast = 0.0f;
            FilterSettings.Saturation = 0.0f;
            FilterSettings.Temperature = 0.0f;
            FilterSettings.Tint = 0.0f;
            FilterSettings.Sharpness = 0.0f;
            FilterSettings.Vignette = 0.0f;
        });
        RefreshUsbCommand = new RelayCommand(async () =>
        {
            UsbStatusText = "Refreshing USB tunnels...";
            await _usbBridge.StartAsync();
            UsbStatusText = $"Monitoring {_usbBridge.ConnectedDevices.Count} devices.";
        });
        RegenerateQrCommand = new RelayCommand(() =>
        {
            _webCameraServer.RefreshLanIp();
            GenerateQrCodeImage();
            OnPropertyChanged(nameof(QrUrlText));
            UsbStatusText = $"Refreshed QR Code for {_webCameraServer.HttpsUrl}";
        });
        RefreshQrCommand = new RelayCommand(() =>
        {
            _webCameraServer.RefreshLanIp();
            GenerateQrCodeImage();
            OnPropertyChanged(nameof(QrUrlText));
            UsbStatusText = $"Refreshed QR Code for {_webCameraServer.HttpsUrl}";
        });
        ToggleSafariQrModeCommand = new RelayCommand(() =>
        {
            IsSafariQrMode = true;
            GenerateQrCodeImage();
        });

        // 4. Hook Events
        _syntheticSource.OnFrameGenerated += HandleSyntheticFrame;
        _nalParser.OnFrameDecoded += (bgraFrame, width, height) =>
        {
            _hasDecodedRealFrame = true;
            if (_syntheticSource.IsRunning)
                _syntheticSource.Stop();
            ProcessAndRenderFrame(bgraFrame, width, height);
        };
        _sessionServer.ClientConnected += HandleClientConnected;
        _sessionServer.ClientDisconnected += HandleClientDisconnected;
        _sessionServer.DeviceInfoUpdated += HandleDeviceInfoUpdated;
        _sessionServer.VideoPacketReceived += HandleVideoPacketReceived;
        _sessionServer.LogMessage += msg =>
        {
            LogToFile(msg);
            _ = _dispatcher.BeginInvoke(() => UsbStatusText = msg);
        };
        _usbBridge.DeviceConnected += HandleUsbDeviceConnected;
        _usbBridge.DeviceDisconnected += HandleUsbDeviceDisconnected;
        _usbBridge.LogMessage += msg => UsbStatusText = msg;

        // Safari WebCam Server Hooks
        _webCameraServer.LogMessage += msg => LogToFile(msg);
        _webCameraServer.ClientConnected += ep =>
        {
            Interlocked.Exchange(ref _lastRealFrameTimestamp, Stopwatch.GetTimestamp());
            LogToFile($"[WebCam] Client connected: {ep}");
            _ = _dispatcher.BeginInvoke(() =>
            {
                IsPhoneConnected = true;
                ConnectedPhoneName = $"Safari iPhone ({ep})";
                ConnectionStatusBadge = "🌐 SAFARI WEBCAM";
                UsbStatusText = $"Safari client connected from {ep}!";
            });
        };
        _webCameraServer.JpegFrameReceived += HandleJpegFrame;
        _webCameraServer.ClientTelemetryReceived += HandleWebCamTelemetry;

        // 5. Start Telemetry Timer (updates at 2 Hz)
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _telemetryTimer.Tick += OnTelemetryTick;
        _telemetryTimer.Start();

        // 6. Defer background services and synthetic camera until window finishes loading
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            EnsureProcessWindowTitles();
            _ = _sessionServer.StartAsync(9430);
            _ = _usbBridge.StartAsync();
            (int initW, int initH, int initFps) = ParseSelectedResolutionAndFps();
            _webCameraServer.UpdateDesiredState(width: initW, height: initH, fps: initFps);
            _ = _webCameraServer.StartAsync();

            SourceMode = CamoSourceMode.SafariWebCam;
            ConnectionStatusBadge = "🌐 MOBILE WEBCAM (READY)";
            UsbStatusText = $"Mobile WebCam server listening on {_webCameraServer.HttpsUrl}";
            GenerateQrCodeImage();
            UpdateSourcePipeline();
        });
    }

    private void GenerateQrCodeImage()
    {
        string url = _webCameraServer.HttpsUrl;
        QrPairingId = "Safari / Chrome Mobile WebCam";
        QrAesKey = url;

        bool[,] matrix = CamoQrPairingService.GenerateQrMatrix(url, quietZone: 3);
        QrCodeImage = QrCodeImageHelper.CreateQrBitmap(matrix, scale: 6, padding: 2);
    }

    private void HandleJpegFrame(byte[] jpegBytes)
    {
        if (Volatile.Read(ref _isFrameInFlight) != 0)
            return;

        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var decoder = new JpegBitmapDecoder(
                ms,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                int width = frame.PixelWidth;
                int height = frame.PixelHeight;

                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
                int requiredBytes = width * height * 4;
                if (_decodeBgraBuffer == null || _decodeBgraBuffer.Length != requiredBytes)
                {
                    _decodeBgraBuffer = new byte[requiredBytes];
                }
                byte[] bgra = _decodeBgraBuffer;
                converted.CopyPixels(bgra, width * 4, 0);

                _hasDecodedRealFrame = true;
                Interlocked.Exchange(ref _lastRealFrameTimestamp, Stopwatch.GetTimestamp());
                if (_syntheticSource.IsRunning)
                {
                    _syntheticSource.Stop();
                }

                Interlocked.Add(ref _webCamBytesInWindow, jpegBytes.Length);
                long count = Interlocked.Increment(ref _webCamFramesDecoded);
                int mid = (height / 2 * width + width / 2) * 4;
                if (mid >= 0 && mid + 3 < bgra.Length && (count <= 5 || count % 150 == 0))
                {
                    LogToFile($"[WebCam] Rendered JPEG #{count}: {width}x{height} ({jpegBytes.Length}B), midPixel=({bgra[mid]},{bgra[mid+1]},{bgra[mid+2]},{bgra[mid+3]})");
                }

                if (!IsPhoneConnected || SourceMode != CamoSourceMode.SafariWebCam || _lastReportedWebWidth != width || _lastReportedWebHeight != height)
                {
                    _lastReportedWebWidth = width;
                    _lastReportedWebHeight = height;
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        SourceMode = CamoSourceMode.SafariWebCam;
                        ConnectionStatusBadge = "🟢 SAFARI WEBCAM (LIVE)";
                        IsPhoneConnected = true;
                        if (string.IsNullOrEmpty(ConnectedPhoneName) || ConnectedPhoneName == "No Mobile Connected")
                            ConnectedPhoneName = "iPhone (Safari Live)";
                        UsbStatusText = $"Streaming live from iPhone ({width}x{height})";
                    });
                }

                ProcessAndRenderFrame(bgra, width, height);
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[WebCam] HandleJpegFrame error: {ex.Message}");
        }
    }

    private void HandleWebCamTelemetry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;

            string? type = typeProp.GetString();
            if (type == "caps")
            {
                int w = root.TryGetProperty("w", out var pw) && pw.TryGetInt32(out int valW) ? valW : 0;
                int h = root.TryGetProperty("h", out var ph) && ph.TryGetInt32(out int valH) ? valH : 0;
                double fps = root.TryGetProperty("fps", out var pfps) && pfps.TryGetDouble(out double valFps) ? valFps : 0.0;
                int maxW = root.TryGetProperty("maxW", out var pmw) && pmw.TryGetInt32(out int valMw) ? valMw : 0;
                int maxH = root.TryGetProperty("maxH", out var pmh) && pmh.TryGetInt32(out int valMh) ? valMh : 0;
                double maxFps = root.TryGetProperty("maxFps", out var pmfps) && pmfps.TryGetDouble(out double valMfps) ? valMfps : 0.0;
                int reqW = root.TryGetProperty("reqW", out var prw) && prw.TryGetInt32(out int valRw) ? valRw : 0;
                int reqH = root.TryGetProperty("reqH", out var prh) && prh.TryGetInt32(out int valRh) ? valRh : 0;
                double reqFps = root.TryGetProperty("reqFps", out var prfps) && prfps.TryGetDouble(out double valRfps) ? valRfps : 0.0;

                LogToFile($"[WebCam] Phone camera negotiated {w}x{h}@{fps:F1} (requested {reqW}x{reqH}@{reqFps}, browser max {maxW}x{maxH}@{maxFps})");

                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (reqW >= 3840 && Math.Max(w, h) < 2500)
                    {
                        UsbStatusText = $"Camera active (Safari capped hardware stream to {w}x{h} @ {fps:F0} FPS)";
                    }
                    else
                    {
                        UsbStatusText = $"Camera active: {w}x{h} @ {fps:F0} FPS";
                    }
                });
            }
            else if (type == "userPrefs")
            {
                int? w = root.TryGetProperty("w", out var pw) && pw.TryGetInt32(out int vw) ? vw : null;
                int? h = root.TryGetProperty("h", out var ph) && ph.TryGetInt32(out int vh) ? vh : null;
                int? fps = root.TryGetProperty("fps", out var pfps) && pfps.TryGetInt32(out int vfps) ? vfps : null;
                string? lens = root.TryGetProperty("lens", out var plens) ? plens.GetString() : null;
                bool? torch = root.TryGetProperty("torch", out var ptorch) && (ptorch.ValueKind == JsonValueKind.True || ptorch.ValueKind == JsonValueKind.False) ? ptorch.GetBoolean() : null;

                _webCameraServer.ApplyClientPreferences(w, h, fps, lens);

                _ = _dispatcher.BeginInvoke(() =>
                {
                    _suppressWebCamSync = true;
                    try
                    {
                        if (w.HasValue)
                        {
                            SelectedResolution = w.Value switch
                            {
                                >= 3840 => "3840x2160 (4K UHD) - Unlocked!",
                                >= 1920 => "1920x1080 (Full HD)",
                                _ => "1280x720 (HD)"
                            };
                        }

                        if (fps.HasValue)
                        {
                            SelectedFps = fps.Value >= 50 ? "60 FPS (Ultra Smooth)" : "30 FPS (Standard)";
                        }

                        if (!string.IsNullOrEmpty(lens))
                        {
                            SelectedLens = lens switch
                            {
                                not null when lens.Contains("0.5", StringComparison.OrdinalIgnoreCase) || lens.Contains("ultra", StringComparison.OrdinalIgnoreCase) => "Ultra-Wide 0.5x",
                                not null when lens.Contains("2x", StringComparison.OrdinalIgnoreCase) || lens.Contains("tele", StringComparison.OrdinalIgnoreCase) => "Telephoto 2x",
                                not null when lens.Contains("front", StringComparison.OrdinalIgnoreCase) => "Front Camera",
                                _ => "Wide 1x"
                            };
                        }

                        if (torch.HasValue)
                        {
                            HardwareTorch = torch.Value;
                        }
                    }
                    finally
                    {
                        _suppressWebCamSync = false;
                    }
                });
            }
            else if (type == "stats")
            {
                // Optionally log or keep clean
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[WebCam] Telemetry parse error: {ex.Message}");
        }
    }

    private void AttachQrPairingHandlers()
    {
        _qrPairingService.LogMessage += msg =>
        {
            LogToFile($"[QR] {msg}");
            _ = _dispatcher.BeginInvoke(() => UsbStatusText = msg);
        };

        _qrPairingService.DevicePaired += (device, endpoint) =>
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                IsPhoneConnected = true;
                ConnectedPhoneName = $"{device.Name} (Paired @ {endpoint.Address})";
                ConnectionStatusBadge = "🟢 WI-FI PAIRED";
                UsbStatusText = $"Paired with {device.Name}! Connecting video stream...";
            });

            if (device.LastKnownPort is > 0 and not 9430)
            {
                int sPort = device.LastKnownPort.Value;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _sessionServer.ConnectToPairedPhoneWifiAsync(
                            endpoint.Address.ToString(),
                            sPort,
                            _qrPairingService.DesktopId,
                            device.Password,
                            device.PublicKey);
                    }
                    catch (Exception ex)
                    {
                        _ = _dispatcher.BeginInvoke(() => UsbStatusText = $"Immediate stream connect: {ex.Message}");
                    }
                });
            }
        };

        _qrPairingService.StreamingEndpointDiscovered += (device, endpoint) =>
        {
            if (SourceMode == CamoSourceMode.SafariWebCam)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _sessionServer.ConnectToPairedPhoneWifiAsync(
                        endpoint.Address.ToString(),
                        endpoint.Port,
                        _qrPairingService.DesktopId,
                        device.Password,
                        device.PublicKey);
                }
                catch (Exception ex)
                {
                    _ = _dispatcher.BeginInvoke(() => UsbStatusText = $"Wi-Fi Stream connect retry: {ex.Message}");
                }
            });
        };
    }

    private void ResetStalePhoneConnectionIfNeeded()
    {
        if (IsPhoneConnected && (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRealFrameTimestamp)) > Stopwatch.Frequency * 3)
        {
            IsPhoneConnected = false;
            ConnectedPhoneName = "No Mobile Connected";
        }
    }

    private void UpdateSourcePipeline()
    {
        switch (SourceMode)
        {
            case CamoSourceMode.SyntheticTestCamera:
                ConnectionStatusBadge = "🎬 TEST CAMERA (60 FPS)";
                if (!_syntheticSource.IsRunning)
                {
                    (int w, int h, int fps) = ParseSelectedResolutionAndFps();
                    _syntheticSource.Start(w, h, fps);
                }
                OnPropertyChanged(nameof(IsWaitingForSignal));
                break;

            case CamoSourceMode.UsbMobile:
                _syntheticSource.Stop();
                ResetStalePhoneConnectionIfNeeded();
                ConnectionStatusBadge = IsPhoneConnected ? "📱 CAMO USB (CONNECTED)" : "📱 CAMO USB (WAITING)";
                if (!IsPhoneConnected)
                {
                    ViewportBitmap = null;
                }
                OnPropertyChanged(nameof(IsWaitingForSignal));
                break;

            case CamoSourceMode.SafariWebCam:
            case CamoSourceMode.WifiQrPairing:
                _syntheticSource.Stop();
                ResetStalePhoneConnectionIfNeeded();
                ConnectionStatusBadge = IsPhoneConnected ? "🟢 MOBILE WEBCAM (LIVE)" : "🌐 MOBILE WEBCAM (READY)";
                if (!IsPhoneConnected)
                {
                    ViewportBitmap = null;
                }
                OnPropertyChanged(nameof(IsWaitingForSignal));
                break;
        }
    }

    private (int width, int height, int fps) ParseSelectedResolutionAndFps()
    {
        int width = 1920;
        int height = 1080;
        int fps = 60;

        if (SelectedResolution.Contains("3840"))
        {
            width = 3840;
            height = 2160;
        }
        else if (SelectedResolution.Contains("1280"))
        {
            width = 1280;
            height = 720;
        }

        if (SelectedFps.Contains("30"))
        {
            fps = 30;
        }

        return (width, height, fps);
    }

    private void ApplyResolutionChange()
    {
        (int w, int h, int fps) = ParseSelectedResolutionAndFps();
        if (SourceMode == CamoSourceMode.SyntheticTestCamera && _syntheticSource.IsRunning)
        {
            _syntheticSource.Start(w, h, fps);
        }
        if (_ghostCamMf.IsSessionRunning || GhostCamMfVirtualCamera.IsComRegistered)
        {
            _ghostCamMf.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
            if (IsVirtualCamActive)
            {
                VirtualCamStatusText = $"Ghost Cam LIVE ({_ghostCamMf.ActiveWidth}x{_ghostCamMf.ActiveHeight} @ {_ghostCamMf.ActiveFps} FPS)";
            }
        }
        else if (IsVirtualCamActive && _virtualCameraDriver.IsDriverConnected)
        {
            _virtualCameraDriver.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
            VirtualCamStatusText = $"camodriver.sys connected ({_virtualCameraDriver.ActiveWidth}x{_virtualCameraDriver.ActiveHeight} @ {_virtualCameraDriver.ActiveFps} FPS)";
        }
    }

    private void ApplyFpsChange()
    {
        (int w, int h, int fps) = ParseSelectedResolutionAndFps();
        if (SourceMode == CamoSourceMode.SyntheticTestCamera && _syntheticSource.IsRunning)
        {
            _syntheticSource.Start(w, h, fps);
        }
        if (_ghostCamMf.IsSessionRunning || GhostCamMfVirtualCamera.IsComRegistered)
        {
            _ghostCamMf.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
            if (IsVirtualCamActive)
            {
                VirtualCamStatusText = $"Ghost Cam LIVE ({_ghostCamMf.ActiveWidth}x{_ghostCamMf.ActiveHeight} @ {_ghostCamMf.ActiveFps} FPS)";
            }
        }
        else if (IsVirtualCamActive && _virtualCameraDriver.IsDriverConnected)
        {
            _virtualCameraDriver.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
            VirtualCamStatusText = $"camodriver.sys connected ({_virtualCameraDriver.ActiveWidth}x{_virtualCameraDriver.ActiveHeight} @ {_virtualCameraDriver.ActiveFps} FPS)";
        }
    }

    private void HandleSyntheticFrame(byte[] bgraFrame, int width, int height)
    {
        if (SourceMode != CamoSourceMode.SyntheticTestCamera) return;
        ProcessAndRenderFrame(bgraFrame, width, height);
    }

    private void HandleVideoPacketReceived(VideoFramePacket packet)
    {
        Interlocked.Exchange(ref _lastRealFrameTimestamp, Stopwatch.GetTimestamp());
        if (_syntheticSource.IsRunning)
            _syntheticSource.Stop();

        // Feed H.264 NAL parser
        _nalParser.FeedChunk(packet.Payload.Span);
        TelemetryNals = $"NALs: {_nalParser.Stats.TotalNalUnits:N0}";
        TelemetryBitrate = $"{_nalParser.Stats.BitrateKbps / 1000.0:F1} Mbps";

        if (!_hasDecodedRealFrame)
        {
            int w = _nalParser.Stats.Width > 0 ? _nalParser.Stats.Width : 1280;
            int h = _nalParser.Stats.Height > 0 ? _nalParser.Stats.Height : 720;
            byte[] frame = new byte[w * h * 4];
            _nalParser.RenderAwaitingKeyframeFallback(frame, w, h);
            ProcessAndRenderFrame(frame, w, h);
        }
    }

    private void ProcessAndRenderFrame(byte[] sourceBgra, int width, int height)
    {
        if (Interlocked.CompareExchange(ref _isFrameInFlight, 1, 0) != 0)
            return;

        try
        {
            if (_dispatcher.HasShutdownStarted)
                return;

            // 1. Process studio frame through 60 FPS pixel pipeline
            byte[] processedBgra = _frameProcessor.ProcessFrame(sourceBgra, width, height, FilterSettings);

            // Store latest frame for snapshots and decoupled UI viewport render
            lock (_renderLock)
            {
                if (_latestFrameBgra == null || _latestFrameBgra.Length != processedBgra.Length)
                {
                    _latestFrameBgra = new byte[processedBgra.Length];
                }
                Buffer.BlockCopy(processedBgra, 0, _latestFrameBgra, 0, processedBgra.Length);
                _latestFrameWidth = width;
                _latestFrameHeight = height;
            }

            // 2. Inject into Virtual Camera if active
            if (_isVirtualCamActive)
            {
                if (_ghostCamMf.IsSessionRunning)
                {
                    _ghostCamMf.PushBgraFrame(processedBgra.AsSpan(), width, height);
                }
                else if (_virtualCameraDriver.IsDriverConnected)
                {
                    _virtualCameraDriver.PushBgraFrame(processedBgra.AsSpan(), width, height);
                }
            }

            Interlocked.Increment(ref _renderedFrameCount);
        }
        finally
        {
            Interlocked.Exchange(ref _isFrameInFlight, 0);
        }

        // 3. Deliver to WPF Viewport asynchronously (non-blocking for capture/virtual cam pipeline)
        if (!_dispatcher.HasShutdownStarted &&
            Interlocked.CompareExchange(ref _isUiRenderPending, 1, 0) == 0)
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    if (IsWaitingForSignal)
                        return;

                    lock (_renderLock)
                    {
                        if (_latestFrameBgra != null && _latestFrameWidth > 0 && _latestFrameHeight > 0 &&
                            _latestFrameBgra.Length >= _latestFrameWidth * _latestFrameHeight * 4)
                        {
                            EnsureViewportBitmap(_latestFrameWidth, _latestFrameHeight);
                            _viewportBitmap?.WritePixels(
                                new Int32Rect(0, 0, _latestFrameWidth, _latestFrameHeight),
                                _latestFrameBgra,
                                _latestFrameWidth * 4,
                                0);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isUiRenderPending, 0);
                }
            });
        }
    }

    private void EnsureViewportBitmap(int width, int height)
    {
        if (_viewportBitmap == null || _bitmapWidth != width || _bitmapHeight != height)
        {
            _bitmapWidth = width;
            _bitmapHeight = height;
            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            ViewportBitmap = bmp;
            OnPropertyChanged(nameof(ViewportBitmap));
            LogToFile($"[Viewport] Created and bound WriteableBitmap ({width}x{height}, Bgr32)");
            TelemetryResolution = $"{width} x {height}";
        }
    }

    private int _titleSyncTicks = 0;

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        if (_titleSyncTicks < 10)
        {
            _titleSyncTicks++;
            EnsureProcessWindowTitles();
        }

        // Calculate rendering FPS
        double seconds = _fpsStopwatch.Elapsed.TotalSeconds;
        if (seconds >= 0.5)
        {
            _currentRenderFps = _renderedFrameCount / seconds;
            _renderedFrameCount = 0;
            _fpsStopwatch.Restart();

            TelemetryFps = $"{_currentRenderFps:F1} FPS";

            long webBytes = Interlocked.Exchange(ref _webCamBytesInWindow, 0);
            if (SourceMode == CamoSourceMode.SafariWebCam && IsPhoneConnected)
            {
                double mbps = (webBytes * 8.0) / seconds / 1_000_000.0;
                TelemetryBitrate = $"{mbps:F1} Mbps";
                TelemetryNals = $"Frames: {Volatile.Read(ref _webCamFramesDecoded):N0}";
            }
        }

        if ((SourceMode == CamoSourceMode.SafariWebCam || SourceMode == CamoSourceMode.WifiQrPairing) && IsPhoneConnected)
        {
            long lastReal = Interlocked.Read(ref _lastRealFrameTimestamp);
            if (lastReal > 0 && (Stopwatch.GetTimestamp() - lastReal) > Stopwatch.Frequency * 4)
            {
                IsPhoneConnected = false;
                ConnectionStatusBadge = "🌐 MOBILE WEBCAM (READY)";
                ConnectedPhoneName = "No Mobile Connected";
                TelemetryFps = "0.0 FPS";
                TelemetryBitrate = "0.0 Mbps";
                ViewportBitmap = null;
            }
        }

        // Query Virtual Camera connected applications
        try
        {
            if (_ghostCamMf.IsSessionRunning || GhostCamMfVirtualCamera.IsComRegistered)
            {
                // When using Ghost Cam Media Foundation Virtual Camera, do not overwrite status with camodriver.sys
                TelemetryClients = _isVirtualCamActive ? "Ghost Cam Streaming" : "Ghost Cam Standby";
            }
            else if (_virtualCameraDriver.IsDriverConnected)
            {
                var pids = _virtualCameraDriver.GetConnectedClientPids();
                TelemetryClients = pids.Length > 0 ? $"{pids.Length} App(s) Active" : "0 Apps connected";
                if (IsVirtualCamActive && _virtualCameraDriver.ActiveWidth > 0)
                {
                    VirtualCamStatusText = $"camodriver.sys connected ({_virtualCameraDriver.ActiveWidth}x{_virtualCameraDriver.ActiveHeight} @ {_virtualCameraDriver.ActiveFps} FPS - {pids.Length} apps)";
                }
            }
            else
            {
                TelemetryClients = "Driver Offline";
            }
        }
        catch
        {
            TelemetryClients = "Driver Idle";
        }
    }

    private void ToggleVirtualCamera()
    {
        // 1. If Ghost Cam is not registered in COM or installed DLL is outdated, prompt elevated UAC installer
        if (!GhostCamMfVirtualCamera.IsComRegistered || !GhostCamMfVirtualCamera.IsInstalledDllUpToDate)
        {
            VirtualCamStatusText = "Updating Ghost Cam (UAC prompt)...";
            LogToFile("[VirtualCam] Prompting UAC registration/update for Ghost Cam...");

            bool registered = RunGhostCamRegistrationScript();
            if (!registered || !GhostCamMfVirtualCamera.IsComRegistered || !GhostCamMfVirtualCamera.IsInstalledDllUpToDate)
            {
                IsVirtualCamActive = false;
                VirtualCamStatusText = "Ghost Cam setup cancelled (requires UAC)";
                VirtualCamDevicePath = "Click START VIRTUAL CAMERA to register";
                LogToFile("[VirtualCam] Registration was cancelled or failed");
                return;
            }

            LogToFile("[VirtualCam] Ghost Cam successfully registered/updated via UAC!");
        }

        // 2. Ghost Cam Media Foundation Virtual Camera flow
        if (GhostCamMfVirtualCamera.IsComRegistered)
        {
            (int w, int h, int fps) = ParseSelectedResolutionAndFps();

            if (IsVirtualCamActive)
            {
                IsVirtualCamActive = false;
                _ghostCamMf.SetActiveStreaming(false);
                VirtualCamStatusText = "Ghost Cam ready (standby)";
                LogToFile("[VirtualCam] Ghost Cam switched to standby");
            }
            else
            {
                bool started = true;
                if (!_ghostCamMf.IsSessionRunning)
                {
                    started = _ghostCamMf.StartVirtualCamera(w, h, fps);
                }
                else
                {
                    _ghostCamMf.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
                }

                if (!started || !_ghostCamMf.IsSessionRunning)
                {
                    IsVirtualCamActive = false;
                    VirtualCamStatusText = $"Ghost Cam failed: {_ghostCamMf.LastError}";
                    LogToFile($"[VirtualCam] Failed to start Ghost Cam session: {_ghostCamMf.LastError}");
                    return;
                }

                _ghostCamMf.SetActiveStreaming(true);
                IsVirtualCamActive = true;
                VirtualCamDevicePath = "Windows 11 Media Foundation (Ghost Cam)";
                VirtualCamStatusText = $"Ghost Cam LIVE ({_ghostCamMf.ActiveWidth}x{_ghostCamMf.ActiveHeight} @ {_ghostCamMf.ActiveFps} FPS)";
                LogToFile($"[VirtualCam] Ghost Cam LIVE streaming started: {_ghostCamMf.ActiveWidth}x{_ghostCamMf.ActiveHeight} @ {_ghostCamMf.ActiveFps} FPS");
            }
            return;
        }

        // 3. Fallback to legacy camodriver.sys if present
        if (IsVirtualCamActive)
        {
            IsVirtualCamActive = false;
            VirtualCamStatusText = _virtualCameraDriver.IsDriverConnected
                ? "camodriver.sys connected (idle)"
                : "Virtual Camera stopped";
            LogToFile("[VirtualCam] Stopped streaming to virtual camera");
        }
        else
        {
            if (!_virtualCameraDriver.IsDriverConnected)
            {
                LogToFile("[VirtualCam] Attempting TryReconnect()...");
                _virtualCameraDriver.TryReconnect();
            }

            if (_virtualCameraDriver.IsDriverConnected)
            {
                (int w, int h, int fps) = ParseSelectedResolutionAndFps();
                _virtualCameraDriver.SetStreamFormat((ushort)w, (ushort)h, (ushort)fps);
                VirtualCamDevicePath = _virtualCameraDriver.ActiveDevicePath;
                VirtualCamStatusText = $"camodriver.sys connected ({_virtualCameraDriver.ActiveWidth}x{_virtualCameraDriver.ActiveHeight} @ {_virtualCameraDriver.ActiveFps} FPS)";
                IsVirtualCamActive = true;
                LogToFile($"[VirtualCam] Started streaming: {_virtualCameraDriver.ActiveWidth}x{_virtualCameraDriver.ActiveHeight} @ {_virtualCameraDriver.ActiveFps} FPS to {VirtualCamDevicePath}");
            }
            else
            {
                IsVirtualCamActive = false;
                VirtualCamStatusText = "camodriver.sys connection failed";
                LogToFile("[VirtualCam] Failed to connect to camodriver.sys");
            }
        }
    }

    private bool RunGhostCamRegistrationScript()
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = [
                Path.Combine(appDir, "scripts", "Register-GhostCam.ps1"),
                Path.Combine(appDir, "..", "scripts", "Register-GhostCam.ps1"),
                Path.Combine(appDir, "..", "..", "..", "..", "scripts", "Register-GhostCam.ps1"),
                Path.Combine(Environment.CurrentDirectory, "scripts", "Register-GhostCam.ps1"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghostcam", "runtime", "scripts", "Register-GhostCam.ps1")
            ];

            string? scriptPath = null;
            foreach (var p in searchPaths)
            {
                if (File.Exists(p))
                {
                    scriptPath = Path.GetFullPath(p);
                    break;
                }
            }

            if (scriptPath == null)
            {
                LogToFile("[VirtualCam] Register-GhostCam.ps1 not found!");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (user clicked No on UAC)
        {
            LogToFile("[VirtualCam] User cancelled UAC prompt");
            return false;
        }
        catch (Exception ex)
        {
            LogToFile($"[VirtualCam] Exception running registration script: {ex.Message}");
            return false;
        }
    }

    private void ExecuteTakeSnapshot()
    {
        try
        {
            byte[]? snapshotBuffer;
            int width;
            int height;

            lock (_renderLock)
            {
                if (_latestFrameBgra == null || _latestFrameWidth <= 0 || _latestFrameHeight <= 0) return;
                width = _latestFrameWidth;
                height = _latestFrameHeight;
                snapshotBuffer = (byte[])_latestFrameBgra.Clone();
            }

            string picturesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ghostcam");
            Directory.CreateDirectory(picturesDir);

            string filename = $"Ghostcam_Snap_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string fullPath = Path.Combine(picturesDir, filename);

            WriteableBitmap bmp = new(width, height, 96, 96, PixelFormats.Bgr32, null);
            bmp.WritePixels(new Int32Rect(0, 0, width, height), snapshotBuffer, width * 4, 0);

            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using (FileStream fs = File.OpenWrite(fullPath))
            {
                encoder.Save(fs);
            }

            SnapshotNotificationText = $"Saved snapshot: {filename}";
            ShowSnapshotNotification = true;

            Task.Delay(3000).ContinueWith(_ =>
            {
                _dispatcher.Invoke(() => ShowSnapshotNotification = false);
            });
        }
        catch (Exception ex)
        {
            SnapshotNotificationText = $"Snapshot failed: {ex.Message}";
            ShowSnapshotNotification = true;
        }
    }

    private async Task SyncSettingsToPhoneAsync()
    {
        if (!_sessionServer.IsClientConnected) return;

        int[] res = SelectedResolution.Contains("3840") ? new[] { 3840, 2160 } :
                    SelectedResolution.Contains("1280") ? new[] { 1280, 720 } : new[] { 1920, 1080 };
        int fps = SelectedFps.Contains("60") ? 60 : 30;

        var payload = new SyncSettingsPayload
        {
            HardwareSettings = new HardwareSettings
            {
                CameraId = SelectedLens,
                Iso = HardwareIso,
                ShutterSpeed = 1.0 / Math.Max(1.0, HardwareShutter),
                Temperature = HardwareWbKelvin,
                FocusDistance = HardwareFocus,
                TorchEnabled = HardwareTorch,
                Torch = HardwareTorch ? 1.0 : 0.0
            },
            VideoSettings = new VideoSettings
            {
                Resolution = res,
                DesiredFps = new CamoFpsRational { Numerator = fps, Denominator = 1 }
            }
        };

        try
        {
            await _sessionServer.SendSyncSettingsAsync(payload);
        }
        catch
        {
            // Transient network failure
        }
    }

    private void HandleClientConnected(string endpoint)
    {
        _hasDecodedRealFrame = false;
        _syntheticSource.Stop();

        _dispatcher.Invoke(() =>
        {
            if (SourceMode == CamoSourceMode.SyntheticTestCamera)
            {
                SourceMode = CamoSourceMode.WifiQrPairing;
            }

            IsPhoneConnected = true;
            ConnectedPhoneName = $"Connected: {endpoint}";
            ConnectionStatusBadge = SourceMode == CamoSourceMode.UsbMobile
                ? "🟢 USB CONNECTED"
                : "🟢 WI-FI CONNECTED";
        });
    }

    private void HandleClientDisconnected(string endpoint)
    {
        _dispatcher.Invoke(() =>
        {
            IsPhoneConnected = false;
            ConnectedPhoneName = "Phone Disconnected";
            UpdateSourcePipeline();
        });
    }

    private void HandleDeviceInfoUpdated(DeviceInfoPayload info)
    {
        _dispatcher.Invoke(() =>
        {
            string model = info.DeviceModel ?? "Mobile Device";
            ConnectedPhoneName = $"{model} ({info.OsVersion})";
            TelemetryBattery = $"🔋 {(int)(info.BatteryLevel * 100)}% • {info.BatteryState}";
            TelemetryThermal = $"🌡️ Thermal: {info.ThermalState}";
        });
    }

    private void HandleUsbDeviceConnected(UsbDeviceInfo device)
    {
        _dispatcher.Invoke(() =>
        {
            UsbStatusText = $"Connected {device.Platform}: {device.Model} ({device.Serial})";
        });
    }

    private void HandleUsbDeviceDisconnected(UsbDeviceInfo device)
    {
        _dispatcher.Invoke(() =>
        {
            UsbStatusText = $"Disconnected {device.Platform}: {device.Model}";
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(int dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);
    private delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

    private static void EnsureProcessWindowTitles()
    {
        try
        {
            using var currentProc = Process.GetCurrentProcess();
            currentProc.Refresh();
            const string expectedTitle = "Ghostcam Studio";
            foreach (ProcessThread t in currentProc.Threads)
            {
                EnumThreadWindows(t.Id, (hwnd, _) =>
                {
                    SetWindowText(hwnd, expectedTitle);
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch
        {
            // Best effort title synchronization across auxiliary IME threads
        }
    }

    public void Dispose()
    {
        _telemetryTimer?.Stop();
        _syntheticSource.Dispose();
        _virtualCameraDriver.Dispose();
        _ghostCamMf.Dispose();
        _qrPairingService.Dispose();
        _webCameraServer.Dispose();
        _ = _sessionServer.DisposeAsync();
        _ = _usbBridge.DisposeAsync();
        _nalParser.Dispose();
    }
}

file static class CamoQrPairingServiceExtensions
{
    public static void Dispose(this CamoQrPairingService service)
    {
        service.StopDiscovery();
        _ = service.DisposeAsync();
    }
}
