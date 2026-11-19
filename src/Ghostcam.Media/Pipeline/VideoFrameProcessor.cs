using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// Ultra-fast 60 FPS SIMD-friendly real-time frame processing engine.
/// Handles zoom, pan, rotation, mirror, color grading, sharpening, vignette, and HUD overlays.
/// </summary>
public sealed class VideoFrameProcessor
{
    private byte[]? _scratchBufferA;
    private byte[]? _scratchBufferB;
    private readonly object _bufferLock = new();
    private float _autoPanX;
    private float _autoPanY;
    private float _autoZoom = 1.0f;

    /// <summary>
    /// Processes a BGRA32 frame in-place (or into the provided buffer) using settings.
    /// Returns the buffer containing the processed frame.
    /// </summary>
    public unsafe byte[] ProcessFrame(byte[] sourceBgra, int width, int height, StudioFilterSettings settings)
    {
        if (sourceBgra == null || width <= 0 || height <= 0)
        {
            throw new ArgumentException("Invalid frame buffer or dimensions");
        }

        int bufferSize = width * height * 4;
        EnsureBuffers(bufferSize);

        byte[] current = sourceBgra;
        byte[] next = _scratchBufferA!;

        // 1. Auto Face Framing + Zoom and Pan
        if (settings.AutoFramingEnabled)
        {
            ComputeAutoFramingTarget(current, width, height, out float targetPanX, out float targetPanY);
            _autoPanX += (targetPanX - _autoPanX) * 0.12f;
            _autoPanY += (targetPanY - _autoPanY) * 0.12f;
            _autoZoom += (1.28f - _autoZoom) * 0.10f;
        }
        else if (_autoZoom > 1.001f || Math.Abs(_autoPanX) > 0.001f || Math.Abs(_autoPanY) > 0.001f)
        {
            _autoPanX += (0.0f - _autoPanX) * 0.18f;
            _autoPanY += (0.0f - _autoPanY) * 0.18f;
            _autoZoom += (1.0f - _autoZoom) * 0.18f;
            if (Math.Abs(_autoPanX) < 0.001f) _autoPanX = 0.0f;
            if (Math.Abs(_autoPanY) < 0.001f) _autoPanY = 0.0f;
            if (Math.Abs(_autoZoom - 1.0f) < 0.001f) _autoZoom = 1.0f;
        }

        float effectiveZoom = Math.Clamp(settings.Zoom * _autoZoom, 1.0f, 4.0f);
        float effectivePanX = Math.Clamp(settings.PanX + _autoPanX, -1.0f, 1.0f);
        float effectivePanY = Math.Clamp(settings.PanY + _autoPanY, -1.0f, 1.0f);

        if (effectiveZoom > 1.001f || Math.Abs(effectivePanX) > 0.001f || Math.Abs(effectivePanY) > 0.001f)
        {
            ApplyZoomAndPan(current, next, width, height, effectiveZoom, effectivePanX, effectivePanY);
            Swap(ref current, ref next);
        }

        // 2. Mirror and Rotation
        if (settings.MirrorHorizontal || settings.RotationDegrees != 0)
        {
            ApplyGeometricTransform(current, next, width, height, settings.MirrorHorizontal, settings.RotationDegrees);
            Swap(ref current, ref next);
        }

        // 2.5. Portrait Background Blur
        if (settings.BackgroundBlur > 0.5f)
        {
            int blurRadius = Math.Clamp((int)Math.Round((settings.BackgroundBlur / 100.0f) * 14.0f), 2, 14);
            ApplyOpticalDefocus(current, next, _scratchBufferB!, width, height, blurRadius);
            BlendPortraitBackgroundBlur(current, next, width, height);
        }

        // 2.75. Chroma Key Green Screen
        if (settings.ChromaKeyEnabled)
        {
            ApplyChromaKey(current, width, height, settings.ChromaKeyThreshold);
        }

        // 3. Color Grading, Brightness, Contrast, Saturation, Temperature, Vignette, Presets, Custom LUT
        bool hasColorMods = Math.Abs(settings.Brightness) > 0.01f ||
                           Math.Abs(settings.Contrast) > 0.01f ||
                           Math.Abs(settings.Saturation) > 0.01f ||
                           Math.Abs(settings.Temperature) > 0.01f ||
                           Math.Abs(settings.Tint) > 0.01f ||
                           settings.Vignette > 0.01f ||
                           settings.ColorPreset != StudioColorPreset.None ||
                           settings.CustomLut != null ||
                           Math.Abs(settings.HardwareIso - 100.0f) > 0.5f ||
                           Math.Abs(settings.HardwareShutter - 60.0f) > 0.5f ||
                           Math.Abs(settings.HardwareWbKelvin - 5400.0f) > 10.0f;

        if (hasColorMods)
        {
            ApplyColorGrading(current, next, width, height, settings);
            Swap(ref current, ref next);
        }

        // 4. Sharpness & Manual Focus (Defocus Blur / Peaking Sharpness)
        if (settings.HardwareFocus < 0.48f)
        {
            int blurRadius = Math.Clamp((int)Math.Round((0.5f - settings.HardwareFocus) * 24.0f), 1, 12);
            ApplyOpticalDefocus(current, next, _scratchBufferB!, width, height, blurRadius);
            Swap(ref current, ref next);
        }

        float effectiveSharpness = settings.Sharpness + (settings.HardwareFocus > 0.52f ? (settings.HardwareFocus - 0.5f) * 140.0f : 0.0f);
        if (effectiveSharpness > 0.5f)
        {
            ApplySharpness(current, next, width, height, Math.Min(120.0f, effectiveSharpness));
            Swap(ref current, ref next);
        }

        // 5. HUD Overlay
        if (settings.ShowHudOverlay)
        {
            // HUD can be rendered directly into current buffer
            RenderHudOverlay(current, width, height, settings);
        }

        // If the result ended up in a scratch buffer and wasn't copied back, ensure it's in a returnable buffer
        if (ReferenceEquals(current, sourceBgra))
        {
            return sourceBgra;
        }

        Buffer.BlockCopy(current, 0, sourceBgra, 0, bufferSize);
        return sourceBgra;
    }

