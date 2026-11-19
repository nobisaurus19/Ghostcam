using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// H.264 NAL Unit types defined in ITU-T H.264 / ISO/IEC 14496-10.
/// </summary>
public enum H264NalType
{
    Unspecified = 0,
    SliceNonIdr = 1,
    SliceDataPartitionA = 2,
    SliceDataPartitionB = 3,
    SliceDataPartitionC = 4,
    SliceIdr = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    Aud = 9,
    EndOfSequence = 10,
    EndOfStream = 11,
    Filler = 12,
    SpsExtension = 13,
    PrefixNalUnit = 14,
    SubsetSps = 15
}

/// <summary>
/// Represents a parsed NAL unit from an Annex-B stream.
/// </summary>
public sealed class NalUnit
{
    public H264NalType Type { get; set; }
    public int RefIdc { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsKeyframe => Type == H264NalType.SliceIdr;
    public bool IsParameterSet => Type == H264NalType.Sps || Type == H264NalType.Pps;
}

/// <summary>
/// Real-time stream statistics and telemetry.
/// </summary>
public sealed class StreamStatistics
{
    public double CurrentFps { get; set; }
    public double BitrateKbps { get; set; }
    public long TotalNalUnits { get; set; }
    public long KeyframeCount { get; set; }
    public bool HasSps { get; set; }
    public bool HasPps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long LastKeyframeTimestampMs { get; set; }
}

/// <summary>
/// High-performance H.264 Annex-B NAL unit stream parser and hardware/software Media Foundation MFT decoder.
/// Decodes incoming Annex-B video frames directly into full-fidelity BGRA32 buffers at 60 FPS.
/// </summary>
public sealed class H264NalParser : IDisposable
{
    private static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private const int S_OK = 0;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D6D77);

    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    private readonly List<byte> _streamBuffer = new(131072);
    private readonly Stopwatch _statsStopwatch = Stopwatch.StartNew();
    private long _bytesSinceLastStatReset = 0;
    private long _lastStatCalculationMs = 0;
    private int _framesSinceLastFpsCalc = 0;
    private long _lastFpsCalcMs = 0;

    private byte[]? _spsData;
    private byte[]? _ppsData;
    private bool _mfInitialized;
    private bool _disposed;

    private IMFTransform? _decoder;
    private readonly object _decoderLock = new();
    private bool _decoderInitialized;
    private int _decodedWidth = 1920;
    private int _decodedHeight = 1080;
    private int _decodedStride = 1920;
    private uint _outputBufferSize = 4147200;
    private byte[]? _bgraStagingBuffer;

    public StreamStatistics Stats { get; } = new();

    public event Action<NalUnit>? OnNalUnitParsed;
    public event Action<int, int>? OnResolutionChanged;
    public event Action<byte[], int, int>? OnFrameDecoded;

    public H264NalParser()
    {
        InitializeMediaFoundation();
        InitializeDecoder();
    }

    private void InitializeMediaFoundation()
    {
        try
        {
            int hr = NativeMf.MFStartup(0x00020070, 1); // MF_VERSION, MFSTARTUP_NOSOCKET
            _mfInitialized = (hr == 0);
        }
        catch
        {
            _mfInitialized = false;
        }
    }

