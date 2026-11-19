using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace Ghostcam.Media.Driver;

/// <summary>
/// High-performance Windows 11 Media Foundation Virtual Camera interface for Ghost Cam.
/// Communicates with GhostCamVirtualCamera.dll via native session exports and injects
/// zero-latency YUY2 frames into shared memory (C:\ProgramData\Ghostcam\frame.shm) with event synchronization.
/// </summary>
public sealed unsafe class GhostCamMfVirtualCamera : IDisposable
{
    private const string ClsidString = "{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}";
    private const string ShmFilePath = @"C:\ProgramData\Ghostcam\frame.shm";
    private const string ShmDirectory = @"C:\ProgramData\Ghostcam";
    private const string FrameSyncEventName = @"Global\GhostCam_FrameSync";
    private const int ShmSize = 18 * 1024 * 1024; // 18,874,368 bytes

    private const uint MagicGhst = 0x47485354; // 'GHST'
    private const uint Version1 = 1;
    private const uint FormatYuy2 = 1;
    private const uint FlagActive = 0x00000001;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShmHeader
    {
        public uint Magic;           // 0x47485354 ('GHST')
        public uint Version;         // 1
        public int FrameSeq;         // Monotonically incremented per frame
        public uint Width;           // Landscape width
        public uint Height;          // Landscape height
        public uint Fps;             // Frame rate
        public uint Format;          // 1 = YUY2
        public uint PayloadBytes;    // Width * Height * 2
        public ulong TimestampTicks; // Monotonic ticks
        public uint Flags;           // Bit 0 = Active streaming
        public fixed byte Reserved[20]; // 64 bytes total
    }

    private static class NativeMethods
    {
        private const string DllName = "GhostCamVirtualCamera.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern int GhostCam_CreateAndStartSession(string friendlyName, out IntPtr ppVirtualCamera);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern int GhostCam_StopAndReleaseSession(IntPtr pVirtualCamera);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEvent(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibrary(string lpLibFileName);

        public const uint EVENT_MODIFY_STATE = 0x0002;
    }

    private static bool _dllPreloaded;
    private static readonly object DllLoadLock = new();

    private readonly object _stateLock = new();
    private IntPtr _vcamPtr = IntPtr.Zero;
    private IntPtr _hSyncEvent = IntPtr.Zero;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _viewAccessor;
    private byte* _pView;
    private ShmHeader* _pHeader;

    private byte[]? _scaledBgraBuffer;
    private byte[]? _yuy2Buffer;
    private bool _disposed;