    private void EnsureBuffers(int size)
    {
        lock (_bufferLock)
        {
            if (_scratchBufferA == null || _scratchBufferA.Length < size)
            {
                _scratchBufferA = new byte[size];
            }
            if (_scratchBufferB == null || _scratchBufferB.Length < size)
            {
                _scratchBufferB = new byte[size];
            }
        }
    }

    /// <summary>
    /// Converts a BGRA32 buffer to NV12 using BT.709 integer arithmetic.
    /// </summary>
    public static void ConvertBgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        Driver.CamoVirtualCameraDriver.ConvertBgraToNv12(bgra, nv12, width, height);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(ref byte[] a, ref byte[] b)
    {
        byte[] temp = a;
        a = b;
        b = temp;
    }

    /// <summary>
    /// Smooth bilinear zoom and 2D pan.
    /// </summary>
    private static unsafe void ApplyZoomAndPan(byte[] src, byte[] dst, int width, int height, float zoom, float panX, float panY)
    {
        float cropWidth = width / zoom;
        float cropHeight = height / zoom;

        float maxOffsetX = (width - cropWidth) * 0.5f;
        float maxOffsetY = (height - cropHeight) * 0.5f;

        float startX = maxOffsetX + (panX * maxOffsetX);
        float startY = maxOffsetY + (panY * maxOffsetY);

        startX = Math.Clamp(startX, 0, width - cropWidth);
        startY = Math.Clamp(startY, 0, height - cropHeight);

        float scaleX = cropWidth / width;
        float scaleY = cropHeight / height;

        fixed (byte* pSrcBase = src)
        fixed (byte* pDstBase = dst)
        {
            nint srcPtr = (nint)pSrcBase;
            nint dstPtr = (nint)pDstBase;

            Parallel.For(0, height, y =>
            {
                byte* pSrc = (byte*)srcPtr;
                byte* pDst = (byte*)dstPtr;

                float srcY = startY + (y * scaleY);
                int y0 = (int)srcY;
                int y1 = Math.Min(y0 + 1, height - 1);
                float dy = srcY - y0;
                float invDy = 1.0f - dy;

                byte* pDstRow = pDst + (y * width * 4);
                byte* pSrcRow0 = pSrc + (y0 * width * 4);
                byte* pSrcRow1 = pSrc + (y1 * width * 4);

                for (int x = 0; x < width; x++)
                {
                    float srcX = startX + (x * scaleX);
                    int x0 = (int)srcX;
                    int x1 = Math.Min(x0 + 1, width - 1);
                    float dx = srcX - x0;
                    float invDx = 1.0f - dx;

                    float w00 = invDx * invDy;
                    float w10 = dx * invDy;
                    float w01 = invDx * dy;
                    float w11 = dx * dy;

                    int offset00 = x0 * 4;
                    int offset10 = x1 * 4;

                    int dstOffset = x * 4;

                    // Blue
                    pDstRow[dstOffset] = (byte)(
                        (pSrcRow0[offset00] * w00) +
                        (pSrcRow0[offset10] * w10) +
                        (pSrcRow1[offset00] * w01) +
                        (pSrcRow1[offset10] * w11)
                    );

                    // Green
                    pDstRow[dstOffset + 1] = (byte)(
                        (pSrcRow0[offset00 + 1] * w00) +
                        (pSrcRow0[offset10 + 1] * w10) +
                        (pSrcRow1[offset00 + 1] * w01) +
                        (pSrcRow1[offset10 + 1] * w11)
                    );

                    // Red
                    pDstRow[dstOffset + 2] = (byte)(
                        (pSrcRow0[offset00 + 2] * w00) +
                        (pSrcRow0[offset10 + 2] * w10) +
                        (pSrcRow1[offset00 + 2] * w01) +
                        (pSrcRow1[offset10 + 2] * w11)
                    );

                    // Alpha
                    pDstRow[dstOffset + 3] = 255;
                }
            });
        }
    }