    private void InitializeDecoder()
    {
        if (!_mfInitialized) return;

        lock (_decoderLock)
        {
            try
            {
                int hr = NativeMf.CoCreateInstance(CLSID_CMSH264DecoderMFT, IntPtr.Zero, 1, IID_IMFTransform, out object mftObj);
                if (hr != S_OK || mftObj == null) return;

                _decoder = (IMFTransform)mftObj;

                // Configure input media type (stream 0) to H.264
                hr = NativeMf.MFCreateMediaType(out IMFMediaType inputType);
                if (hr == S_OK)
                {
                    var inAttr = (IMFAttributes)inputType;
                    inAttr.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                    inAttr.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);

                    hr = _decoder.SetInputType(0, inputType, 0);
                    if (hr != S_OK)
                    {
                        if (_decoder.GetInputAvailableType(0, 0, out IMFMediaType advType) == S_OK)
                        {
                            hr = _decoder.SetInputType(0, advType, 0);
                        }
                    }
                }

                if (hr == S_OK)
                {
                    NegotiateOutputType();
                    _decoder.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
                    _decoder.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
                    _decoderInitialized = true;
                }
            }
            catch
            {
                _decoderInitialized = false;
            }
        }
    }

    private bool NegotiateOutputType()
    {
        if (_decoder == null) return false;

        for (uint i = 0; ; i++)
        {
            int hr = _decoder.GetOutputAvailableType(0, i, out IMFMediaType outType);
            if (hr != S_OK) break;

            var outAttr = (IMFAttributes)outType;
            if (outAttr.GetGUID(MF_MT_SUBTYPE, out Guid subType) == S_OK && subType == MFVideoFormat_NV12)
            {
                hr = _decoder.SetOutputType(0, outType, 0);
                if (hr == S_OK)
                {
                    // Query resolution and stride
                    if (outAttr.GetUINT64(MF_MT_FRAME_SIZE, out ulong frameSize) == S_OK && frameSize != 0)
                    {
                        int w = (int)(frameSize >> 32);
                        int h = (int)(frameSize & 0xFFFFFFFF);
                        if (w > 0 && h > 0)
                        {
                            _decodedWidth = w;
                            _decodedHeight = h;
                            Stats.Width = w;
                            Stats.Height = h;
                            OnResolutionChanged?.Invoke(w, h);
                        }
                    }

                    if (outAttr.GetUINT32(MF_MT_DEFAULT_STRIDE, out uint stride) == S_OK && stride > 0)
                    {
                        _decodedStride = (int)stride;
                    }
                    else
                    {
                        _decodedStride = _decodedWidth;
                    }

                    MFT_OUTPUT_STREAM_INFO streamInfo = default;
                    if (_decoder.GetOutputStreamInfo(0, ref streamInfo) == S_OK && streamInfo.cbSize > 0)
                    {
                        _outputBufferSize = streamInfo.cbSize;
                    }
                    else
                    {
                        _outputBufferSize = (uint)(_decodedStride * _decodedHeight * 3 / 2);
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Feeds incoming network or transport chunk bytes into the Annex-B stream parser and decodes available frames.
    /// </summary>
    public void FeedChunk(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        bool hasIdr = false;

        lock (_streamBuffer)
        {
            _bytesSinceLastStatReset += chunk.Length;
            UpdateBitrateTelemetry();

            for (int i = 0; i < chunk.Length; i++)
            {
                _streamBuffer.Add(chunk[i]);
            }

            hasIdr = ParseAccumulatedNals();
        }

        DecodeChunkToFrames(chunk, hasIdr);
    }

    private void DecodeChunkToFrames(ReadOnlySpan<byte> chunk, bool hasIdr)
    {
        if (!_decoderInitialized || _decoder == null) return;

        lock (_decoderLock)
        {
            try
            {
                byte[] packetBytes;

                // Prepend cached SPS/PPS before IDR frames if available and not already in chunk
                if (hasIdr && _spsData != null && _ppsData != null && !ChunkStartsWithSps(chunk))
                {
                    int totalLen = 4 + _spsData.Length + 4 + _ppsData.Length + chunk.Length;
                    packetBytes = new byte[totalLen];
                    int offset = 0;

                    // 00 00 00 01 [SPS]
                    packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 1;
                    Buffer.BlockCopy(_spsData, 0, packetBytes, offset, _spsData.Length);
                    offset += _spsData.Length;

                    // 00 00 00 01 [PPS]
                    packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 1;
                    Buffer.BlockCopy(_ppsData, 0, packetBytes, offset, _ppsData.Length);
                    offset += _ppsData.Length;

                    chunk.CopyTo(packetBytes.AsSpan(offset));
                }
                else
                {
                    packetBytes = chunk.ToArray();
                }

                int hr = NativeMf.MFCreateSample(out IMFSample inputSample);
                if (hr != S_OK) return;

                hr = NativeMf.MFCreateMemoryBuffer((uint)packetBytes.Length, out IMFMediaBuffer inMediaBuffer);
                if (hr != S_OK) return;

                inMediaBuffer.Lock(out IntPtr pInBuf, out _, out _);
                Marshal.Copy(packetBytes, 0, pInBuf, packetBytes.Length);
                inMediaBuffer.Unlock();
                inMediaBuffer.SetCurrentLength((uint)packetBytes.Length);

                inputSample.AddBuffer(inMediaBuffer);

                hr = _decoder.ProcessInput(0, inputSample, 0);
                if (hr == MF_E_NOTACCEPTING)
                {
                    DrainDecodedFrames();
                    hr = _decoder.ProcessInput(0, inputSample, 0);
                }

                if (hr == S_OK)
                {
                    DrainDecodedFrames();
                }
            }
            catch
            {
            }
        }
    }

    private static bool ChunkStartsWithSps(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= 5 && chunk[0] == 0 && chunk[1] == 0)
        {
            if (chunk[2] == 1 && (chunk[3] & 0x1F) == 7) return true;
            if (chunk[2] == 0 && chunk[3] == 1 && (chunk[4] & 0x1F) == 7) return true;
        }
        return false;
    }

    private void DrainDecodedFrames()
    {
        if (_decoder == null) return;

        while (true)
        {
            int hr = NativeMf.MFCreateSample(out IMFSample outputSample);
            if (hr != S_OK) break;

            uint bufSize = Math.Max(_outputBufferSize, (uint)(_decodedStride * _decodedHeight * 3 / 2));
            hr = NativeMf.MFCreateMemoryBuffer(bufSize, out IMFMediaBuffer outMediaBuffer);
            if (hr != S_OK) break;

            outputSample.AddBuffer(outMediaBuffer);

            MFT_OUTPUT_DATA_BUFFER outData = new()
            {
                dwStreamID = 0,
                pSample = Marshal.GetIUnknownForObject(outputSample),
                dwStatus = 0,
                pEvents = IntPtr.Zero
            };

            hr = _decoder.ProcessOutput(0, 1, ref outData, out _);

            if (hr == S_OK)
            {
                ExtractAndEmitFrame(outMediaBuffer);

                if (outData.pSample != IntPtr.Zero) Marshal.Release(outData.pSample);
                if (outData.pEvents != IntPtr.Zero) Marshal.Release(outData.pEvents);
            }
            else if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (outData.pSample != IntPtr.Zero) Marshal.Release(outData.pSample);
                if (outData.pEvents != IntPtr.Zero) Marshal.Release(outData.pEvents);

                NegotiateOutputType();
            }
            else
            {
                if (outData.pSample != IntPtr.Zero) Marshal.Release(outData.pSample);
                if (outData.pEvents != IntPtr.Zero) Marshal.Release(outData.pEvents);
                break;
            }
        }
    }

    private unsafe void ExtractAndEmitFrame(IMFMediaBuffer mediaBuffer)
    {
        IntPtr pData = IntPtr.Zero;
        int pitch = _decodedStride;
        bool is2D = false;

        if (mediaBuffer is IMF2DBuffer buffer2D)
        {
            if (buffer2D.Lock2D(out pData, out pitch) == S_OK)
            {
                is2D = true;
            }
        }

        if (!is2D)
        {
            if (mediaBuffer.Lock(out pData, out _, out _) != S_OK)
            {
                return;
            }
        }

        try
        {
            int width = _decodedWidth > 0 ? _decodedWidth : 1920;
            int height = _decodedHeight > 0 ? _decodedHeight : 1080;
            int bgraSize = width * height * 4;

            if (_bgraStagingBuffer == null || _bgraStagingBuffer.Length < bgraSize)
            {
                _bgraStagingBuffer = new byte[bgraSize];
            }

            ConvertNv12ToBgra32((byte*)pData, pitch, width, height, _bgraStagingBuffer);

            OnFrameDecoded?.Invoke(_bgraStagingBuffer, width, height);
        }
        finally
        {
            if (is2D)
            {
                ((IMF2DBuffer)mediaBuffer).Unlock2D();
            }
            else
            {
                mediaBuffer.Unlock();
            }
        }
    }

    /// <summary>
    /// Converts a decoded NV12 frame buffer to high-quality BGRA32 using SIMD-style parallel ITU-R BT.709 integer arithmetic.
    /// </summary>
    public static unsafe void ConvertNv12ToBgra32(byte* nv12Ptr, int stride, int width, int height, byte[] bgraOut)
    {
        if (nv12Ptr == null || width <= 0 || height <= 0 || bgraOut == null || bgraOut.Length < width * height * 4)
        {
            return;
        }

        nint nv12Address = (nint)nv12Ptr;
        int yPlaneSize = stride * height;

        fixed (byte* pBgraBase = bgraOut)
        {
            nint bgraAddress = (nint)pBgraBase;

            Parallel.For(0, height, y =>
            {
                byte* pYRow = (byte*)nv12Address + (y * stride);
                int uvY = y >> 1;
                byte* pUvRow = (byte*)nv12Address + yPlaneSize + (uvY * stride);
                byte* pDstRow = (byte*)bgraAddress + (y * width * 4);

                for (int x = 0; x < width; x++)
                {
                    int yVal = pYRow[x];
                    int uvX = (x >> 1) << 1;
                    int uVal = pUvRow[uvX];
                    int vVal = pUvRow[uvX + 1];

                    // ITU-R BT.709 limited-range integer color conversion
                    int c = yVal - 16;
                    if (c < 0) c = 0;
                    int d = uVal - 128;
                    int e = vVal - 128;

                    int r = (298 * c + 459 * e + 128) >> 8;
                    int g = (298 * c - 55 * d - 136 * e + 128) >> 8;
                    int b = (298 * c + 541 * d + 128) >> 8;

                    int dstIdx = x * 4;
                    pDstRow[dstIdx] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                    pDstRow[dstIdx + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                    pDstRow[dstIdx + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                    pDstRow[dstIdx + 3] = 255;
                }
            });
        }
    }

    private void UpdateBitrateTelemetry()
    {
        long now = _statsStopwatch.ElapsedMilliseconds;
        long elapsed = now - _lastStatCalculationMs;
        if (elapsed >= 1000)
        {
            Stats.BitrateKbps = (_bytesSinceLastStatReset * 8.0) / elapsed;
            _bytesSinceLastStatReset = 0;
            _lastStatCalculationMs = now;
        }
    }

    private void UpdateFpsTelemetry()
    {
        _framesSinceLastFpsCalc++;
        long now = _statsStopwatch.ElapsedMilliseconds;
        long elapsed = now - _lastFpsCalcMs;
        if (elapsed >= 500)
        {
            Stats.CurrentFps = (_framesSinceLastFpsCalc * 1000.0) / elapsed;
            _framesSinceLastFpsCalc = 0;
            _lastFpsCalcMs = now;
        }
    }

    /// <summary>
    /// Scans buffer for 0x000001 and 0x00000001 start codes and extracts NAL units. Returns true if an IDR slice was found.
    /// </summary>
    private bool ParseAccumulatedNals()
    {
        int searchIndex = 0;
        int firstStartCode = -1;
        int firstStartCodeLen = 0;
        bool hasIdr = false;

        while (searchIndex < _streamBuffer.Count - 3)
        {
            int startCodeLen = 0;
            if (_streamBuffer[searchIndex] == 0 && _streamBuffer[searchIndex + 1] == 0)
            {
                if (_streamBuffer[searchIndex + 2] == 1)
                {
                    startCodeLen = 3;
                }
                else if (searchIndex < _streamBuffer.Count - 3 && _streamBuffer[searchIndex + 2] == 0 && _streamBuffer[searchIndex + 3] == 1)
                {
                    startCodeLen = 4;
                }
            }

            if (startCodeLen > 0)
            {
                if (firstStartCode != -1)
                {
                    int nalStart = firstStartCode + firstStartCodeLen;
                    int nalLength = searchIndex - nalStart;

                    if (nalLength > 0)
                    {
                        byte[] nalBytes = new byte[nalLength];
                        _streamBuffer.CopyTo(nalStart, nalBytes, 0, nalLength);
                        if (ProcessNal(nalBytes))
                        {
                            hasIdr = true;
                        }
                    }

                    firstStartCode = searchIndex;
                    firstStartCodeLen = startCodeLen;
                    searchIndex += startCodeLen;
                    continue;
                }
                else
                {
                    firstStartCode = searchIndex;
                    firstStartCodeLen = startCodeLen;
                    searchIndex += startCodeLen;
                    continue;
                }
            }

            searchIndex++;
        }

        if (firstStartCode > 0)
        {
            _streamBuffer.RemoveRange(0, firstStartCode);
        }
        else if (firstStartCode == -1 && _streamBuffer.Count > 131072)
        {
            _streamBuffer.RemoveRange(0, 65536);
        }

        return hasIdr;
    }

    /// <summary>
    /// Flushes any pending unparsed NAL unit remaining in the accumulator buffer.
    /// </summary>
    public void Flush()
    {
        lock (_streamBuffer)
        {
            if (_streamBuffer.Count < 3) return;

            int startCodeLen = 0;
            int startIdx = -1;

            for (int i = 0; i <= _streamBuffer.Count - 3; i++)
            {
                if (_streamBuffer[i] == 0 && _streamBuffer[i + 1] == 0)
                {
                    if (_streamBuffer[i + 2] == 1)
                    {
                        startIdx = i;
                        startCodeLen = 3;
                        break;
                    }
                    else if (i <= _streamBuffer.Count - 4 && _streamBuffer[i + 2] == 0 && _streamBuffer[i + 3] == 1)
                    {
                        startIdx = i;
                        startCodeLen = 4;
                        break;
                    }
                }
            }

            if (startIdx >= 0)
            {
                int nalStart = startIdx + startCodeLen;
                int nalLength = _streamBuffer.Count - nalStart;
                if (nalLength > 0)
                {
                    byte[] nalBytes = new byte[nalLength];
                    _streamBuffer.CopyTo(nalStart, nalBytes, 0, nalLength);
                    ProcessNal(nalBytes);
                }
            }

            _streamBuffer.Clear();
        }

        lock (_decoderLock)
        {
            DrainDecodedFrames();
        }
    }

    private bool ProcessNal(byte[] nalBytes)
    {
        if (nalBytes.Length == 0) return false;

        byte header = nalBytes[0];
        int refIdc = (header >> 5) & 0x03;
        int nalTypeVal = header & 0x1F;
        H264NalType type = (H264NalType)nalTypeVal;

        NalUnit nal = new()
        {
            Type = type,
            RefIdc = refIdc,
            Data = nalBytes
        };

        Stats.TotalNalUnits++;

        bool isIdr = (type == H264NalType.SliceIdr);

        if (isIdr)
        {
            Stats.KeyframeCount++;
            Stats.LastKeyframeTimestampMs = _statsStopwatch.ElapsedMilliseconds;
            UpdateFpsTelemetry();
        }
        else if (type == H264NalType.SliceNonIdr)
        {
            UpdateFpsTelemetry();
        }
        else if (type == H264NalType.Sps)
        {
            Stats.HasSps = true;
            _spsData = nalBytes;
            ParseSpsResolution(nalBytes);
        }
        else if (type == H264NalType.Pps)
        {
            Stats.HasPps = true;
            _ppsData = nalBytes;
        }

        OnNalUnitParsed?.Invoke(nal);
        return isIdr;
    }

    /// <summary>
    /// Parses SPS NAL unit using Exponential-Golomb decoding to extract exact width and height.
    /// </summary>
    private void ParseSpsResolution(byte[] spsNal)
    {
        try
        {
            byte[] rbsp = RemoveEmulationPrevention(spsNal.AsSpan(1));
            BitReader reader = new(rbsp);

            int profileIdc = reader.ReadBits(8);
            reader.ReadBits(8);
            int levelIdc = reader.ReadBits(8);
            int seqParameterSetId = reader.ReadUe();

            int chromaFormatIdc = 1;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = reader.ReadUe();
                if (chromaFormatIdc == 3)
                {
                    reader.ReadBit();
                }
                reader.ReadUe();
                reader.ReadUe();
                reader.ReadBit();
                bool seqScalingMatrixPresent = reader.ReadBit();
                if (seqScalingMatrixPresent)
                {
                    int scalingListCount = chromaFormatIdc != 3 ? 8 : 12;
                    for (int i = 0; i < scalingListCount; i++)
                    {
                        bool seqScalingListPresent = reader.ReadBit();
                        if (seqScalingListPresent)
                        {
                            int sizeOfScalingList = i < 6 ? 16 : 64;
                            int lastScale = 8;
                            int nextScale = 8;
                            for (int j = 0; j < sizeOfScalingList; j++)
                            {
                                if (nextScale != 0)
                                {
                                    int deltaScale = reader.ReadSe();
                                    nextScale = (lastScale + deltaScale + 256) % 256;
                                }
                                lastScale = nextScale == 0 ? lastScale : nextScale;
                            }
                        }
                    }
                }
            }

            reader.ReadUe();
            int picOrderCntType = reader.ReadUe();
            if (picOrderCntType == 0)
            {
                reader.ReadUe();
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBit();
                reader.ReadSe();
                reader.ReadSe();
                int numRefFramesInPicOrderCntCycle = reader.ReadUe();
                for (int i = 0; i < numRefFramesInPicOrderCntCycle; i++)
                {
                    reader.ReadSe();
                }
            }

            reader.ReadUe();
            reader.ReadBit();
            int picWidthInMbsMinus1 = reader.ReadUe();
            int picHeightInMapUnitsMinus1 = reader.ReadUe();
            bool frameMbsOnlyFlag = reader.ReadBit();

            if (!frameMbsOnlyFlag)
            {
                reader.ReadBit();
            }

            reader.ReadBit();
            bool frameCroppingFlag = reader.ReadBit();

            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (frameCroppingFlag)
            {
                cropLeft = reader.ReadUe();
                cropRight = reader.ReadUe();
                cropTop = reader.ReadUe();
                cropBottom = reader.ReadUe();
            }

            int cropSubX = 2;
            int cropSubY = 2 * (frameMbsOnlyFlag ? 1 : 2);

            int width = ((picWidthInMbsMinus1 + 1) * 16) - ((cropLeft + cropRight) * cropSubX);
            int height = ((2 - (frameMbsOnlyFlag ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16) - ((cropTop + cropBottom) * cropSubY);

            if (width > 0 && height > 0)
            {
                bool changed = Stats.Width != width || Stats.Height != height;
                Stats.Width = width;
                Stats.Height = height;
                _decodedWidth = width;
                _decodedHeight = height;

                if (changed)
                {
                    OnResolutionChanged?.Invoke(width, height);
                }
            }
        }
        catch
        {
        }
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> src)
    {
        List<byte> dst = new(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i >= 2 && src[i] == 3 && src[i - 1] == 0 && src[i - 2] == 0)
            {
                continue;
            }
            dst.Add(src[i]);
        }
        return dst.ToArray();
    }

    /// <summary>
    /// Renders a diagnostic fallback frame into a BGRA buffer when awaiting stream initialization or keyframe.
    /// </summary>
    public unsafe void RenderAwaitingKeyframeFallback(byte[] targetBgra, int width, int height)
    {
        if (targetBgra.Length < width * height * 4) return;

        fixed (byte* p = targetBgra)
        {
            for (int i = 0; i < width * height; i++)
            {
                int idx = i * 4;
                p[idx] = 24;     // B
                p[idx + 1] = 20; // G
                p[idx + 2] = 22; // R
                p[idx + 3] = 255;
            }

            int cx = width / 2;
            int cy = height / 2;
            int badgeW = 400;
            int badgeH = 120;
            int left = Math.Max(0, cx - (badgeW / 2));
            int right = Math.Min(width - 1, cx + (badgeW / 2));
            int top = Math.Max(0, cy - (badgeH / 2));
            int bottom = Math.Min(height - 1, cy + (badgeH / 2));

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    int idx = (y * width + x) * 4;
                    bool isBorder = (x == left || x == right || y == top || y == bottom);
                    if (isBorder)
                    {
                        p[idx] = 255;     // Neon cyan/blue border
                        p[idx + 1] = 160;
                        p[idx + 2] = 40;
                    }
                    else
                    {
                        p[idx] = 38;
                        p[idx + 1] = 32;
                        p[idx + 2] = 34;
                    }
                }
            }

            int dotRadius = 8;
            int dotY = cy - 20;
            int dotX = cx - 140;
            byte pulseVal = (byte)(160 + (Math.Sin(_statsStopwatch.Elapsed.TotalSeconds * 5.0) * 80));

            for (int dy = -dotRadius; dy <= dotRadius; dy++)
            {
                for (int dx = -dotRadius; dx <= dotRadius; dx++)
                {
                    if ((dx * dx) + (dy * dy) <= dotRadius * dotRadius)
                    {
                        int px = dotX + dx;
                        int py = dotY + dy;
                        if (px >= 0 && px < width && py >= 0 && py < height)
                        {
                            int idx = (py * width + px) * 4;
                            p[idx] = 0;
                            p[idx + 1] = pulseVal; // Green pulse
                            p[idx + 2] = 0;
                        }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_decoderLock)
        {
            if (_decoder != null)
            {
                try
                {
                    _decoder.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
                }
                catch { }

                if (Marshal.IsComObject(_decoder))
                {
                    Marshal.ReleaseComObject(_decoder);
                }
                _decoder = null;
            }
            _decoderInitialized = false;
        }

        if (_mfInitialized)
        {
            try { NativeMf.MFShutdown(); } catch { }
            _mfInitialized = false;
        }
    }

    /// <summary>
    /// Bitstream reader helper for Exponential-Golomb decoding.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _bitOffset = 0;
        }

        public bool ReadBit()
        {
            if (_bitOffset >= _bytes.Length * 8) return false;
            int byteIndex = _bitOffset / 8;
            int bitInByte = 7 - (_bitOffset % 8);
            _bitOffset++;
            return ((_bytes[byteIndex] >> bitInByte) & 1) == 1;
        }

        public int ReadBits(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                result = (result << 1) | (ReadBit() ? 1 : 0);
            }
            return result;
        }

        public int ReadUe()
        {
            int leadingZeros = 0;
            while (!ReadBit() && leadingZeros < 32)
            {
                leadingZeros++;
            }
            if (leadingZeros == 0) return 0;
            int info = ReadBits(leadingZeros);
            return (1 << leadingZeros) - 1 + info;
        }

        public int ReadSe()
        {
            int ue = ReadUe();
            int sign = (ue & 1) == 0 ? -1 : 1;
            return sign * ((ue + 1) / 2);
        }
    }

    #region Media Foundation COM Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(IntPtr guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(IntPtr guidKey, out int pType);
        [PreserveSig] int CompareItem(IntPtr guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare(IntPtr pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(in Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] string pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(IntPtr guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(in Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(in Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    private interface IMFMediaType
    {
        // 30 IMFAttributes methods
        [PreserveSig] int GetItem(IntPtr guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(IntPtr guidKey, out int pType);
        [PreserveSig] int CompareItem(IntPtr guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare(IntPtr pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(in Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] string pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(IntPtr guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(in Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(in Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);

        // IMFMediaType methods
        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        [PreserveSig] int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pdwInputIDs, uint dwOutputIDArraySize, [Out] uint[] pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(uint dwInputStreamID, IntPtr pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, ref MFT_OUTPUT_STREAM_INFO pStreamInfo);
        [PreserveSig] int GetAttributes(out IntPtr pAttributes);
        [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, out IntPtr pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, out IntPtr pAttributes);
        [PreserveSig] int DeleteInputStream(uint dwStreamID);
        [PreserveSig] int AddInputStreams(uint cStreams, [In] uint[] adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, [In] IMFMediaType pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, [In] IMFMediaType pType, uint dwFlags);
        [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        [PreserveSig] int GetOutputStatus(out uint pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(uint eMessage, IntPtr ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, [In] IMFSample pSample, uint dwFlags);
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount, [In, Out] ref MFT_OUTPUT_DATA_BUFFER pOutputSamples, out uint pdwStatus);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")]
    private interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out IntPtr ppbScanline0, out int plPitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr pbScanline0, out int plPitch);
        [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool pfIsContiguous);
        [PreserveSig] int GetContiguousLength(out uint pcbLength);
        [PreserveSig] int ContiguousCopyTo(IntPtr pbDestBuffer, uint cbDestBuffer);
        [PreserveSig] int ContiguousCopyFrom(IntPtr pbSrcBuffer, uint cbSrcBuffer);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    private interface IMFSample
    {
        // 30 IMFAttributes methods
        [PreserveSig] int GetItem(IntPtr guidKey, IntPtr pValue);
        [PreserveSig] int GetItemType(IntPtr guidKey, out int pType);
        [PreserveSig] int CompareItem(IntPtr guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare(IntPtr pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
        [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
        [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
        [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
        [PreserveSig] int GetString(in Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] string pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob(in Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
        [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(IntPtr guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(in Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
        [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
        [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
        [PreserveSig] int SetString(in Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob(in Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown(in Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        [PreserveSig] int CopyAllItems(IMFAttributes pDest);

        // IMFSample methods
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer([In] IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer([In] IMFMediaBuffer pBuffer);
    }

    private static class NativeMf
    {
        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample ppSample);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    #endregion
}
