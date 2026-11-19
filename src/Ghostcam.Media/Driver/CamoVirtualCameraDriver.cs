using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Ghostcam.Media.Driver;

/// <summary>
/// Direct kernel interface to camodriver.sys Virtual Camera driver.
/// Communicates via IOCTL_KS_METHOD 0x002F000F (METHOD_NEITHER) and signals Global\FRAME_SYNC_EVENT_CAMO
/// for zero-latency frame injection.
/// </summary>
public sealed class CamoVirtualCameraDriver : IDisposable
{
    private static readonly Guid KSCATEGORY_CAPTURE = new("65E8773D-8F56-11D0-A3B9-00A0C9223196");
    private static readonly Guid KSCATEGORY_VIDEO_CAMERA = new("E5323777-F976-4F5B-9B55-B94699C46E44");
    private static readonly Guid MagicHeaderGuid = new("5375998C-98CB-44C2-A5D2-C0F4D1094B5E");

    private const uint IOCTL_KS_METHOD = 0x002F000F;
    private const string GlobalSyncEventName = @"Global\FRAME_SYNC_EVENT_CAMO";

    // KS Method Subfunctions
    private const uint METHOD_PUSH_FRAME = 0;
    private const uint METHOD_GET_STREAM_INFO = 1;
    private const uint METHOD_SET_STREAM_INFO = 2;
    private const uint METHOD_GET_CONNECTED_PIDS = 3;
    private const uint METHOD_GET_VERSION = 7;

    private static readonly string[] FallbackDevicePaths =
    [
        @"\\?\ROOT#CAMERA#0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\{32B66CD3-5A83-4878-B90D-7BDCA94E066B}",
        @"\\?\ROOT#CAMERA#0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\{32B66CD3-5A83-4878-B90D-7BDCA94E066B}",
        @"\\?\ROOT#CAMERA#0000#{6994ad05-93ef-11d0-a3cc-00a0c9223196}\{32B66CD3-5A83-4878-B90D-7BDCA94E066B}"
    ];

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KsMethod
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StreamInfo
    {
        public ushort Width;
        public ushort Height;
        public ushort FrameRate;
    }

    private SafeFileHandle? _driverHandle;
    private IntPtr _hSyncEvent = IntPtr.Zero;
    private bool _disposed;
    private readonly object _syncLock = new();

    private byte[]? _yuy2Buffer;
    private byte[]? _scaledBgraBuffer;
    private readonly uint[] _pidBuffer = new uint[128]; // 512 bytes for up to 128 PIDs

    public bool IsDriverConnected => _driverHandle != null && !_driverHandle.IsInvalid && !_driverHandle.IsClosed;
    public string ActiveDevicePath { get; private set; } = string.Empty;
    public ushort ActiveWidth { get; private set; }
    public ushort ActiveHeight { get; private set; }
    public ushort ActiveFps { get; private set; }

    public CamoVirtualCameraDriver()
    {
        InitializeDriver();
        InitializeSyncEvent();
    }

    /// <summary>
    /// Attempts to locate, open, and verify the Camo virtual camera kernel driver handle.
    /// </summary>
    private void InitializeDriver()
    {
        lock (_syncLock)
        {
            // 1. Try SetupAPI device interface discovery across capture/video camera classes
            string? discoveredPath = DiscoverDeviceInterfacePath();
            if (!string.IsNullOrEmpty(discoveredPath))
            {
                if (TryOpenDevice(discoveredPath))
                {
                    return;
                }
            }

            // 2. Try fallback known driver paths
            foreach (string path in FallbackDevicePaths)
            {
                if (TryOpenDevice(path))
                {
                    return;
                }
            }
        }
    }

    public bool TryReconnect()
    {
        lock (_syncLock)
        {
            if (IsDriverConnected)
            {
                if (RefreshActiveStreamInfo())
                {
                    return true;
                }

                _driverHandle?.Dispose();
                _driverHandle = null;
            }

            InitializeDriver();
            InitializeSyncEvent();
            return IsDriverConnected;
        }
    }