    /// <summary>
    /// Applies horizontal mirror and 90/180/270 degree rotation.
    /// </summary>
    private static unsafe void ApplyGeometricTransform(byte[] src, byte[] dst, int width, int height, bool mirror, int rotation)
    {
        fixed (byte* pSrcBase = src)
        fixed (byte* pDstBase = dst)
        {
            nint srcPtr = (nint)pSrcBase;
            nint dstPtr = (nint)pDstBase;

            Parallel.For(0, height, y =>
            {
                byte* pSrc = (byte*)srcPtr;
                byte* pDst = (byte*)dstPtr;

                for (int x = 0; x < width; x++)
                {
                    int srcX = mirror ? (width - 1 - x) : x;
                    int srcY = y;

                    int outX = srcX;
                    int outY = srcY;

                    switch (rotation)
                    {
                        case 90:
                            outX = (int)(((float)(height - 1 - srcY) / height) * width);
                            outY = (int)(((float)srcX / width) * height);
                            break;
                        case 180:
                            outX = width - 1 - srcX;
                            outY = height - 1 - srcY;
                            break;
                        case 270:
                            outX = (int)(((float)srcY / height) * width);
                            outY = (int)(((float)(width - 1 - srcX) / width) * height);
                            break;
                    }

                    outX = Math.Clamp(outX, 0, width - 1);
                    outY = Math.Clamp(outY, 0, height - 1);

                    int srcIndex = (y * width + x) * 4;
                    int dstIndex = (outY * width + outX) * 4;

                    *(uint*)(pDst + dstIndex) = *(uint*)(pSrc + srcIndex);
                }
            });
        }
    }

