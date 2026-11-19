using System;
using System.Globalization;
using System.IO;

namespace Ghostcam.Media.Pipeline;

/// <summary>
/// Parses and applies standard Adobe .cube 3D color lookup tables (LUTs) with trilinear interpolation.
/// </summary>
public sealed class CubeLut3D
{
    private readonly float[] _table;
    private readonly float _invRangeR;
    private readonly float _invRangeG;
    private readonly float _invRangeB;

    private CubeLut3D(int size, string name, float[] domainMin, float[] domainMax, float[] table)
    {
        Size = size;
        Name = name;
        DomainMin = domainMin;
        DomainMax = domainMax;
        _table = table;

        float rangeR = domainMax[0] - domainMin[0];
        float rangeG = domainMax[1] - domainMin[1];
        float rangeB = domainMax[2] - domainMin[2];

        _invRangeR = rangeR > 1e-6f ? 1.0f / rangeR : 1.0f;
        _invRangeG = rangeG > 1e-6f ? 1.0f / rangeG : 1.0f;
        _invRangeB = rangeB > 1e-6f ? 1.0f / rangeB : 1.0f;
    }

    /// <summary>
    /// Dimension N of the N x N x N 3D LUT cube.
    /// </summary>
    public int Size { get; }

    /// <summary>
    /// Display name parsed from TITLE or derived from filename.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Minimum input RGB domain bounds (default [0, 0, 0]).
    /// </summary>
    public float[] DomainMin { get; }

    /// <summary>
    /// Maximum input RGB domain bounds (default [1, 1, 1]).
    /// </summary>
    public float[] DomainMax { get; }

