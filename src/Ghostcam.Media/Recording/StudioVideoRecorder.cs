using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ghostcam.Media.Recording;

/// <summary>
/// Records processed BGRA32 frames on a background thread into a standard RIFF MJPEG .avi video file
/// with full idx1 frame indexing and real-time duration calibration.
/// </summary>
public sealed class StudioVideoRecorder : IDisposable
{
    private const uint AviFlagHasIndex = 0x00000010;
    private const uint AviFlagIsInterleaved = 0x00000100;
    private const uint AviIndexKeyFrame = 0x00000010;

    private readonly object _stateLock = new();
    private readonly Stopwatch _recWatch = new();

    private BlockingCollection<(byte[] Bgra, int Width, int Height)>? _queue;
    private Thread? _writerThread;
    private FileStream? _stream;
    private BinaryWriter? _writer;
    private List<(uint OffsetFromMovi, uint Length)>? _indexEntries;

    private int _width;
    private int _height;
    private int _targetFps = 30;
    private long _framesQueued;
    private double _nextFrameDueMs;

    // Header byte positions patched on Stop()
    private long _riffSizePos;
    private long _avihMicroSecPos;
    private long _avihTotalFramesPos;
    private long _strhScalePos;
    private long _strhRatePos;
    private long _strhLengthPos;
    private long _moviSizePos;
    private long _moviListTypePos;

    private bool _disposed;

    public bool IsRecording { get; private set; }

    public string? CurrentFilePath { get; private set; }

    public TimeSpan RecordedDuration => IsRecording ? _recWatch.Elapsed : TimeSpan.Zero;

    /// <summary>
    /// Starts recording BGRA32 frames to the specified .avi output path.
    /// </summary>
    public bool Start(string filePath, int width, int height, int targetFps = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive.");
        }

