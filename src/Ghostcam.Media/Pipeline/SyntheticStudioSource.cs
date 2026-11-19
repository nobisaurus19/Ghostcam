using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// High-fidelity 60 FPS synthetic studio camera generator.
/// Generates studio test patterns, dynamic gradients, SMPTE color bars, real-time timecode,
/// resolution badges, and live audio waveform telemetry for zero-dependency camera testing.
/// </summary>
public sealed class SyntheticStudioSource : IDisposable
{
    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private readonly object _stateLock = new();

    private int _width = 1920;
    private int _height = 1080;
    private int _fps = 60;
    private long _frameCounter;
    private bool _disposed;

    private byte[]? _frameBuffer;

    public bool IsRunning { get; private set; }
    public int Width => _width;
    public int Height => _height;
    public int TargetFps => _fps;
    public long FramesGenerated => _frameCounter;

    public event Action<byte[], int, int>? OnFrameGenerated;

    /// <summary>
    /// Starts generating frames at the requested resolution and frame rate.
    /// </summary>
    public void Start(int width = 1920, int height = 1080, int fps = 60)
    {
        lock (_stateLock)
        {
            if (IsRunning) Stop();

            _width = width;
            _height = height;
            _fps = Math.Clamp(fps, 1, 120);
            _frameCounter = 0;

            int bufferSize = _width * _height * 4;
            _frameBuffer = new byte[bufferSize];

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _renderThread = new Thread(() => RenderLoop(token))
            {
                Name = "GhostcamSyntheticCameraWorker",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };

            IsRunning = true;
            _renderThread.Start();
        }
    }

    /// <summary>
    /// Stops the synthetic camera generator.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsRunning) return;

            IsRunning = false;
            _cts?.Cancel();

            if (_renderThread != null && _renderThread.IsAlive)
            {
                _renderThread.Join(500);
            }