    /// <summary>
    /// Loads and validates a standard Adobe .cube 3D LUT file.
    /// </summary>
    public static CubeLut3D LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"LUT file not found: '{filePath}'.", filePath);
        }

        string name = Path.GetFileNameWithoutExtension(filePath);
        float[] domainMin = [0.0f, 0.0f, 0.0f];
        float[] domainMax = [1.0f, 1.0f, 1.0f];
        int size = 0;
        float[]? table = null;
        int entryIndex = 0;
        int expectedEntries = 0;

        char[] separators = [' ', '\t'];

        foreach (string rawLine in File.ReadLines(filePath))
        {
            int commentIdx = rawLine.IndexOf('#');
            string line = (commentIdx >= 0 ? rawLine[..commentIdx] : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                string titleValue = line.Length > 5 ? line[5..].Trim() : string.Empty;
                if (titleValue.Length >= 2 && titleValue[0] == '"' && titleValue[^1] == '"')
                {
                    titleValue = titleValue[1..^1].Trim();
                }

                if (!string.IsNullOrWhiteSpace(titleValue))
                {
                    name = titleValue;
                }
                continue;
            }

            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("1D .cube LUTs are not supported; expected LUT_3D_SIZE.");
            }

            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                {
                    throw new InvalidDataException($"Invalid LUT_3D_SIZE directive: '{line}'.");
                }

                if (size < 2 || size > 65)
                {
                    throw new InvalidDataException($"LUT_3D_SIZE must be between 2 and 65, got {size}.");
                }

                expectedEntries = size * size * size;
                table = new float[expectedEntries * 3];
                continue;
            }

            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
            {
                domainMin = ParseTriplet(line, "DOMAIN_MIN");
                continue;
            }

            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
            {
                domainMax = ParseTriplet(line, "DOMAIN_MAX");
                continue;
            }

            // Ignore optional metadata keywords like LUT_3D_INPUT_RANGE
            if (char.IsLetter(line[0]))
            {
                continue;
            }

            if (table == null)
            {
                throw new InvalidDataException("LUT data encountered before LUT_3D_SIZE header.");
            }

            if (entryIndex >= expectedEntries)
            {
                throw new InvalidDataException($"Too many LUT entries: expected {expectedEntries} rows for size {size}.");
            }

            string[] tokens = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 ||
                !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float rVal) ||
                !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float gVal) ||
                !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float bVal))
            {
                throw new InvalidDataException($"Malformed RGB triplet in .cube file: '{line}'.");
            }

            int offset = entryIndex * 3;
            table[offset] = rVal;
            table[offset + 1] = gVal;
            table[offset + 2] = bVal;
            entryIndex++;
        }

        if (size < 2 || size > 65 || table == null)
        {
            throw new InvalidDataException("Missing or invalid LUT_3D_SIZE header in .cube file.");
        }

        if (entryIndex != expectedEntries)
        {
            throw new InvalidDataException(
                $"LUT entry count mismatch: expected {expectedEntries} rows for size {size}, found {entryIndex}.");
        }

        return new CubeLut3D(size, name, domainMin, domainMax, table);
    }

    /// <summary>
    /// Applies the 3D LUT using trilinear interpolation on 0..255 RGB channel values.
    /// </summary>
    public void Apply(ref float r, ref float g, ref float b)
    {
        float nr = Math.Clamp(((r / 255.0f) - DomainMin[0]) * _invRangeR, 0.0f, 1.0f);
        float ng = Math.Clamp(((g / 255.0f) - DomainMin[1]) * _invRangeG, 0.0f, 1.0f);
        float nb = Math.Clamp(((b / 255.0f) - DomainMin[2]) * _invRangeB, 0.0f, 1.0f);

        int maxIndex = Size - 1;
        float fr = nr * maxIndex;
        float fg = ng * maxIndex;
        float fb = nb * maxIndex;

        int r0 = Math.Clamp((int)fr, 0, maxIndex);
        int g0 = Math.Clamp((int)fg, 0, maxIndex);
        int b0 = Math.Clamp((int)fb, 0, maxIndex);

        int r1 = Math.Min(r0 + 1, maxIndex);
        int g1 = Math.Min(g0 + 1, maxIndex);
        int b1 = Math.Min(b0 + 1, maxIndex);

        float dr = fr - r0;
        float dg = fg - g0;
        float db = fb - b0;

        int strideB = Size * Size;

        int c000 = (r0 + (Size * g0) + (strideB * b0)) * 3;
        int c100 = (r1 + (Size * g0) + (strideB * b0)) * 3;
        int c010 = (r0 + (Size * g1) + (strideB * b0)) * 3;
        int c110 = (r1 + (Size * g1) + (strideB * b0)) * 3;

        int c001 = (r0 + (Size * g0) + (strideB * b1)) * 3;
        int c101 = (r1 + (Size * g0) + (strideB * b1)) * 3;
        int c011 = (r0 + (Size * g1) + (strideB * b1)) * 3;
        int c111 = (r1 + (Size * g1) + (strideB * b1)) * 3;

        // Trilinear interpolation for R
        float r00 = _table[c000] + ((_table[c100] - _table[c000]) * dr);
        float r10 = _table[c010] + ((_table[c110] - _table[c010]) * dr);
        float r01 = _table[c001] + ((_table[c101] - _table[c001]) * dr);
        float r11 = _table[c011] + ((_table[c111] - _table[c011]) * dr);
        float r0g = r00 + ((r10 - r00) * dg);
        float r1g = r01 + ((r11 - r01) * dg);
        float outR = r0g + ((r1g - r0g) * db);

        // Trilinear interpolation for G
        float g00 = _table[c000 + 1] + ((_table[c100 + 1] - _table[c000 + 1]) * dr);
        float g10 = _table[c010 + 1] + ((_table[c110 + 1] - _table[c010 + 1]) * dr);
        float g01 = _table[c001 + 1] + ((_table[c101 + 1] - _table[c001 + 1]) * dr);
        float g11 = _table[c011 + 1] + ((_table[c111 + 1] - _table[c011 + 1]) * dr);
        float g0g = g00 + ((g10 - g00) * dg);
        float g1g = g01 + ((g11 - g01) * dg);
        float outG = g0g + ((g1g - g0g) * db);

        // Trilinear interpolation for B
        float b00 = _table[c000 + 2] + ((_table[c100 + 2] - _table[c000 + 2]) * dr);
        float b10 = _table[c010 + 2] + ((_table[c110 + 2] - _table[c010 + 2]) * dr);
        float b01 = _table[c001 + 2] + ((_table[c101 + 2] - _table[c001 + 2]) * dr);
        float b11 = _table[c011 + 2] + ((_table[c111 + 2] - _table[c011 + 2]) * dr);
        float b0g = b00 + ((b10 - b00) * dg);
        float b1g = b01 + ((b11 - b01) * dg);
        float outB = b0g + ((b1g - b0g) * db);

        r = outR * 255.0f;
        g = outG * 255.0f;
        b = outB * 255.0f;
    }

    private static float[] ParseTriplet(string line, string directive)
    {
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v0) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v1) ||
            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float v2))
        {
            throw new InvalidDataException($"Invalid {directive} directive: '{line}'.");
        }

        return [v0, v1, v2];
    }
}