        lock (_stateLock)
        {
            if (_disposed || IsRecording)
            {
                return false;
            }

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _width = width;
            _height = height;
            _targetFps = Math.Clamp(targetFps, 1, 120);
            _framesQueued = 0;
            _nextFrameDueMs = 0.0;
            _indexEntries = new List<(uint OffsetFromMovi, uint Length)>(1024);

            _stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536);
            _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);

            WriteAviHeaderPlaceholder(_writer, _width, _height, _targetFps);

            _queue = new BlockingCollection<(byte[] Bgra, int Width, int Height)>(boundedCapacity: 4);
            CurrentFilePath = filePath;
            _recWatch.Restart();
            IsRecording = true;

            _writerThread = new Thread(WriterLoop)
            {
                Name = "GhostcamStudioAviRecorder",
                IsBackground = true
            };
            _writerThread.Start();

            return true;
        }
    }

    /// <summary>
    /// Enqueues a BGRA32 frame for encoding and disk writing without blocking the live capture thread.
    /// </summary>
    public void PushFrame(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (!IsRecording || _disposed)
        {
            return;
        }

        if (width != _width || height != _height)
        {
            return;
        }

        int expectedBytes = width * height * 4;
        if (bgra.Length < expectedBytes)
        {
            return;
        }

        BlockingCollection<(byte[] Bgra, int Width, int Height)>? queue = _queue;
        if (queue == null || queue.IsAddingCompleted)
        {
            return;
        }

        double frameIntervalMs = 1000.0 / _targetFps;
        double nowMs = _recWatch.Elapsed.TotalMilliseconds;
        if (_framesQueued > 0 && (nowMs + (frameIntervalMs * 0.25)) < _nextFrameDueMs)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(expectedBytes);
        bgra.Slice(0, expectedBytes).CopyTo(rented);

        try
        {
            if (queue.TryAdd((rented, width, height)))
            {
                _nextFrameDueMs = (_framesQueued == 0 ? nowMs : Math.Max(nowMs - (frameIntervalMs * 0.5), _nextFrameDueMs)) + frameIntervalMs;
                _framesQueued++;
            }
            else
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Stops recording, finalizes the idx1 chunk and AVI headers, and returns the saved file path.
    /// </summary>
    public string? Stop()
    {
        BlockingCollection<(byte[] Bgra, int Width, int Height)>? queue;
        Thread? writerThread;
        FileStream? stream;
        BinaryWriter? writer;
        List<(uint OffsetFromMovi, uint Length)>? indexEntries;
        string? savedPath;
        double elapsedSeconds;

        lock (_stateLock)
        {
            if (!IsRecording)
            {
                return null;
            }

            IsRecording = false;
            _recWatch.Stop();
            elapsedSeconds = Math.Max(0.001, _recWatch.Elapsed.TotalSeconds);

            savedPath = CurrentFilePath;
            CurrentFilePath = null;

            queue = _queue;
            _queue = null;

            writerThread = _writerThread;
            _writerThread = null;

            stream = _stream;
            _stream = null;

            writer = _writer;
            _writer = null;

            indexEntries = _indexEntries;
            _indexEntries = null;
        }

        try
        {
            queue?.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        writerThread?.Join(3000);

        if (writer != null && stream != null && indexEntries != null)
        {
            try
            {
                FinalizeAviFile(writer, stream, indexEntries, elapsedSeconds);
            }
            catch
            {
                // Ensure stream disposal even if finalization encounters I/O errors.
            }
        }

        queue?.Dispose();
        writer?.Dispose();
        stream?.Dispose();

        return savedPath;
    }

    private void WriterLoop()
    {
        BlockingCollection<(byte[] Bgra, int Width, int Height)>? queue = _queue;
        BinaryWriter? writer = _writer;
        FileStream? stream = _stream;
        List<(uint OffsetFromMovi, uint Length)>? indexEntries = _indexEntries;

        if (queue == null || writer == null || stream == null || indexEntries == null)
        {
            return;
        }

        using var jpegStream = new MemoryStream(128 * 1024);

        foreach ((byte[] bgra, int w, int h) in queue.GetConsumingEnumerable())
        {
            try
            {
                jpegStream.SetLength(0);
                BitmapSource frameBitmap = BitmapSource.Create(
                    w,
                    h,
                    96.0,
                    96.0,
                    PixelFormats.Bgr32,
                    null,
                    bgra,
                    w * 4);

                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(frameBitmap));
                encoder.Save(jpegStream);

                uint jpegLen = (uint)jpegStream.Length;
                long chunkStartPos = stream.Position;
                uint offsetRelativeToMovi = (uint)(chunkStartPos - _moviListTypePos);

                WriteFourCc(writer, "00dc");
                writer.Write(jpegLen);

                byte[] jpegBuffer = jpegStream.GetBuffer();
                writer.Write(jpegBuffer, 0, (int)jpegLen);

                if ((jpegLen & 1) != 0)
                {
                    writer.Write((byte)0);
                }

                indexEntries.Add((offsetRelativeToMovi, jpegLen));
            }
            catch
            {
                // Skip malformed or interrupted frame cleanly without tearing down recorder.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bgra);
            }
        }
    }

    private void WriteAviHeaderPlaceholder(BinaryWriter writer, int width, int height, int fps)
    {
        uint microSecPerFrame = (uint)Math.Max(1, Math.Round(1_000_000.0 / fps));
        uint suggestedBufSize = (uint)(width * height * 3);

        // RIFF header
        WriteFourCc(writer, "RIFF");
        _riffSizePos = writer.BaseStream.Position;
        writer.Write(0u); // Patched on Stop()
        WriteFourCc(writer, "AVI ");

        // LIST 'hdrl'
        WriteFourCc(writer, "LIST");
        long hdrlSizePos = writer.BaseStream.Position;
        writer.Write(0u);
        long hdrlTypePos = writer.BaseStream.Position;
        WriteFourCc(writer, "hdrl");

        // 'avih' (56 bytes)
        WriteFourCc(writer, "avih");
        writer.Write(56u);
        _avihMicroSecPos = writer.BaseStream.Position;
        writer.Write(microSecPerFrame);                        // dwMicroSecPerFrame
        writer.Write((uint)(width * height * 3 * fps));        // dwMaxBytesPerSec
        writer.Write(0u);                                      // dwPaddingGranularity
        writer.Write(AviFlagHasIndex | AviFlagIsInterleaved);  // dwFlags
        _avihTotalFramesPos = writer.BaseStream.Position;
        writer.Write(0u);                                      // dwTotalFrames (patched on Stop)
        writer.Write(0u);                                      // dwInitialFrames
        writer.Write(1u);                                      // dwStreams
        writer.Write(suggestedBufSize);                        // dwSuggestedBufferSize
        writer.Write((uint)width);                             // dwWidth
        writer.Write((uint)height);                            // dwHeight
        writer.Write(0u);                                      // dwReserved[0]
        writer.Write(0u);                                      // dwReserved[1]
        writer.Write(0u);                                      // dwReserved[2]
        writer.Write(0u);                                      // dwReserved[3]

        // LIST 'strl'
        WriteFourCc(writer, "LIST");
        long strlSizePos = writer.BaseStream.Position;
        writer.Write(0u);
        long strlTypePos = writer.BaseStream.Position;
        WriteFourCc(writer, "strl");

        // 'strh' (56 bytes)
        WriteFourCc(writer, "strh");
        writer.Write(56u);
        WriteFourCc(writer, "vids");                           // fccType
        WriteFourCc(writer, "MJPG");                           // fccHandler
        writer.Write(0u);                                      // dwFlags
        writer.Write((ushort)0);                               // wPriority
        writer.Write((ushort)0);                               // wLanguage
        writer.Write(0u);                                      // dwInitialFrames
        _strhScalePos = writer.BaseStream.Position;
        writer.Write(microSecPerFrame);                        // dwScale (patched on Stop)
        _strhRatePos = writer.BaseStream.Position;
        writer.Write(1_000_000u);                              // dwRate (1,000,000 / dwScale = exact FPS)
        writer.Write(0u);                                      // dwStart
        _strhLengthPos = writer.BaseStream.Position;
        writer.Write(0u);                                      // dwLength (patched on Stop)
        writer.Write(suggestedBufSize);                        // dwSuggestedBufferSize
        writer.Write(unchecked((uint)-1));                     // dwQuality
        writer.Write(0u);                                      // dwSampleSize
        writer.Write((short)0);                                // rcFrame.left
        writer.Write((short)0);                                // rcFrame.top
        writer.Write((short)width);                            // rcFrame.right
        writer.Write((short)height);                           // rcFrame.bottom

        // 'strf' BITMAPINFOHEADER (40 bytes)
        WriteFourCc(writer, "strf");
        writer.Write(40u);
        writer.Write(40u);                                     // biSize
        writer.Write(width);                                   // biWidth
        writer.Write(height);                                  // biHeight
        writer.Write((ushort)1);                               // biPlanes
        writer.Write((ushort)24);                              // biBitCount
        WriteFourCc(writer, "MJPG");                           // biCompression
        writer.Write(suggestedBufSize);                        // biSizeImage
        writer.Write(0);                                       // biXPelsPerMeter
        writer.Write(0);                                       // biYPelsPerMeter
        writer.Write(0u);                                      // biClrUsed
        writer.Write(0u);                                      // biClrImportant

        // Patch 'strl' and 'hdrl' list sizes
        long afterHdrlPos = writer.BaseStream.Position;
        writer.BaseStream.Position = strlSizePos;
        writer.Write((uint)(afterHdrlPos - strlTypePos));
        writer.BaseStream.Position = hdrlSizePos;
        writer.Write((uint)(afterHdrlPos - hdrlTypePos));
        writer.BaseStream.Position = afterHdrlPos;

        // LIST 'movi'
        WriteFourCc(writer, "LIST");
        _moviSizePos = writer.BaseStream.Position;
        writer.Write(0u); // Patched on Stop()
        _moviListTypePos = writer.BaseStream.Position;
        WriteFourCc(writer, "movi");
    }

    private void FinalizeAviFile(
        BinaryWriter writer,
        FileStream stream,
        List<(uint OffsetFromMovi, uint Length)> indexEntries,
        double elapsedSeconds)
    {
        long moviEndPos = stream.Position;
        uint moviSize = (uint)(moviEndPos - _moviListTypePos);
        uint totalFrames = (uint)indexEntries.Count;

        // Write 'idx1' chunk
        WriteFourCc(writer, "idx1");
        writer.Write(totalFrames * 16u);
        for (int i = 0; i < indexEntries.Count; i++)
        {
            (uint offsetFromMovi, uint length) = indexEntries[i];
            WriteFourCc(writer, "00dc");
            writer.Write(AviIndexKeyFrame);
            writer.Write(offsetFromMovi);
            writer.Write(length);
        }

        long fileEndPos = stream.Position;
        uint riffSize = (uint)(fileEndPos - 8);

        uint actualMicroSecPerFrame = totalFrames > 0
            ? (uint)Math.Clamp(Math.Round((elapsedSeconds * 1_000_000.0) / totalFrames), 1000, 1_000_000)
            : (uint)Math.Max(1, Math.Round(1_000_000.0 / _targetFps));

        // Patch RIFF size
        stream.Position = _riffSizePos;
        writer.Write(riffSize);

        // Patch avih fields
        stream.Position = _avihMicroSecPos;
        writer.Write(actualMicroSecPerFrame);
        stream.Position = _avihTotalFramesPos;
        writer.Write(totalFrames);

        // Patch strh fields
        stream.Position = _strhScalePos;
        writer.Write(actualMicroSecPerFrame);
        stream.Position = _strhRatePos;
        writer.Write(1_000_000u);
        stream.Position = _strhLengthPos;
        writer.Write(totalFrames);

        // Patch movi size
        stream.Position = _moviSizePos;
        writer.Write(moviSize);

        writer.Flush();
        stream.Flush();
    }

    private static void WriteFourCc(BinaryWriter writer, string fourCc)
    {
        writer.Write((byte)fourCc[0]);
        writer.Write((byte)fourCc[1]);
        writer.Write((byte)fourCc[2]);
        writer.Write((byte)fourCc[3]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
