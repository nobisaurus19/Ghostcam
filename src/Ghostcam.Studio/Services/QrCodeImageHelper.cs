using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ghostcam.Studio.Services;

public static class QrCodeImageHelper
{
    /// <summary>
    /// Converts a 2D boolean QR code matrix into a crisp, high-contrast BitmapSource for display.
    /// </summary>
    public static BitmapSource CreateQrBitmap(bool[,] matrix, int scale = 6, int padding = 2)
    {
        int matrixWidth = matrix.GetLength(0);
        int matrixHeight = matrix.GetLength(1);

        int totalModulesX = matrixWidth + (padding * 2);
        int totalModulesY = matrixHeight + (padding * 2);

        int pixelWidth = totalModulesX * scale;
        int pixelHeight = totalModulesY * scale;

        byte[] pixels = new byte[pixelWidth * pixelHeight * 4]; // BGRA32

        // Initialize with pure white background (optimal for phone camera scanning)
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;     // B
            pixels[i + 1] = 255; // G
            pixels[i + 2] = 255; // R
            pixels[i + 3] = 255; // A
        }

        // Fill in dark modules
        for (int my = 0; my < matrixHeight; my++)
        {
            for (int mx = 0; mx < matrixWidth; mx++)
            {
                if (matrix[mx, my])
                {
                    int startX = (mx + padding) * scale;
                    int startY = (my + padding) * scale;

                    for (int py = 0; py < scale; py++)
                    {
                        int rowOffset = (startY + py) * pixelWidth * 4;
                        for (int px = 0; px < scale; px++)
                        {
                            int offset = rowOffset + ((startX + px) * 4);
                            pixels[offset] = 15;     // B
                            pixels[offset + 1] = 15; // G
                            pixels[offset + 2] = 15; // R
                            pixels[offset + 3] = 255; // A
                        }
                    }
                }
            }
        }

        WriteableBitmap bitmap = new(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, pixelWidth, pixelHeight), pixels, pixelWidth * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
