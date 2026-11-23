using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// H.265 / HEVC NAL unit types defined in ITU-T H.265 / ISO/IEC 23008-2.
/// </summary>
public enum H265NalType
{
    TrailN = 0,
    TrailR = 1,
    TsaN = 2,
    TsaR = 3,
    StsaN = 4,
    StsaR = 5,
    RadlN = 6,
    RadlR = 7,
    RaslN = 8,
    RaslR = 9,
    BlaWLp = 16,
    BlaWRadl = 17,
    BlaNLp = 18,
    IdrWRadl = 19,
    IdrNLp = 20,
    CraNut = 21,
    Vps = 32,
    Sps = 33,
    Pps = 34,
    Aud = 35,
    EndOfSequence = 36,
    EndOfBitStream = 37,
    FillerData = 38,
    PrefixSei = 39,
    SuffixSei = 40
}

/// <summary>
/// High-performance H.265 (HEVC) Annex-B NAL unit stream parser and hardware/software Media Foundation MFT decoder.
/// Decodes incoming Annex-B video frames directly into full-fidelity BGRA32 buffers.
/// </summary>
public sealed class H265NalParser : IDisposable
{
    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H265 = new("35363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private const int S_OK = 0;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D6D77);

    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    private const uint MFT_ENUM_FLAG_LOCALMFT = 0x00000010;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const uint MFT_ENUM_FLAG_SYNC_LOCAL_SORTED = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    private const uint MFT_ENUM_FLAG_ALL = 0x0000007F;

    private readonly List<byte> _streamBuffer = new(131072);
    private readonly Stopwatch _statsStopwatch = Stopwatch.StartNew();
    private long _bytesSinceLastStatReset = 0;
    private long _lastStatCalculationMs = 0;
    private int _framesSinceLastFpsCalc = 0;
    private long _lastFpsCalcMs = 0;

    private byte[]? _vpsData;
    private byte[]? _spsData;
    private byte[]? _ppsData;
    private bool _mfInitialized;
    private bool _disposed;

    private IMFTransform? _decoder;
    private readonly object _decoderLock = new();
    private bool _decoderInitialized;
    private int _codedWidth = 1920;
    private int _codedHeight = 1080;
    private int _decodedWidth = 1920;
    private int _decodedHeight = 1080;
    private int _decodedStride = 1920;
    private uint _outputBufferSize = 4147200;
    private bool _outputStreamProvidesSamples;
    private byte[]? _bgraStagingBuffer;

    public bool IsAvailable => _decoderInitialized;

    public StreamStatistics Stats { get; } = new();

    public event Action<int, int>? OnResolutionChanged;
    public event Action<byte[], int, int>? OnFrameDecoded;

    public H265NalParser()
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
                if (TryActivateHevcDecoder(MFVideoFormat_HEVC, MFT_ENUM_FLAG_SYNC_LOCAL_SORTED)) return;
                if (TryActivateHevcDecoder(MFVideoFormat_H265, MFT_ENUM_FLAG_SYNC_LOCAL_SORTED)) return;
                if (TryActivateHevcDecoder(MFVideoFormat_HEVC, MFT_ENUM_FLAG_ALL)) return;
                TryActivateHevcDecoder(MFVideoFormat_H265, MFT_ENUM_FLAG_ALL);
            }
            catch
            {
                _decoderInitialized = false;
            }
        }
    }

    private unsafe bool TryActivateHevcDecoder(Guid inputSubtype, uint enumFlags)
    {
        MFT_REGISTER_TYPE_INFO inType = new()
        {
            guidMajorType = MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        MFT_REGISTER_TYPE_INFO outType = new()
        {
            guidMajorType = MFMediaType_Video,
            guidSubtype = MFVideoFormat_NV12
        };

        IntPtr ppActivate = IntPtr.Zero;
        uint count = 0;

        int hr = NativeMf.MFTEnumEx(
            MFT_CATEGORY_VIDEO_DECODER,
            enumFlags,
            &inType,
            &outType,
            out ppActivate,
            out count);

        if (hr != S_OK || count == 0 || ppActivate == IntPtr.Zero)
        {
            if (ppActivate != IntPtr.Zero)
            {
                ReleaseActivationArray(ppActivate, count);
            }
            return false;
        }

        try
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr pUnk = Marshal.ReadIntPtr(ppActivate, (int)(i * IntPtr.Size));
                if (pUnk == IntPtr.Zero) continue;

                IMFActivate? activate = null;
                try
                {
                    activate = Marshal.GetObjectForIUnknown(pUnk) as IMFActivate;
                    if (activate == null) continue;

                    if (activate.ActivateObject(IID_IMFTransform, out object mftObj) != S_OK || mftObj == null)
                    {
                        continue;
                    }

                    var candidate = (IMFTransform)mftObj;
                    if (ConfigureDecoderCandidate(candidate, inputSubtype))
                    {
                        _decoder = candidate;
                        _decoderInitialized = true;
                        return true;
                    }

                    if (Marshal.IsComObject(candidate))
                    {
                        Marshal.FinalReleaseComObject(candidate);
                    }
                    try
                    {
                        activate.ShutdownObject();
                    }
                    catch
                    {
                    }
                }
                catch
                {
                    if (_decoder != null && Marshal.IsComObject(_decoder))
                    {
                        Marshal.FinalReleaseComObject(_decoder);
                    }
                    _decoder = null;
                    _decoderInitialized = false;
                }
                finally
                {
                    if (activate != null && Marshal.IsComObject(activate))
                    {
                        Marshal.FinalReleaseComObject(activate);
                    }
                }
            }
        }
        finally
        {
            ReleaseActivationArray(ppActivate, count);
        }

        return false;
    }

    private static void ReleaseActivationArray(IntPtr ppActivate, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            IntPtr pUnk = Marshal.ReadIntPtr(ppActivate, (int)(i * IntPtr.Size));
            if (pUnk != IntPtr.Zero)
            {
                Marshal.Release(pUnk);
            }
        }
        Marshal.FreeCoTaskMem(ppActivate);
    }

    private bool ConfigureDecoderCandidate(IMFTransform candidate, Guid inputSubtype)
    {
        _decoder = candidate;
        try
        {
            UnlockAsyncMftIfNeeded(candidate);

            IMFMediaType? inputType = null;
            IMFMediaType? advType = null;
            int hr;
            try
            {
                hr = NativeMf.MFCreateMediaType(out inputType);
                if (hr != S_OK || inputType == null) return false;

                var inAttr = (IMFAttributes)inputType;
                inAttr.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                inAttr.SetGUID(MF_MT_SUBTYPE, inputSubtype);
                inAttr.SetUINT64(MF_MT_FRAME_SIZE, ((ulong)1920 << 32) | 1080UL);

                hr = candidate.SetInputType(0, inputType, 0);
                if (hr != S_OK)
                {
                    if (candidate.GetInputAvailableType(0, 0, out advType) == S_OK && advType != null)
                    {
                        hr = candidate.SetInputType(0, advType, 0);
                    }
                }
            }
            finally
            {
                if (advType != null && Marshal.IsComObject(advType))
                {
                    Marshal.FinalReleaseComObject(advType);
                }
                if (inputType != null && Marshal.IsComObject(inputType))
                {
                    Marshal.FinalReleaseComObject(inputType);
                }
            }

            if (hr != S_OK) return false;

            if (!NegotiateOutputType()) return false;

            candidate.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            candidate.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
            return true;
        }
        catch
        {
            _decoder = null;
            return false;
        }
    }

    private static void UnlockAsyncMftIfNeeded(IMFTransform mft)
    {
        if (mft.GetAttributes(out IntPtr pAttrs) != S_OK || pAttrs == IntPtr.Zero)
        {
            return;
        }

        IMFAttributes? mftAttrs = null;
        try
        {
            mftAttrs = Marshal.GetObjectForIUnknown(pAttrs) as IMFAttributes;
            if (mftAttrs != null &&
                mftAttrs.GetUINT32(MF_TRANSFORM_ASYNC, out uint isAsync) == S_OK &&
                isAsync != 0)
            {
                mftAttrs.SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, 1);
            }
        }
        finally
        {
            if (mftAttrs != null && Marshal.IsComObject(mftAttrs))
            {
                Marshal.FinalReleaseComObject(mftAttrs);
            }
            Marshal.Release(pAttrs);
        }
    }

    private bool NegotiateOutputType()
    {
        if (_decoder == null) return false;

        for (uint i = 0; ; i++)
        {
            int hr = _decoder.GetOutputAvailableType(0, i, out IMFMediaType outType);
            if (hr != S_OK) break;

            try
            {
                var outAttr = (IMFAttributes)outType;
                if (outAttr.GetGUID(MF_MT_SUBTYPE, out Guid subType) == S_OK && subType == MFVideoFormat_NV12)
                {
                    hr = _decoder.SetOutputType(0, outType, 0);
                    if (hr == S_OK)
                    {
                        if (outAttr.GetUINT64(MF_MT_FRAME_SIZE, out ulong frameSize) == S_OK && frameSize != 0)
                        {
                            int w = (int)(frameSize >> 32);
                            int h = (int)(frameSize & 0xFFFFFFFF);
                            if (w > 0 && h > 0)
                            {
                                _codedWidth = w;
                                _codedHeight = h;

                                int visW = 0;
                                int visH = 0;
                                if (outAttr.GetBlobSize(MF_MT_MINIMUM_DISPLAY_APERTURE, out uint apSize) == S_OK && apSize >= 16 && apSize <= 1024)
                                {
                                    byte[] apBuf = new byte[apSize];
                                    unsafe
                                    {
                                        fixed (byte* pAp = apBuf)
                                        {
                                            if (outAttr.GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (IntPtr)pAp, apSize, out _) == S_OK)
                                            {
                                                int cx = BitConverter.ToInt32(apBuf, 8);
                                                int cy = BitConverter.ToInt32(apBuf, 12);
                                                if (cx > 0 && cy > 0 && cx <= _codedWidth && cy <= _codedHeight)
                                                {
                                                    visW = cx;
                                                    visH = cy;
                                                }
                                            }
                                        }
                                    }
                                }

                                if (visW <= 0 || visH <= 0)
                                {
                                    if (Stats.Width > 0 && Stats.Height > 0 && Stats.Width <= _codedWidth && Stats.Height <= _codedHeight)
                                    {
                                        visW = Stats.Width;
                                        visH = Stats.Height;
                                    }
                                    else
                                    {
                                        visW = _codedWidth;
                                        visH = _codedHeight;
                                    }
                                }

                                _decodedWidth = visW;
                                _decodedHeight = visH;
                                Stats.Width = visW;
                                Stats.Height = visH;
                                OnResolutionChanged?.Invoke(visW, visH);
                            }
                        }

                        if (outAttr.GetUINT32(MF_MT_DEFAULT_STRIDE, out uint stride) == S_OK && stride > 0)
                        {
                            _decodedStride = (int)stride;
                        }
                        else
                        {
                            _decodedStride = _codedWidth;
                        }

                        MFT_OUTPUT_STREAM_INFO streamInfo = default;
                        if (_decoder.GetOutputStreamInfo(0, ref streamInfo) == S_OK)
                        {
                            _outputStreamProvidesSamples = (streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                            _outputBufferSize = Math.Max(streamInfo.cbSize, (uint)(_decodedStride * _codedHeight * 3 / 2));
                        }
                        else
                        {
                            _outputStreamProvidesSamples = false;
                            _outputBufferSize = (uint)(_decodedStride * _codedHeight * 3 / 2);
                        }

                        return true;
                    }
                }
            }
            finally
            {
                if (outType != null && Marshal.IsComObject(outType))
                {
                    Marshal.FinalReleaseComObject(outType);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Feeds incoming network or transport chunk bytes into the HEVC Annex-B stream parser and decodes available frames.
    /// </summary>
    public void FeedChunk(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        bool hasKeyframe = false;

        lock (_streamBuffer)
        {
            _bytesSinceLastStatReset += chunk.Length;
            UpdateBitrateTelemetry();

            for (int i = 0; i < chunk.Length; i++)
            {
                _streamBuffer.Add(chunk[i]);
            }

            hasKeyframe = ParseAccumulatedNals();
        }

        DecodeChunkToFrames(chunk, hasKeyframe);
    }

    private void DecodeChunkToFrames(ReadOnlySpan<byte> chunk, bool hasKeyframe)
    {
        if (!_decoderInitialized || _decoder == null) return;

        lock (_decoderLock)
        {
            try
            {
                byte[] packetBytes;

                // Prepend cached VPS/SPS/PPS before IDR/CRA keyframes if available and not already in chunk
                if (hasKeyframe && _vpsData != null && _spsData != null && _ppsData != null && !ChunkStartsWithVps(chunk))
                {
                    int totalLen = 4 + _vpsData.Length + 4 + _spsData.Length + 4 + _ppsData.Length + chunk.Length;
                    packetBytes = new byte[totalLen];
                    int offset = 0;

                    // 00 00 00 01 [VPS]
                    packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 0; packetBytes[offset++] = 1;
                    Buffer.BlockCopy(_vpsData, 0, packetBytes, offset, _vpsData.Length);
                    offset += _vpsData.Length;

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

                IMFSample? inputSample = null;
                IMFMediaBuffer? inMediaBuffer = null;
                try
                {
                    int hr = NativeMf.MFCreateSample(out inputSample);
                    if (hr != S_OK || inputSample == null) return;

                    hr = NativeMf.MFCreateMemoryBuffer((uint)packetBytes.Length, out inMediaBuffer);
                    if (hr != S_OK || inMediaBuffer == null) return;

                    hr = inMediaBuffer.Lock(out IntPtr pInBuf, out _, out _);
                    if (hr != S_OK) return;
                    try
                    {
                        Marshal.Copy(packetBytes, 0, pInBuf, packetBytes.Length);
                    }
                    finally
                    {
                        inMediaBuffer.Unlock();
                    }
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
                finally
                {
                    if (inMediaBuffer != null && Marshal.IsComObject(inMediaBuffer))
                    {
                        Marshal.FinalReleaseComObject(inMediaBuffer);
                    }
                    if (inputSample != null && Marshal.IsComObject(inputSample))
                    {
                        Marshal.FinalReleaseComObject(inputSample);
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static bool ChunkStartsWithVps(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= 5 && chunk[0] == 0 && chunk[1] == 0)
        {
            if (chunk[2] == 1 && ((chunk[3] >> 1) & 0x3F) == 32) return true;
            if (chunk[2] == 0 && chunk[3] == 1 && ((chunk[4] >> 1) & 0x3F) == 32) return true;
        }
        return false;
    }

    private void DrainDecodedFrames()
    {
        if (_decoder == null) return;

        while (true)
        {
            IMFSample? outputSample = null;
            IMFMediaBuffer? outMediaBuffer = null;
            IMFSample? providedSample = null;
            IMFMediaBuffer? providedBuffer = null;

            MFT_OUTPUT_DATA_BUFFER outData = new()
            {
                dwStreamID = 0,
                pSample = IntPtr.Zero,
                dwStatus = 0,
                pEvents = IntPtr.Zero
            };

            try
            {
                if (!_outputStreamProvidesSamples)
                {
                    int hrCreate = NativeMf.MFCreateSample(out outputSample);
                    if (hrCreate != S_OK || outputSample == null) break;

                    uint bufSize = Math.Max(_outputBufferSize, (uint)(_decodedStride * _codedHeight * 3 / 2));
                    hrCreate = NativeMf.MFCreateMemoryBuffer(bufSize, out outMediaBuffer);
                    if (hrCreate != S_OK || outMediaBuffer == null) break;

                    outputSample.AddBuffer(outMediaBuffer);
                    outData.pSample = Marshal.GetIUnknownForObject(outputSample);
                }

                int hr = _decoder.ProcessOutput(0, 1, ref outData, out _);

                if (hr == S_OK)
                {
                    if (_outputStreamProvidesSamples)
                    {
                        if (outData.pSample != IntPtr.Zero)
                        {
                            providedSample = Marshal.GetObjectForIUnknown(outData.pSample) as IMFSample;
                            if (providedSample != null &&
                                providedSample.GetBufferByIndex(0, out providedBuffer) == S_OK &&
                                providedBuffer != null)
                            {
                                ExtractAndEmitFrame(providedBuffer);
                            }
                        }
                    }
                    else if (outMediaBuffer != null)
                    {
                        ExtractAndEmitFrame(outMediaBuffer);
                    }
                }
                else if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    if (!NegotiateOutputType()) break;
                }
                else
                {
                    break;
                }
            }
            finally
            {
                if (providedBuffer != null && Marshal.IsComObject(providedBuffer))
                {
                    Marshal.FinalReleaseComObject(providedBuffer);
                }
                if (providedSample != null && Marshal.IsComObject(providedSample))
                {
                    Marshal.FinalReleaseComObject(providedSample);
                }
                if (outData.pSample != IntPtr.Zero)
                {
                    Marshal.Release(outData.pSample);
                }
                if (outData.pEvents != IntPtr.Zero)
                {
                    Marshal.Release(outData.pEvents);
                }
                if (outMediaBuffer != null && Marshal.IsComObject(outMediaBuffer))
                {
                    Marshal.FinalReleaseComObject(outMediaBuffer);
                }
                if (outputSample != null && Marshal.IsComObject(outputSample))
                {
                    Marshal.FinalReleaseComObject(outputSample);
                }
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
            if (_codedWidth > 0 && width > _codedWidth) width = _codedWidth;
            if (_codedHeight > 0 && height > _codedHeight) height = _codedHeight;
            int bgraSize = width * height * 4;

            if (_bgraStagingBuffer == null || _bgraStagingBuffer.Length < bgraSize)
            {
                _bgraStagingBuffer = new byte[bgraSize];
            }

            H264NalParser.ConvertNv12ToBgra32((byte*)pData, pitch, width, height, _bgraStagingBuffer, _codedHeight);

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
    /// Scans buffer for 0x000001 and 0x00000001 start codes and extracts NAL units. Returns true if an IDR or CRA keyframe slice was found.
    /// </summary>
    private bool ParseAccumulatedNals()
    {
        int searchIndex = 0;
        int firstStartCode = -1;
        int firstStartCodeLen = 0;
        bool hasKeyframe = false;

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
                            hasKeyframe = true;
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

        return hasKeyframe;
    }

    /// <summary>
    /// Flushes any pending unparsed NAL unit remaining in the accumulator buffer and drains decoded frames.
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

        int nalType = (nalBytes[0] >> 1) & 0x3F;
        Stats.TotalNalUnits++;

        bool isKeyframe = nalType is 19 or 20 or 21;

        if (isKeyframe)
        {
            Stats.KeyframeCount++;
            Stats.LastKeyframeTimestampMs = _statsStopwatch.ElapsedMilliseconds;
            UpdateFpsTelemetry();
        }
        else if (nalType < 32)
        {
            UpdateFpsTelemetry();
        }
        else if (nalType == 32)
        {
            _vpsData = nalBytes;
        }
        else if (nalType == 33)
        {
            Stats.HasSps = true;
            _spsData = nalBytes;
        }
        else if (nalType == 34)
        {
            Stats.HasPps = true;
            _ppsData = nalBytes;
        }

        return isKeyframe;
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
                    Marshal.FinalReleaseComObject(_decoder);
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

    #region Media Foundation COM Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

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
    [Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67")]
    private interface IMFActivate
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

        // IMFActivate methods
        [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
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

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern unsafe int MFTEnumEx(
            Guid guidCategory,
            uint Flags,
            MFT_REGISTER_TYPE_INFO* pInputType,
            MFT_REGISTER_TYPE_INFO* pOutputType,
            out IntPtr pppMFTActivate,
            out uint pcMFTActivate);
    }

    #endregion
}