    public static bool IsComRegistered
    {
        get
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{ClsidString}\InprocServer32");
                if (key != null)
                {
                    var val = key.GetValue(null) as string;
                    return !string.IsNullOrWhiteSpace(val);
                }
            }
            catch
            {
                // Access check fallback
            }
            return false;
        }
    }

    public static bool IsInstalledDllUpToDate
    {
        get
        {
            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string installedDll = Path.Combine(programFiles, "Ghostcam", "GhostCamVirtualCamera.dll");
                if (!File.Exists(installedDll)) return false;

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string localDll = Path.Combine(appDir, "GhostCamVirtualCamera.dll");
                if (!File.Exists(localDll))
                {
                    localDll = Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\GhostCamVirtualCamera\bin\x64\Release\GhostCamVirtualCamera.dll"));
                }
                if (!File.Exists(localDll)) return true;

                var localInfo = new FileInfo(localDll);
                var installedInfo = new FileInfo(installedDll);

                if (localInfo.Length != installedInfo.Length)
                {
                    return false;
                }

                using var sha = SHA256.Create();
                using var s1 = File.OpenRead(localDll);
                using var s2 = File.OpenRead(installedDll);
                byte[] h1 = sha.ComputeHash(s1);
                byte[] h2 = sha.ComputeHash(s2);
                return h1.SequenceEqual(h2);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsSessionRunning => _vcamPtr != IntPtr.Zero;
    public bool IsActiveStreaming { get; private set; }
    public ushort ActiveWidth { get; private set; } = 1280;
    public ushort ActiveHeight { get; private set; } = 720;
    public ushort ActiveFps { get; private set; } = 30;
    public string LastError { get; private set; } = string.Empty;

    public GhostCamMfVirtualCamera()
    {
        EnsureDllLoaded();
    }

    private static void EnsureDllLoaded()
    {
        if (_dllPreloaded) return;
        lock (DllLoadLock)
        {
            if (_dllPreloaded) return;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localDll = Path.Combine(appDir, "GhostCamVirtualCamera.dll");
            if (File.Exists(localDll))
            {
                NativeMethods.LoadLibrary(localDll);
                _dllPreloaded = true;
                return;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installedDll = Path.Combine(programFiles, "Ghostcam", "GhostCamVirtualCamera.dll");
            if (File.Exists(installedDll))
            {
                NativeMethods.LoadLibrary(installedDll);
                _dllPreloaded = true;
                return;
            }

            // Fallback development paths
            string devDll = Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\GhostCamVirtualCamera\bin\x64\Release\GhostCamVirtualCamera.dll"));
            if (File.Exists(devDll))
            {
                NativeMethods.LoadLibrary(devDll);
                _dllPreloaded = true;
            }
        }
    }

    /// <summary>
    /// Starts the Media Foundation session for Ghost Cam and maps the shared frame memory.
    /// </summary>
    public bool StartVirtualCamera(int width = 1280, int height = 720, int fps = 30)
    {
        lock (_stateLock)
        {
            if (_disposed) return false;

            EnsureDllLoaded();

            // Set normalized landscape dimensions
            SetStreamFormat((ushort)width, (ushort)height, (ushort)fps);

            // 1. Initialize Shared Memory Mapping first
            if (!InitializeSharedMemory())
            {
                return false;
            }

            // 2. Start Virtual Camera Session
            if (_vcamPtr == IntPtr.Zero)
            {
                try
                {
                    int hr = NativeMethods.GhostCam_CreateAndStartSession("Ghost Cam", out _vcamPtr);
                    if (hr < 0)
                    {
                        LastError = $"GhostCam_CreateAndStartSession failed: 0x{hr:X8}";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LastError = $"Failed to invoke GhostCam_CreateAndStartSession: {ex.Message}";
                    return false;
                }
            }

            // 3. Connect to sync event if available
            ConnectSyncEvent();

            return true;
        }
    }

    private bool InitializeSharedMemory()
    {
        try
        {
            if (!Directory.Exists(ShmDirectory))
            {
                Directory.CreateDirectory(ShmDirectory);
            }

            if (_mmf == null)
            {
                var fs = new FileStream(
                    ShmFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.None);

                if (fs.Length < ShmSize)
                {
                    fs.SetLength(ShmSize);
                }

                _mmf = MemoryMappedFile.CreateFromFile(
                    fs,
                    mapName: null,
                    capacity: ShmSize,
                    access: MemoryMappedFileAccess.ReadWrite,
                    inheritability: HandleInheritability.None,
                    leaveOpen: false);

                _viewAccessor = _mmf.CreateViewAccessor(0, ShmSize, MemoryMappedFileAccess.ReadWrite);
                _viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pView);
                _pHeader = (ShmHeader*)_pView;
            }

            if (_pHeader != null)
            {
                if (_pHeader->Magic != MagicGhst)
                {
                    _pHeader->Magic = MagicGhst;
                    _pHeader->Version = Version1;
                    _pHeader->FrameSeq = 0;
                    _pHeader->Width = ActiveWidth;
                    _pHeader->Height = ActiveHeight;
                    _pHeader->Fps = ActiveFps;
                    _pHeader->Format = FormatYuy2;
                    _pHeader->PayloadBytes = (uint)(ActiveWidth * ActiveHeight * 2);
                    _pHeader->TimestampTicks = 0;
                    _pHeader->Flags = 0; // Standby / idle
                }
                else
                {
                    _pHeader->Width = ActiveWidth;
                    _pHeader->Height = ActiveHeight;
                    _pHeader->Fps = ActiveFps;
                    _pHeader->PayloadBytes = (uint)(ActiveWidth * ActiveHeight * 2);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"InitializeSharedMemory failed: {ex.Message}";
            return false;
        }
    }

    private void ConnectSyncEvent()
    {
        if (_hSyncEvent != IntPtr.Zero) return;
        _hSyncEvent = NativeMethods.OpenEvent(NativeMethods.EVENT_MODIFY_STATE, false, FrameSyncEventName);
    }

    /// <summary>
    /// Configures stream resolution and frame rate, enforcing landscape dimensions and even alignment.
    /// </summary>
    public void SetStreamFormat(ushort width, ushort height, ushort fps)
    {
        lock (_stateLock)
        {
            // Enforce landscape: phone portrait video (e.g. 720x1280) will be letterboxed into landscape
            if (height > width)
            {
                (width, height) = (height, width);
            }

            width = (ushort)Math.Max(2, (width / 2) * 2);
            height = (ushort)Math.Max(2, (height / 2) * 2);
            if (fps == 0) fps = 30;

            ActiveWidth = width;
            ActiveHeight = height;
            ActiveFps = fps;

            if (_pHeader != null)
            {
                _pHeader->Width = width;
                _pHeader->Height = height;
                _pHeader->Fps = fps;
                _pHeader->PayloadBytes = (uint)(width * height * 2);
            }
        }
    }

    /// <summary>
    /// Sets whether active video frames are being streamed.
    /// </summary>
    public void SetActiveStreaming(bool active)
    {
        lock (_stateLock)
        {
            IsActiveStreaming = active;
            if (_pHeader != null)
            {
                if (active)
                {
                    _pHeader->Flags |= FlagActive;
                }
                else
                {
                    _pHeader->Flags &= ~FlagActive;
                }
            }
        }
    }

    /// <summary>
    /// Converts a BGRA32 pixel frame to YUY2, letterboxes into the active landscape target format,
    /// writes directly into the shared memory payload at offset 64, and signals FrameSync.
    /// </summary>
    public bool PushBgraFrame(ReadOnlySpan<byte> bgra32, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra32.IsEmpty || _disposed) return false;
        if (bgra32.Length < width * height * 4) return false;

        lock (_stateLock)
        {
            if (_pView == null || _pHeader == null) return false;

            int dstW = ActiveWidth > 0 ? ActiveWidth : 1280;
            int dstH = ActiveHeight > 0 ? ActiveHeight : 720;

            ReadOnlySpan<byte> finalBgra;
            if (width == dstW && height == dstH)
            {
                finalBgra = bgra32;
            }
            else
            {
                int scaledBgraSize = dstW * dstH * 4;
                if (_scaledBgraBuffer == null || _scaledBgraBuffer.Length < scaledBgraSize)
                {
                    _scaledBgraBuffer = new byte[scaledBgraSize];
                }

                CamoVirtualCameraDriver.ScaleBgraAspectFit(
                    bgra32, width, height,
                    _scaledBgraBuffer.AsSpan(0, scaledBgraSize), dstW, dstH);

                finalBgra = _scaledBgraBuffer.AsSpan(0, scaledBgraSize);
            }

            int yuy2Size = dstW * dstH * 2;
            if ((sizeof(ShmHeader) + yuy2Size) > ShmSize)
            {
                return false;
            }

            if (_yuy2Buffer == null || _yuy2Buffer.Length < yuy2Size)
            {
                _yuy2Buffer = new byte[yuy2Size];
            }

            CamoVirtualCameraDriver.ConvertBgraToYuy2(finalBgra, _yuy2Buffer.AsSpan(0, yuy2Size), dstW, dstH);

            // Begin Seqlock write (ensure odd FrameSeq)
            int curSeq = Volatile.Read(ref _pHeader->FrameSeq);
            int oddSeq = (curSeq & 1) == 0 ? (curSeq + 1) : (curSeq + 2);
            Interlocked.Exchange(ref _pHeader->FrameSeq, oddSeq);
            Thread.MemoryBarrier();

            // Copy to SHM starting at offset 64
            byte* pDstPayload = _pView + sizeof(ShmHeader);
            fixed (byte* pYuy2Src = _yuy2Buffer)
            {
                Buffer.MemoryCopy(pYuy2Src, pDstPayload, ShmSize - sizeof(ShmHeader), yuy2Size);
            }

            // Update SHM Header fields
            _pHeader->Width = (uint)dstW;
            _pHeader->Height = (uint)dstH;
            _pHeader->Fps = ActiveFps;
            _pHeader->PayloadBytes = (uint)yuy2Size;
            _pHeader->TimestampTicks = (ulong)Stopwatch.GetTimestamp();
            _pHeader->Flags |= FlagActive;
            Thread.MemoryBarrier();

            // End Seqlock write (even FrameSeq = oddSeq + 1)
            Interlocked.Exchange(ref _pHeader->FrameSeq, oddSeq + 1);

            // Fire synchronization event
            ConnectSyncEvent();
            if (_hSyncEvent != IntPtr.Zero)
            {
                NativeMethods.SetEvent(_hSyncEvent);
            }

            return true;
        }
    }

    /// <summary>
    /// Stops the active virtual camera session and closes shared memory resources.
    /// </summary>
    public void StopVirtualCamera()
    {
        lock (_stateLock)
        {
            if (_pHeader != null)
            {
                _pHeader->Flags &= ~FlagActive;
            }

            if (_hSyncEvent != IntPtr.Zero)
            {
                NativeMethods.SetEvent(_hSyncEvent);
                NativeMethods.CloseHandle(_hSyncEvent);
                _hSyncEvent = IntPtr.Zero;
            }

            if (_vcamPtr != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.GhostCam_StopAndReleaseSession(_vcamPtr);
                }
                catch
                {
                    // Ignore unload errors
                }
                _vcamPtr = IntPtr.Zero;
            }

            if (_pView != null && _viewAccessor != null)
            {
                try
                {
                    _viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                catch
                {
                }
                _pView = null;
                _pHeader = null;
            }

            if (_viewAccessor != null)
            {
                _viewAccessor.Dispose();
                _viewAccessor = null;
            }

            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }

            IsActiveStreaming = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopVirtualCamera();
    }
}