    /// <summary>
    /// Combined high-speed pixel pipeline for brightness, contrast, saturation, temperature, vignette, and color presets.
    /// </summary>
    private static unsafe void ApplyColorGrading(byte[] src, byte[] dst, int width, int height, StudioFilterSettings settings)
    {
        float isoFactor = Math.Max(0.1f, settings.HardwareIso / 100.0f);
        float shutterFactor = Math.Max(0.05f, 60.0f / Math.Max(1.0f, settings.HardwareShutter));
        float evStops = MathF.Log2(isoFactor * shutterFactor);
        float exposureGain = MathF.Pow(2.0f, evStops * 0.65f);
        bool hasExposureGain = Math.Abs(exposureGain - 1.0f) > 0.005f;

        float wbDelta = (settings.HardwareWbKelvin - 5400.0f) / 20.0f;
        float tempRed = (settings.Temperature + wbDelta) * 0.55f;
        float tempBlue = -(settings.Temperature + wbDelta) * 0.55f;

        float brightness = settings.Brightness * 1.28f;
        float contrastFactor = (259f * (settings.Contrast + 255f)) / (255f * (259f - settings.Contrast));
        float satFactor = 1.0f + (settings.Saturation / 100.0f);
        float tintGreen = settings.Tint * 0.45f;
        float vignetteStrength = settings.Vignette / 100.0f;
        StudioColorPreset preset = settings.ColorPreset;
        CubeLut3D? customLut = settings.CustomLut;

        float cx = width * 0.5f;
        float cy = height * 0.5f;
        float maxDistSq = (cx * cx) + (cy * cy);

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            nint srcPtr = (nint)pSrc;
            nint dstPtr = (nint)pDst;

            Parallel.For(0, height, y =>
            {
                byte* pSrcLocal = (byte*)srcPtr;
                byte* pDstLocal = (byte*)dstPtr;

                int rowOffset = y * width * 4;
                float dy = (y - cy);
                float dySq = dy * dy;

                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + (x * 4);

                    float b = pSrcLocal[idx];
                    float g = pSrcLocal[idx + 1];
                    float r = pSrcLocal[idx + 2];

                    if (hasExposureGain)
                    {
                        r *= exposureGain;
                        g *= exposureGain;
                        b *= exposureGain;
                    }

                    // 1. Contrast
                    r = (contrastFactor * (r - 128f)) + 128f;
                    g = (contrastFactor * (g - 128f)) + 128f;
                    b = (contrastFactor * (b - 128f)) + 128f;

                    // 2. Brightness
                    r += brightness;
                    g += brightness;
                    b += brightness;

                    // 3. White Balance: Temperature & Tint
                    r += tempRed;
                    b += tempBlue;
                    g += tintGreen;

                    // 4. Saturation
                    if (Math.Abs(settings.Saturation) > 0.01f)
                    {
                        float gray = (0.299f * r) + (0.587f * g) + (0.114f * b);
                        r = gray + ((r - gray) * satFactor);
                        g = gray + ((g - gray) * satFactor);
                        b = gray + ((b - gray) * satFactor);
                    }

                    // 5. Color Presets
                    switch (preset)
                    {
                        case StudioColorPreset.StudioWarm:
                            r = (r * 1.07f) + 8f;
                            g = (g * 1.02f) + 3f;
                            b = (b * 0.92f) - 4f;
                            break;

                        case StudioColorPreset.CinematicTealOrange:
                            float lum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                            if (lum < 128f)
                            {
                                float sFactor = (128f - lum) / 128f;
                                b += 32f * sFactor;
                                g += 16f * sFactor;
                                r -= 8f * sFactor;
                            }
                            else
                            {
                                float hFactor = (lum - 128f) / 128f;
                                r += 36f * hFactor;
                                g += 14f * hFactor;
                                b -= 18f * hFactor;
                            }
                            break;

                        case StudioColorPreset.CyberpunkNeon:
                            r = (r * 1.25f) + 12f;
                            b = (b * 1.35f) + 24f;
                            g *= 0.82f;
                            break;

                        case StudioColorPreset.NoirMono:
                            float monoLum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                            float monoContrast = ((monoLum - 128f) * 1.35f) + 128f;
                            r = g = b = monoContrast;
                            break;

                        case StudioColorPreset.VividPop:
                            float vLum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                            r = vLum + ((r - vLum) * 1.45f);
                            g = vLum + ((g - vLum) * 1.45f);
                            b = vLum + ((b - vLum) * 1.45f);
                            r = ((r - 128f) * 1.12f) + 128f;
                            g = ((g - 128f) * 1.12f) + 128f;
                            b = ((b - 128f) * 1.12f) + 128f;
                            break;
                    }

                    // 5.5. Custom .cube 3D LUT
                    if (customLut != null)
                    {
                        customLut.Apply(ref r, ref g, ref b);
                    }

                    // 6. Vignette
                    if (vignetteStrength > 0.001f)
                    {
                        float dx = (x - cx);
                        float distSq = (dx * dx) + dySq;
                        float normDist = distSq / maxDistSq;
                        float vFactor = 1.0f - (normDist * vignetteStrength * 0.75f);
                        vFactor = Math.Clamp(vFactor, 0.0f, 1.0f);

                        r *= vFactor;
                        g *= vFactor;
                        b *= vFactor;
                    }

                    pDstLocal[idx] = (byte)Math.Clamp((int)b, 0, 255);
                    pDstLocal[idx + 1] = (byte)Math.Clamp((int)g, 0, 255);
                    pDstLocal[idx + 2] = (byte)Math.Clamp((int)r, 0, 255);
                    pDstLocal[idx + 3] = 255;
                }
            });
        }
    }

    /// <summary>
    /// Applies 3x3 high-pass unsharp mask convolution.
    /// </summary>
    private static unsafe void ApplySharpness(byte[] src, byte[] dst, int width, int height, float sharpness)
    {
        float amount = sharpness / 100.0f * 0.65f;
        float centerWeight = 1.0f + (4.0f * amount);
        float edgeWeight = -amount;

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            nint srcPtr = (nint)pSrc;
            nint dstPtr = (nint)pDst;

            Parallel.For(1, height - 1, y =>
            {
                byte* pSrcLocal = (byte*)srcPtr;
                byte* pDstLocal = (byte*)dstPtr;

                int rowOffset = y * width * 4;
                int rowAbove = (y - 1) * width * 4;
                int rowBelow = (y + 1) * width * 4;

                for (int x = 1; x < width - 1; x++)
                {
                    int c = rowOffset + (x * 4);
                    int l = rowOffset + ((x - 1) * 4);
                    int r = rowOffset + ((x + 1) * 4);
                    int t = rowAbove + (x * 4);
                    int b = rowBelow + (x * 4);

                    for (int ch = 0; ch < 3; ch++)
                    {
                        float val = (pSrcLocal[c + ch] * centerWeight) +
                                    ((pSrcLocal[l + ch] + pSrcLocal[r + ch] + pSrcLocal[t + ch] + pSrcLocal[b + ch]) * edgeWeight);

                        pDstLocal[c + ch] = (byte)Math.Clamp((int)val, 0, 255);
                    }
                    pDstLocal[c + 3] = 255;
                }
            });
        }
    }

    /// <summary>
    /// High-speed 2-pass separable box blur simulating optical defocus.
    /// Pass 1 (horizontal): src -> temp
    /// Pass 2 (vertical): temp -> dst
    /// Clamps coordinates to image boundaries and ensures alpha is 255.
    /// </summary>
    private static unsafe void ApplyOpticalDefocus(byte[] src, byte[] dst, byte[] temp, int width, int height, int radius)
    {
        if (radius < 1)
        {
            Buffer.BlockCopy(src, 0, dst, 0, width * height * 4);
            return;
        }

        int kernelSize = (2 * radius) + 1;
        int stride = width * 4;

        fixed (byte* pSrcBase = src)
        fixed (byte* pDstBase = dst)
        fixed (byte* pTempBase = temp)
        {
            nint srcPtr = (nint)pSrcBase;
            nint dstPtr = (nint)pDstBase;
            nint tempPtr = (nint)pTempBase;

            // Pass 1: Horizontal blur (src -> temp)
            Parallel.For(0, height, y =>
            {
                byte* pSrcRow = (byte*)srcPtr + (y * stride);
                byte* pTempRow = (byte*)tempPtr + (y * stride);

                int sumB = 0, sumG = 0, sumR = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int kx = Math.Clamp(k, 0, width - 1) * 4;
                    sumB += pSrcRow[kx];
                    sumG += pSrcRow[kx + 1];
                    sumR += pSrcRow[kx + 2];
                }

                for (int x = 0; x < width; x++)
                {
                    int outOffset = x * 4;
                    pTempRow[outOffset] = (byte)Math.Clamp((sumB + (kernelSize / 2)) / kernelSize, 0, 255);
                    pTempRow[outOffset + 1] = (byte)Math.Clamp((sumG + (kernelSize / 2)) / kernelSize, 0, 255);
                    pTempRow[outOffset + 2] = (byte)Math.Clamp((sumR + (kernelSize / 2)) / kernelSize, 0, 255);
                    pTempRow[outOffset + 3] = 255;

                    int removeX = Math.Clamp(x - radius, 0, width - 1) * 4;
                    int addX = Math.Clamp(x + radius + 1, 0, width - 1) * 4;

                    sumB += pSrcRow[addX] - pSrcRow[removeX];
                    sumG += pSrcRow[addX + 1] - pSrcRow[removeX + 1];
                    sumR += pSrcRow[addX + 2] - pSrcRow[removeX + 2];
                }
            });

            // Pass 2: Vertical blur (temp -> dst)
            Parallel.For(0, width, x =>
            {
                byte* pTempCol = (byte*)tempPtr + (x * 4);
                byte* pDstCol = (byte*)dstPtr + (x * 4);

                int sumB = 0, sumG = 0, sumR = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int ky = Math.Clamp(k, 0, height - 1);
                    byte* pSample = pTempCol + (ky * stride);
                    sumB += pSample[0];
                    sumG += pSample[1];
                    sumR += pSample[2];
                }

                for (int y = 0; y < height; y++)
                {
                    byte* pOut = pDstCol + (y * stride);
                    pOut[0] = (byte)Math.Clamp((sumB + (kernelSize / 2)) / kernelSize, 0, 255);
                    pOut[1] = (byte)Math.Clamp((sumG + (kernelSize / 2)) / kernelSize, 0, 255);
                    pOut[2] = (byte)Math.Clamp((sumR + (kernelSize / 2)) / kernelSize, 0, 255);
                    pOut[3] = 255;

                    int removeY = Math.Clamp(y - radius, 0, height - 1);
                    int addY = Math.Clamp(y + radius + 1, 0, height - 1);

                    byte* pRemove = pTempCol + (removeY * stride);
                    byte* pAdd = pTempCol + (addY * stride);

                    sumB += pAdd[0] - pRemove[0];
                    sumG += pAdd[1] - pRemove[1];
                    sumR += pAdd[2] - pRemove[2];
                }
            });
        }
    }

    /// <summary>
    /// Estimates the subject/face centroid using subsampled YCbCr skin-chroma detection for auto-framing.
    /// </summary>
    private static unsafe void ComputeAutoFramingTarget(byte[] bgra, int width, int height, out float targetPanX, out float targetPanY)
    {
        const int stridePx = 8;
        float sumX = 0.0f;
        float sumY = 0.0f;
        float sumWeight = 0.0f;
        float upperCutoffY = height * 0.72f;

        fixed (byte* pBase = bgra)
        {
            for (int y = 0; y < height; y += stridePx)
            {
                byte* pRow = pBase + (y * width * 4);
                float weight = y < upperCutoffY ? 1.5f : 0.65f;

                for (int x = 0; x < width; x += stridePx)
                {
                    int idx = x * 4;
                    float b = pRow[idx];
                    float g = pRow[idx + 1];
                    float r = pRow[idx + 2];

                    float yLum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                    if (yLum <= 55.0f)
                    {
                        continue;
                    }

                    float cb = 128.0f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
                    float cr = 128.0f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);

                    if (cb >= 77.0f && cb <= 127.0f && cr >= 133.0f && cr <= 173.0f)
                    {
                        sumX += x * weight;
                        sumY += y * weight;
                        sumWeight += weight;
                    }
                }
            }
        }

        if (sumWeight < 10.0f)
        {
            targetPanX = 0.0f;
            targetPanY = 0.0f;
            return;
        }

        float avgX = sumX / sumWeight;
        float avgY = sumY / sumWeight;

        float normX = ((avgX / width) - 0.5f) * 2.0f;
        float normY = ((avgY / height) - 0.5f) * 2.0f;

        targetPanX = Math.Clamp(normX * 1.8f, -1.0f, 1.0f);
        targetPanY = Math.Clamp(normY * 1.8f, -1.0f, 1.0f);
    }

    /// <summary>
    /// Blends sharp foreground subject with blurred background using an elliptical portrait mask and skin protection.
    /// Writes the blended result back into the sharp buffer.
    /// </summary>
    private static unsafe void BlendPortraitBackgroundBlur(byte[] sharp, byte[] blurred, int width, int height)
    {
        float cx = width * 0.5f;
        float cy = height * 0.58f;
        float invRx = 1.0f / Math.Max(1.0f, width * 0.34f);
        float invRy = 1.0f / Math.Max(1.0f, height * 0.48f);

        fixed (byte* pSharpBase = sharp)
        fixed (byte* pBlurBase = blurred)
        {
            nint sharpPtr = (nint)pSharpBase;
            nint blurPtr = (nint)pBlurBase;

            Parallel.For(0, height, y =>
            {
                byte* pSharpRow = (byte*)sharpPtr + (y * width * 4);
                byte* pBlurRow = (byte*)blurPtr + (y * width * 4);

                float dy = (y - cy) * invRy;
                float dySq = dy * dy;

                for (int x = 0; x < width; x++)
                {
                    int idx = x * 4;
                    float dx = (x - cx) * invRx;
                    float dist = MathF.Sqrt((dx * dx) + dySq);

                    float t = Math.Clamp((dist - 0.65f) / 0.60f, 0.0f, 1.0f);
                    float mask = 1.0f - (t * t * (3.0f - (2.0f * t)));

                    float sb = pSharpRow[idx];
                    float sg = pSharpRow[idx + 1];
                    float sr = pSharpRow[idx + 2];

                    if (dist < 1.55f && mask < 0.92f)
                    {
                        float yLum = (0.299f * sr) + (0.587f * sg) + (0.114f * sb);
                        if (yLum > 50.0f)
                        {
                            float cb = 128.0f - (0.168736f * sr) - (0.331264f * sg) + (0.5f * sb);
                            float cr = 128.0f + (0.5f * sr) - (0.418688f * sg) - (0.081312f * sb);

                            if (cb >= 75.0f && cb <= 129.0f && cr >= 131.0f && cr <= 175.0f)
                            {
                                float skinFalloff = 1.0f - Math.Clamp((dist - 1.15f) / 0.40f, 0.0f, 1.0f);
                                mask = MathF.Max(mask, 0.92f * skinFalloff);
                            }
                        }
                    }

                    if (mask < 0.999f)
                    {
                        float bb = pBlurRow[idx];
                        float bg = pBlurRow[idx + 1];
                        float br = pBlurRow[idx + 2];

                        pSharpRow[idx] = (byte)(bb + ((sb - bb) * mask));
                        pSharpRow[idx + 1] = (byte)(bg + ((sg - bg) * mask));
                        pSharpRow[idx + 2] = (byte)(br + ((sr - br) * mask));
                        pSharpRow[idx + 3] = 255;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Keys out green screen dominance with edge spill suppression and composites onto a #14161D dark studio backdrop.
    /// </summary>
    private static unsafe void ApplyChromaKey(byte[] bgra, int width, int height, float threshold)
    {
        float minDom = Math.Clamp(52.0f - (threshold * 0.48f), 4.0f, 52.0f);
        const float invSoftness = 1.0f / 24.0f;
        const float bgR = 20.0f;
        const float bgG = 22.0f;
        const float bgB = 29.0f;

        fixed (byte* pBase = bgra)
        {
            nint ptr = (nint)pBase;

            Parallel.For(0, height, y =>
            {
                byte* pRow = (byte*)ptr + (y * width * 4);

                for (int x = 0; x < width; x++)
                {
                    int idx = x * 4;
                    float b = pRow[idx];
                    float g = pRow[idx + 1];
                    float r = pRow[idx + 2];

                    float maxRb = MathF.Max(r, b);
                    float greenDom = g - maxRb;

                    if (greenDom > minDom)
                    {
                        float keyAmount = Math.Clamp((greenDom - minDom) * invSoftness, 0.0f, 1.0f);
                        keyAmount = keyAmount * keyAmount * (3.0f - (2.0f * keyAmount));
                        float fgAlpha = 1.0f - keyAmount;

                        float spillReduce = Math.Clamp(keyAmount * 1.35f, 0.0f, 1.0f);
                        float fgG = g - ((g - maxRb) * spillReduce);

                        pRow[idx] = (byte)Math.Clamp((int)((b * fgAlpha) + (bgB * keyAmount)), 0, 255);
                        pRow[idx + 1] = (byte)Math.Clamp((int)((fgG * fgAlpha) + (bgG * keyAmount)), 0, 255);
                        pRow[idx + 2] = (byte)Math.Clamp((int)((r * fgAlpha) + (bgR * keyAmount)), 0, 255);
                        pRow[idx + 3] = 255;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Renders professional framing grid lines, center crosshair, and safe area guides.
    /// </summary>
    private static unsafe void RenderHudOverlay(byte[] bgra, int width, int height, StudioFilterSettings settings)
    {
        fixed (byte* p = bgra)
        {
            // 1. Rule of thirds lines (semi-transparent cyan/white)
            int x1 = width / 3;
            int x2 = (width * 2) / 3;
            int y1 = height / 3;
            int y2 = (height * 2) / 3;

            byte gridB = 220, gridG = 220, gridR = 220;

            for (int y = 0; y < height; y += 4)
            {
                DrawPixel(p, width, height, x1, y, gridB, gridG, gridR);
                DrawPixel(p, width, height, x2, y, gridB, gridG, gridR);
            }

            for (int x = 0; x < width; x += 4)
            {
                DrawPixel(p, width, height, x, y1, gridB, gridG, gridR);
                DrawPixel(p, width, height, x, y2, gridB, gridG, gridR);
            }

            // 2. Center crosshair
            int cx = width / 2;
            int cy = height / 2;
            int crossSize = Math.Min(width, height) / 24;

            for (int d = -crossSize; d <= crossSize; d++)
            {
                if (Math.Abs(d) > 3) // Leave gap in center
                {
                    DrawPixel(p, width, height, cx + d, cy, 50, 255, 50); // Vibrant green
                    DrawPixel(p, width, height, cx, cy + d, 50, 255, 50);
                }
            }

            // 3. Safe area border (90% rectangle)
            int safeInsetX = width / 20;
            int safeInsetY = height / 20;
            int right = width - safeInsetX;
            int bottom = height - safeInsetY;

            for (int x = safeInsetX; x <= right; x += 6)
            {
                DrawPixel(p, width, height, x, safeInsetY, 160, 160, 160);
                DrawPixel(p, width, height, x, bottom, 160, 160, 160);
            }
            for (int y = safeInsetY; y <= bottom; y += 6)
            {
                DrawPixel(p, width, height, safeInsetX, y, 160, 160, 160);
                DrawPixel(p, width, height, right, y, 160, 160, 160);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void DrawPixel(byte* p, int width, int height, int x, int y, byte b, byte g, byte r)
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
}