            _cts?.Dispose();
            _cts = null;
            _renderThread = null;
        }
    }

    private void RenderLoop(CancellationToken token)
    {
        long intervalTicks = Stopwatch.Frequency / _fps;
        Stopwatch sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;

        while (!token.IsCancellationRequested && IsRunning)
        {
            long currentTicks = sw.ElapsedTicks;
            if (currentTicks >= nextTick)
            {
                _frameCounter++;
                GenerateSingleFrameInternal(_frameBuffer!, _width, _height, _frameCounter, sw.Elapsed.TotalSeconds);

                try
                {
                    OnFrameGenerated?.Invoke(_frameBuffer!, _width, _height);
                }
                catch
                {
                    // Prevent consumer exceptions from breaking generator loop
                }

                nextTick += intervalTicks;
                if (currentTicks > nextTick + (intervalTicks * 2))
                {
                    // Catch up if falling behind
                    nextTick = currentTicks + intervalTicks;
                }
            }
            else
            {
                long remainingTicks = nextTick - currentTicks;
                int sleepMs = (int)((remainingTicks * 1000) / Stopwatch.Frequency);
                if (sleepMs > 2)
                {
                    Thread.Sleep(sleepMs - 1);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }
        }
    }

    /// <summary>
    /// Generates a single standalone studio test frame into a newly allocated buffer.
    /// </summary>
    public byte[] GenerateSingleFrame(int width, int height, long frameIndex)
    {
        byte[] buffer = new byte[width * height * 4];
        GenerateSingleFrameInternal(buffer, width, height, frameIndex, frameIndex / 60.0);
        return buffer;
    }

    private static unsafe void GenerateSingleFrameInternal(byte[] buffer, int width, int height, long frameIndex, double timeSeconds)
    {
        fixed (byte* p = buffer)
        {
            nint pBase = (nint)p;

            // 1. Dynamic Studio Backdrop: Soft dual radial lighting sweeps
            float sweepAngle = (float)(timeSeconds * 0.8);
            float light1X = (width * 0.5f) + ((float)Math.Cos(sweepAngle) * (width * 0.35f));
            float light1Y = (height * 0.5f) + ((float)Math.Sin(sweepAngle * 0.7f) * (height * 0.25f));

            float light2X = (width * 0.5f) - ((float)Math.Cos(sweepAngle * 0.9f) * (width * 0.35f));
            float light2Y = (height * 0.5f) - ((float)Math.Sin(sweepAngle * 0.6f) * (height * 0.25f));

            float maxR = Math.Max(width, height) * 0.7f;
            float invMaxR = 1.0f / maxR;

            Parallel.For(0, height, y =>
            {
                byte* pRow = (byte*)pBase + (y * width * 4);
                float dy1 = y - light1Y;
                float dy1Sq = dy1 * dy1;
                float dy2 = y - light2Y;
                float dy2Sq = dy2 * dy2;

                for (int x = 0; x < width; x++)
                {
                    float dx1 = x - light1X;
                    float d1 = (float)Math.Sqrt((dx1 * dx1) + dy1Sq) * invMaxR;
                    float w1 = Math.Clamp(1.0f - d1, 0.0f, 1.0f);

                    float dx2 = x - light2X;
                    float d2 = (float)Math.Sqrt((dx2 * dx2) + dy2Sq) * invMaxR;
                    float w2 = Math.Clamp(1.0f - d2, 0.0f, 1.0f);

                    // Deep studio vignette background + Amber key light + Cyan fill light
                    int b = (int)(22 + (w2 * 68));
                    int g = (int)(18 + (w1 * 25) + (w2 * 45));
                    int r = (int)(20 + (w1 * 75) + (w2 * 15));

                    int idx = x * 4;
                    pRow[idx] = (byte)Math.Clamp(b, 0, 255);
                    pRow[idx + 1] = (byte)Math.Clamp(g, 0, 255);
                    pRow[idx + 2] = (byte)Math.Clamp(r, 0, 255);
                    pRow[idx + 3] = 255;
                }
            });

            // 2. SMPTE Color Bars (Top Band: 15% height)
            int barHeight = height * 15 / 100;
            int barWidth = width / 8;
            (byte B, byte G, byte R)[] smpteColors =
            [
                (200, 200, 200), // White
                (20, 200, 200),  // Yellow
                (200, 200, 20),  // Cyan
                (20, 200, 20),   // Green
                (200, 20, 200),  // Magenta
                (20, 20, 200),   // Red
                (200, 20, 20),   // Blue
                (25, 25, 25)     // Black
            ];

            for (int i = 0; i < 8; i++)
            {
                int startX = i * barWidth;
                int endX = (i == 7) ? width : startX + barWidth;
                var (b, g, r) = smpteColors[i];

                for (int y = 0; y < barHeight; y++)
                {
                    int row = y * width * 4;
                    for (int x = startX; x < endX; x++)
                    {
                        int idx = row + (x * 4);
                        p[idx] = b;
                        p[idx + 1] = g;
                        p[idx + 2] = r;
                        p[idx + 3] = 255;
                    }
                }
            }

            // 3. Center Optical Target & Reticle
            int cx = width / 2;
            int cy = height / 2;
            int[] ringRadii = [80, 160, 240, 320];

            foreach (int rad in ringRadii)
            {
                DrawCircle(p, width, height, cx, cy, rad, 120, 120, 120);
            }

            // Crosshair lines
            for (int d = -360; d <= 360; d++)
            {
                if (Math.Abs(d) > 25)
                {
                    DrawSafePixel(p, width, height, cx + d, cy, 180, 180, 180);
                    DrawSafePixel(p, width, height, cx, cy + d, 180, 180, 180);
                }
            }

            // 4. Live Audio Spectrum Bars (Bottom 12% height)
            int eqTop = height - (height * 12 / 100);
            int barCount = 32;
            int eqBarWidth = Math.Max(4, (width - (barCount * 6)) / barCount);
            int startEqX = (width - ((eqBarWidth + 6) * barCount)) / 2;

            for (int b = 0; b < barCount; b++)
            {
                double phase = (b * 0.28) + (timeSeconds * 4.5);
                double dynamicVal = (Math.Sin(phase) * 0.45) + (Math.Sin(phase * 2.3) * 0.35) + 0.2;
                float heightPct = Math.Clamp((float)Math.Abs(dynamicVal), 0.05f, 0.95f);
                int barH = (int)((height - eqTop - 12) * heightPct);

                int bx = startEqX + (b * (eqBarWidth + 6));
                int byTop = height - 10 - barH;

                for (int y = byTop; y < height - 10; y++)
                {
                    for (int x = bx; x < bx + eqBarWidth; x++)
                    {
                        // Gradient from green (bottom) to amber to red (top)
                        float prog = (float)(height - 10 - y) / (height - eqTop);
                        byte red = (byte)(prog > 0.6f ? 255 : (prog * 400));
                        byte green = (byte)(prog < 0.7f ? 230 : 50);
                        byte blue = 40;

                        DrawSafePixel(p, width, height, x, y, blue, green, red);
                    }
                }
            }

            // 5. Pulsing Live "● LIVE ON-AIR" Indicator
            int liveX = 50;
            int liveY = barHeight + 45;
            byte dotIntensity = (byte)(160 + (Math.Sin(timeSeconds * 6.0) * 90));
            DrawFilledCircle(p, width, height, liveX, liveY, 12, 20, 20, dotIntensity);
            DrawText(p, width, height, liveX + 24, liveY - 10, "LIVE ON-AIR", 3, 240, 240, 240);

            // 6. SMPTE Rolling Timecode (HH:MM:SS:FF)
            long totalSeconds = frameIndex / 60;
            long frames = frameIndex % 60;
            long hours = (totalSeconds / 3600) % 24;
            long minutes = (totalSeconds / 60) % 60;
            long seconds = totalSeconds % 60;
            string timecodeStr = $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";

            DrawText(p, width, height, cx - 180, cy - 20, timecodeStr, 4, 255, 255, 255);

            // 7. Resolution & Framerate Badges
            string badgeText = $"GHOSTCAM STUDIO - {width}X{height} @ 60 FPS";
            DrawText(p, width, height, cx - 220, cy + 40, badgeText, 2, 80, 220, 255);

            string frameNumText = $"FRAME #{frameIndex:D8}";
            DrawText(p, width, height, cx - 100, cy + 80, frameNumText, 2, 180, 180, 180);
        }
    }

    private static unsafe void DrawSafePixel(byte* p, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
        {
            int idx = (y * width + x) * 4;
            p[idx] = b;
            p[idx + 1] = g;
            p[idx + 2] = r;
            p[idx + 3] = 255;
        }
    }

    private static unsafe void DrawCircle(byte* p, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
    {
        int x = radius;
        int y = 0;
        int err = 0;

        while (x >= y)
        {
            DrawSafePixel(p, width, height, cx + x, cy + y, b, g, r);
            DrawSafePixel(p, width, height, cx + y, cy + x, b, g, r);
            DrawSafePixel(p, width, height, cx - y, cy + x, b, g, r);
            DrawSafePixel(p, width, height, cx - x, cy + y, b, g, r);
            DrawSafePixel(p, width, height, cx - x, cy - y, b, g, r);
            DrawSafePixel(p, width, height, cx - y, cy - x, b, g, r);
            DrawSafePixel(p, width, height, cx + y, cy - x, b, g, r);
            DrawSafePixel(p, width, height, cx + x, cy - y, b, g, r);

            if (err <= 0)
            {
                y += 1;
                err += (2 * y) + 1;
            }
            if (err > 0)
            {
                x -= 1;
                err -= (2 * x) + 1;
            }
        }
    }

    private static unsafe void DrawFilledCircle(byte* p, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
    {
        int rSq = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= rSq)
                {
                    DrawSafePixel(p, width, height, cx + dx, cy + dy, b, g, r);
                }
            }
        }
    }

    /// <summary>
    /// Renders text into BGRA buffer using built-in 5x7 bitmap font matrices.
    /// </summary>
    private static unsafe void DrawText(byte* p, int width, int height, int startX, int startY, string text, int scale, byte b, byte g, byte r)
    {
        int cursorX = startX;

        foreach (char ch in text)
        {
            byte[] glyph = GetGlyphMatrix(char.ToUpperInvariant(ch));

            for (int row = 0; row < 7; row++)
            {
                byte rowBits = glyph[row];
                for (int col = 0; col < 5; col++)
                {
                    if (((rowBits >> (4 - col)) & 1) == 1)
                    {
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int px = cursorX + (col * scale) + sx;
                                int py = startY + (row * scale) + sy;
                                DrawSafePixel(p, width, height, px, py, b, g, r);
                            }
                        }
                    }
                }
            }

            cursorX += (6 * scale); // 5 cols + 1 spacing col
        }
    }

    private static byte[] GetGlyphMatrix(char ch) => ch switch
    {
        '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        '2' => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        '3' => [0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E],
        '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        ':' => [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
        '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        '#' => [0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A],
        '@' => [0x0E, 0x11, 0x17, 0x15, 0x17, 0x10, 0x0F],
        ' ' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        'A' => [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'B' => [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
        'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        'D' => [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
        'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        'G' => [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
        'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'I' => [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
        'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        'M' => [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
        'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
        'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        'S' => [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
        'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        'V' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
        'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
        'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
        _ => [0x1F, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x1F]
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