    private bool TryOpenDevice(string devicePath)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        // Verify driver protocol with GetVersion (expects 3) and GetStreamInfo before accepting
        unsafe
        {
            KsMethod mVer = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_GET_VERSION, Flags = 1 };
            uint version = 0;
            uint returned = 0;
            bool okVer = NativeMethods.DeviceIoControl(
                handle,
                IOCTL_KS_METHOD,
                (IntPtr)(&mVer),
                (uint)sizeof(KsMethod),
                (IntPtr)(&version),
                sizeof(uint),
                out returned,
                IntPtr.Zero);

            if (!okVer || version != 3)
            {
                handle.Dispose();
                return false;
            }

            KsMethod mInfo = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_GET_STREAM_INFO, Flags = 1 };
            StreamInfo info = default;
            bool okInfo = NativeMethods.DeviceIoControl(
                handle,
                IOCTL_KS_METHOD,
                (IntPtr)(&mInfo),
                (uint)sizeof(KsMethod),
                (IntPtr)(&info),
                (uint)sizeof(StreamInfo),
                out returned,
                IntPtr.Zero);

            if (!okInfo || returned < sizeof(StreamInfo))
            {
                handle.Dispose();
                return false;
            }

            _driverHandle = handle;
            ActiveDevicePath = devicePath;
            ActiveWidth = info.Width;
            ActiveHeight = info.Height;
            ActiveFps = info.FrameRate;
            return true;
        }
    }

    private void InitializeSyncEvent()
    {
        if (_hSyncEvent != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hSyncEvent);
            _hSyncEvent = IntPtr.Zero;
        }

        _hSyncEvent = NativeMethods.OpenEventW(NativeMethods.EVENT_MODIFY_STATE | NativeMethods.SYNCHRONIZE, false, GlobalSyncEventName);
        if (_hSyncEvent == IntPtr.Zero)
        {
            _hSyncEvent = NativeMethods.CreateEventW(IntPtr.Zero, false, false, GlobalSyncEventName);
        }
    }

    /// <summary>
    /// Configures the virtual camera's resolution and target framerate in the kernel driver,
    /// then reads back the actual resolution accepted by the driver.
    /// Note: camodriver.sys has a kernel KS_DATARANGE_VIDEO bug when Height > Width (storing Height
    /// in MinOutputSize.cx, causing DirectShow/Discord capture to output solid black).
    /// Therefore, landscape orientation is enforced (swapping width and height if height > width)
    /// and dimensions are aligned to even values.
    /// </summary>
    public unsafe bool SetStreamFormat(ushort width, ushort height, ushort fps)
    {
        if (height > width)
        {
            (width, height) = (height, width);
        }

        width &= 0xFFFE;
        height &= 0xFFFE;

        if (width == 0 || height == 0)
        {
            return false;
        }

        if (fps == 0)
        {
            fps = 30;
        }

        lock (_syncLock)
        {
            if (!IsDriverConnected) return false;

            KsMethod mSet = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_SET_STREAM_INFO, Flags = 1 };
            StreamInfo target = new StreamInfo { Width = width, Height = height, FrameRate = fps };

            bool ok = SendIoctl(ref mSet, (IntPtr)(&target), (uint)sizeof(StreamInfo), out _);
            RefreshActiveStreamInfo();
            return ok;
        }
    }

    /// <summary>
    /// Queries the driver for its currently active stream format.
    /// </summary>
    public unsafe bool RefreshActiveStreamInfo()
    {
        if (!IsDriverConnected) return false;

        KsMethod mGet = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_GET_STREAM_INFO, Flags = 1 };
        StreamInfo info = default;
        if (SendIoctl(ref mGet, (IntPtr)(&info), (uint)sizeof(StreamInfo), out uint returned) && returned >= sizeof(StreamInfo))
        {
            if (info.Width > 0 && info.Height > 0)
            {
                ActiveWidth = info.Width;
                ActiveHeight = info.Height;
                ActiveFps = info.FrameRate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a BGRA32 pixel frame to YUY2 and pushes it directly into the kernel driver,
    /// then signals FRAME_SYNC_EVENT_CAMO. Handles resolution mismatches and aspect-ratio letterboxing.
    /// </summary>
    public unsafe bool PushBgraFrame(ReadOnlySpan<byte> bgra32, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra32.IsEmpty) return false;
        if (bgra32.Length < width * height * 4) return false;

        lock (_syncLock)
        {
            if (!IsDriverConnected) return false;

            if (ActiveWidth <= 0 || ActiveHeight <= 0)
            {
                RefreshActiveStreamInfo();
            }

            int dstWidth = ActiveWidth > 0 ? ActiveWidth : width;
            int dstHeight = ActiveHeight > 0 ? ActiveHeight : height;

            ReadOnlySpan<byte> finalBgra;

            if (width == dstWidth && height == dstHeight)
            {
                finalBgra = bgra32;
            }
            else
            {
                // Source and destination resolutions differ (e.g. portrait 720x1280 into 1280x720, or Discord locked resolution).
                // Scale with aspect-ratio-preserving fit (letterbox / pillarbox black padding).
                int scaledBgraSize = dstWidth * dstHeight * 4;
                if (_scaledBgraBuffer == null || _scaledBgraBuffer.Length < scaledBgraSize)
                {
                    _scaledBgraBuffer = new byte[scaledBgraSize];
                }

                ScaleBgraAspectFit(bgra32, width, height, _scaledBgraBuffer.AsSpan(0, scaledBgraSize), dstWidth, dstHeight);
                finalBgra = _scaledBgraBuffer.AsSpan(0, scaledBgraSize);
            }

            int yuy2Size = dstWidth * dstHeight * 2;
            if (_yuy2Buffer == null || _yuy2Buffer.Length < yuy2Size)
            {
                _yuy2Buffer = new byte[yuy2Size];
            }

            ConvertBgraToYuy2(finalBgra, _yuy2Buffer.AsSpan(0, yuy2Size), dstWidth, dstHeight);
            return PushYuy2Frame(_yuy2Buffer.AsSpan(0, yuy2Size), dstWidth, dstHeight);
        }
    }

    /// <summary>
    /// Injects a YUY2 frame buffer directly into the Camo kernel driver (Method 0) and fires the sync event.
    /// </summary>
    public unsafe bool PushYuy2Frame(ReadOnlySpan<byte> yuy2, int width, int height)
    {
        if (yuy2.IsEmpty || width <= 0 || height <= 0) return false;
        int expectedSize = width * height * 2;
        if (yuy2.Length < expectedSize) return false;

        lock (_syncLock)
        {
            if (!IsDriverConnected) return false;

            KsMethod mPush = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_PUSH_FRAME, Flags = 1 };
            fixed (byte* pYuy2 = yuy2)
            {
                bool success = SendIoctl(ref mPush, (IntPtr)pYuy2, (uint)expectedSize, out _);

                // Signal synchronization event to wake up DirectShow/MediaFoundation consumers
                if (_hSyncEvent != IntPtr.Zero)
                {
                    NativeMethods.SetEvent(_hSyncEvent);
                }

                return success;
            }
        }
    }

    /// <summary>
    /// Backwards-compatibility helper for NV12 frames: converts NV12 to YUY2 and pushes to driver.
    /// </summary>
    public unsafe bool PushNv12Frame(ReadOnlySpan<byte> nv12, int width, int height)
    {
        if (nv12.IsEmpty || width <= 0 || height <= 0) return false;

        lock (_syncLock)
        {
            int bgraSize = width * height * 4;
            if (_scaledBgraBuffer == null || _scaledBgraBuffer.Length < bgraSize)
            {
                _scaledBgraBuffer = new byte[bgraSize];
            }

            ConvertNv12ToBgra(nv12, _scaledBgraBuffer.AsSpan(0, bgraSize), width, height);
            return PushBgraFrame(_scaledBgraBuffer.AsSpan(0, bgraSize), width, height);
        }
    }

    /// <summary>
    /// Retrieves process IDs of any applications (e.g. Zoom, Teams, Chrome, OBS, Discord) currently consuming the virtual camera.
    /// </summary>
    public unsafe int[] GetConnectedClientPids()
    {
        lock (_syncLock)
        {
            if (!IsDriverConnected) return Array.Empty<int>();

            KsMethod mPids = new KsMethod { Set = MagicHeaderGuid, Id = METHOD_GET_CONNECTED_PIDS, Flags = 1 };
            fixed (uint* pPids = _pidBuffer)
            {
                if (SendIoctl(ref mPids, (IntPtr)pPids, (uint)(_pidBuffer.Length * sizeof(uint)), out uint returned) && returned >= sizeof(uint))
                {
                    int count = (int)(returned / sizeof(uint));
                    if (count <= 0) return Array.Empty<int>();
                    int[] result = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        result[i] = (int)_pidBuffer[i];
                    }
                    return result;
                }
            }

            return Array.Empty<int>();
        }
    }

    private unsafe bool SendIoctl(ref KsMethod method, IntPtr pPayload, uint payloadSize, out uint bytesReturned)
    {
        bytesReturned = 0;
        if (_driverHandle == null || _driverHandle.IsInvalid || _driverHandle.IsClosed) return false;

        fixed (KsMethod* pMethod = &method)
        {
            return NativeMethods.DeviceIoControl(
                _driverHandle,
                IOCTL_KS_METHOD,
                (IntPtr)pMethod,
                (uint)sizeof(KsMethod),
                pPayload,
                payloadSize,
                out bytesReturned,
                IntPtr.Zero);
        }
    }

    /// <summary>
    /// Converts BGRA32 pixel buffer to YUY2 (4:2:2 packed) using standard BT.601 studio range arithmetic.
    /// Unrolled to process 4 pixels (2 macro-pixels = 16 bytes BGRA -> 8 bytes YUY2 packed ulong) per iteration
    /// with direct ulong*/uint* stores and compact studio-range clamping.
    /// </summary>
    public static unsafe void ConvertBgraToYuy2(ReadOnlySpan<byte> bgra, Span<byte> yuy2, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.IsEmpty || yuy2.IsEmpty) return;
        int halfWidth = width / 2;

        fixed (byte* pSrcBase = bgra)
        fixed (byte* pDstBase = yuy2)
        {
            nint srcPtr = (nint)pSrcBase;
            nint dstPtr = (nint)pDstBase;

            Parallel.For(0, height, y =>
            {
                byte* pSrcRow = (byte*)srcPtr + (y * width * 4);
                byte* pDstRow = (byte*)dstPtr + (y * width * 2);

                int col = 0;
                int unrollLimit = halfWidth - 1;

                // Process 4 pixels (2 macro-pixels = 16 bytes BGRA -> 8 bytes YUY2 packed ulong) per iteration
                for (; col < unrollLimit; col += 2)
                {
                    // Pixel 0 & 1
                    int b0 = pSrcRow[0];
                    int g0 = pSrcRow[1];
                    int r0 = pSrcRow[2];

                    int b1 = pSrcRow[4];
                    int g1 = pSrcRow[5];
                    int r1 = pSrcRow[6];

                    // Pixel 2 & 3
                    int b2 = pSrcRow[8];
                    int g2 = pSrcRow[9];
                    int r2 = pSrcRow[10];

                    int b3 = pSrcRow[12];
                    int g3 = pSrcRow[13];
                    int r3 = pSrcRow[14];

                    pSrcRow += 16;

                    // BT.601 integer arithmetic for Pixel 0 & 1
                    int y0 = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
                    int y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;

                    int rAvg0 = (r0 + r1 + 1) >> 1;
                    int gAvg0 = (g0 + g1 + 1) >> 1;
                    int bAvg0 = (b0 + b1 + 1) >> 1;

                    int u0 = ((-38 * rAvg0 - 74 * gAvg0 + 112 * bAvg0 + 128) >> 8) + 128;
                    int v0 = ((112 * rAvg0 - 94 * gAvg0 - 18 * bAvg0 + 128) >> 8) + 128;

                    // BT.601 integer arithmetic for Pixel 2 & 3
                    int y2 = ((66 * r2 + 129 * g2 + 25 * b2 + 128) >> 8) + 16;
                    int y3 = ((66 * r3 + 129 * g3 + 25 * b3 + 128) >> 8) + 16;

                    int rAvg1 = (r2 + r3 + 1) >> 1;
                    int gAvg1 = (g2 + g3 + 1) >> 1;
                    int bAvg1 = (b2 + b3 + 1) >> 1;

                    int u1 = ((-38 * rAvg1 - 74 * gAvg1 + 112 * bAvg1 + 128) >> 8) + 128;
                    int v1 = ((112 * rAvg1 - 94 * gAvg1 - 18 * bAvg1 + 128) >> 8) + 128;

                    uint cy0 = (uint)(y0 < 16 ? 16 : (y0 > 235 ? 235 : y0));
                    uint cu0 = (uint)(u0 < 16 ? 16 : (u0 > 240 ? 240 : u0));
                    uint cy1 = (uint)(y1 < 16 ? 16 : (y1 > 235 ? 235 : y1));
                    uint cv0 = (uint)(v0 < 16 ? 16 : (v0 > 240 ? 240 : v0));

                    uint cy2 = (uint)(y2 < 16 ? 16 : (y2 > 235 ? 235 : y2));
                    uint cu1 = (uint)(u1 < 16 ? 16 : (u1 > 240 ? 240 : u1));
                    uint cy3 = (uint)(y3 < 16 ? 16 : (y3 > 235 ? 235 : y3));
                    uint cv1 = (uint)(v1 < 16 ? 16 : (v1 > 240 ? 240 : v1));

                    uint packLow = cy0 | (cu0 << 8) | (cy1 << 16) | (cv0 << 24);
                    uint packHigh = cy2 | (cu1 << 8) | (cy3 << 16) | (cv1 << 24);

                    *(ulong*)pDstRow = (ulong)packLow | ((ulong)packHigh << 32);
                    pDstRow += 8;
                }

                // Remainder scalar loop for odd halfWidth
                for (; col < halfWidth; col++)
                {
                    int b0 = pSrcRow[0];
                    int g0 = pSrcRow[1];
                    int r0 = pSrcRow[2];

                    int b1 = pSrcRow[4];
                    int g1 = pSrcRow[5];
                    int r1 = pSrcRow[6];
                    pSrcRow += 8;

                    int y0 = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
                    int y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;

                    int rAvg = (r0 + r1 + 1) >> 1;
                    int gAvg = (g0 + g1 + 1) >> 1;
                    int bAvg = (b0 + b1 + 1) >> 1;

                    int u = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                    int v = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;

                    uint cy0 = (uint)(y0 < 16 ? 16 : (y0 > 235 ? 235 : y0));
                    uint cu = (uint)(u < 16 ? 16 : (u > 240 ? 240 : u));
                    uint cy1 = (uint)(y1 < 16 ? 16 : (y1 > 235 ? 235 : y1));
                    uint cv = (uint)(v < 16 ? 16 : (v > 240 ? 240 : v));

                    *(uint*)pDstRow = cy0 | (cu << 8) | (cy1 << 16) | (cv << 24);
                    pDstRow += 4;
                }
            });
        }
    }

    /// <summary>
    /// Scales BGRA32 buffer into target dimensions preserving aspect ratio with letterbox/pillarbox black bars.
    /// Eliminates per-pixel division via precomputed X-coordinate map rented from ArrayPool.
    /// </summary>
    public static unsafe void ScaleBgraAspectFit(
        ReadOnlySpan<byte> src, int srcW, int srcH,
        Span<byte> dst, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return;

        double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        int targetW = (int)Math.Round(srcW * scale);
        int targetH = (int)Math.Round(srcH * scale);

        // Ensure targetW is even for 2-pixel YUY2 alignment
        targetW = Math.Clamp((targetW / 2) * 2, 2, dstW);
        targetH = Math.Clamp(targetH, 1, dstH);

        // Only clear destination buffer if letterboxing or pillarboxing is needed
        if (targetW < dstW || targetH < dstH)
        {
            dst.Clear();
        }

        int offsetX = (dstW - targetW) / 2;
        offsetX = (offsetX / 2) * 2;
        int offsetY = (dstH - targetH) / 2;

        int[] xMap = ArrayPool<int>.Shared.Rent(targetW);
        try
        {
            for (int x = 0; x < targetW; x++)
            {
                xMap[x] = Math.Min((int)((long)x * srcW / targetW), srcW - 1);
            }

            fixed (byte* pSrcBase = src)
            fixed (byte* pDstBase = dst)
            fixed (int* pXMapBase = xMap)
            {
                nint srcPtr = (nint)pSrcBase;
                nint dstPtr = (nint)pDstBase;
                nint xMapPtr = (nint)pXMapBase;

                Parallel.For(0, targetH, y =>
                {
                    int srcY = (int)((long)y * srcH / targetH);
                    if (srcY >= srcH) srcY = srcH - 1;

                    uint* pSrcRow = (uint*)((byte*)srcPtr + (srcY * srcW * 4));
                    uint* pDstRow = (uint*)((byte*)dstPtr + ((offsetY + y) * dstW * 4)) + offsetX;
                    int* pXMap = (int*)xMapPtr;

                    for (int x = 0; x < targetW; x++)
                    {
                        pDstRow[x] = pSrcRow[pXMap[x]];
                    }
                });
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xMap);
        }
    }

    /// <summary>
    /// Converts BGRA32 pixel buffer to NV12 using BT.709 integer arithmetic (maintained for pipeline compatibility).
    /// </summary>
    public static unsafe void ConvertBgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        int ySize = width * height;
        int uvOffset = ySize;

        fixed (byte* pBgraBase = bgra)
        fixed (byte* pNv12Base = nv12)
        {
            nint bgraPtr = (nint)pBgraBase;
            nint nv12Ptr = (nint)pNv12Base;

            Parallel.For(0, height, y =>
            {
                byte* pSrcRow = (byte*)bgraPtr + (y * width * 4);
                byte* pDstYRow = (byte*)nv12Ptr + (y * width);

                for (int x = 0; x < width; x++)
                {
                    int b = pSrcRow[0];
                    int g = pSrcRow[1];
                    int r = pSrcRow[2];
                    pSrcRow += 4;

                    int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    pDstYRow[x] = (byte)(yVal < 16 ? 16 : (yVal > 235 ? 235 : yVal));
                }
            });

            int halfHeight = height / 2;
            int halfWidth = width / 2;

            Parallel.For(0, halfHeight, row =>
            {
                int y0 = row * 2;
                int y1 = y0 + 1;

                byte* pSrcRow0 = (byte*)bgraPtr + (y0 * width * 4);
                byte* pSrcRow1 = (byte*)bgraPtr + (y1 * width * 4);
                byte* pDstUvRow = (byte*)nv12Ptr + uvOffset + (row * width);

                for (int col = 0; col < halfWidth; col++)
                {
                    int x0 = col * 8;
                    int x1 = x0 + 4;

                    int b0 = pSrcRow0[x0];
                    int g0 = pSrcRow0[x0 + 1];
                    int r0 = pSrcRow0[x0 + 2];

                    int b1 = pSrcRow0[x1];
                    int g1 = pSrcRow0[x1 + 1];
                    int r1 = pSrcRow0[x1 + 2];

                    int b2 = pSrcRow1[x0];
                    int g2 = pSrcRow1[x0 + 1];
                    int r2 = pSrcRow1[x0 + 2];

                    int b3 = pSrcRow1[x1];
                    int g3 = pSrcRow1[x1 + 1];
                    int r3 = pSrcRow1[x1 + 2];

                    int rAvg = (r0 + r1 + r2 + r3 + 2) >> 2;
                    int gAvg = (g0 + g1 + g2 + g3 + 2) >> 2;
                    int bAvg = (b0 + b1 + b2 + b3 + 2) >> 2;

                    int uVal = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                    int vVal = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;

                    pDstUvRow[col * 2] = (byte)(uVal < 16 ? 16 : (uVal > 240 ? 240 : uVal));
                    pDstUvRow[col * 2 + 1] = (byte)(vVal < 16 ? 16 : (vVal > 240 ? 240 : vVal));
                }
            });
        }
    }

    private static unsafe void ConvertNv12ToBgra(ReadOnlySpan<byte> nv12, Span<byte> bgra, int width, int height)
    {
        int ySize = width * height;
        int uvOffset = ySize;

        fixed (byte* pNv12Base = nv12)
        fixed (byte* pBgraBase = bgra)
        {
            nint nv12Ptr = (nint)pNv12Base;
            nint bgraPtr = (nint)pBgraBase;

            Parallel.For(0, height, y =>
            {
                byte* pSrcY = (byte*)nv12Ptr + (y * width);
                byte* pSrcUv = (byte*)nv12Ptr + uvOffset + ((y / 2) * width);
                byte* pDstBgra = (byte*)bgraPtr + (y * width * 4);

                for (int x = 0; x < width; x++)
                {
                    int yVal = pSrcY[x] - 16;
                    int uVal = pSrcUv[(x / 2) * 2] - 128;
                    int vVal = pSrcUv[(x / 2) * 2 + 1] - 128;

                    int c = Math.Max(0, yVal);
                    int r = (298 * c + 409 * vVal + 128) >> 8;
                    int g = (298 * c - 100 * uVal - 208 * vVal + 128) >> 8;
                    int b = (298 * c + 516 * uVal + 128) >> 8;

                    pDstBgra[0] = (byte)Math.Clamp(b, 0, 255);
                    pDstBgra[1] = (byte)Math.Clamp(g, 0, 255);
                    pDstBgra[2] = (byte)Math.Clamp(r, 0, 255);
                    pDstBgra[3] = 255;
                    pDstBgra += 4;
                }
            });
        }
    }

    private static string? DiscoverDeviceInterfacePath()
    {
        Guid[] searchClasses = [KSCATEGORY_CAPTURE, KSCATEGORY_VIDEO_CAMERA];
        foreach (Guid searchGuid in searchClasses)
        {
            Guid g = searchGuid;
            IntPtr hDevInfo = NativeMethods.SetupDiGetClassDevsW(
                ref g,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.DIGCF_DEVICEINTERFACE | NativeMethods.DIGCF_PRESENT);

            if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == -1)
                continue;

            try
            {
                NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData = new();
                interfaceData.cbSize = (uint)Marshal.SizeOf(interfaceData);

                for (uint index = 0; NativeMethods.SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref g, index, ref interfaceData); index++)
                {
                    NativeMethods.SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                    if (requiredSize == 0) continue;

                    IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        int cbSize = IntPtr.Size == 8 ? 8 : 5;
                        Marshal.WriteInt32(detailBuffer, cbSize);

                        if (NativeMethods.SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref interfaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
                        {
                            IntPtr pDevicePath = IntPtr.Add(detailBuffer, 4);
                            string? devicePath = Marshal.PtrToStringUni(pDevicePath);
                            if (!string.IsNullOrEmpty(devicePath))
                            {
                                if (devicePath.Contains("32b66cd3-5a83-4878-b90d-7bdca94e066b", StringComparison.OrdinalIgnoreCase) ||
                                    devicePath.Contains("root#camera#", StringComparison.OrdinalIgnoreCase))
                                {
                                    return devicePath;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuffer);
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_syncLock)
        {
            _driverHandle?.Dispose();
            _driverHandle = null;

            if (_hSyncEvent != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_hSyncEvent);
                _hSyncEvent = IntPtr.Zero;
            }
        }
    }

    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const uint SYNCHRONIZE = 0x00100000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(
            ref Guid ClassGuid,
            IntPtr Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet,
            IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid,
            uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    }
}
