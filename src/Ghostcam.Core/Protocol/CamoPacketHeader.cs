using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostcam.Core.Protocol;

/// <summary>
/// Camo protocol packet types reverse-engineered from Camo Studio ECMA-335 metadata.
/// </summary>
public enum CamoPacketType : byte
{
    Hello                 = 0,  // 0x00
    Video                 = 1,  // 0x01
    Audio                 = 2,  // 0x02
    UpdateDeviceInfo      = 3,  // 0x03
    SyncSettings          = 4,  // 0x04
    Wake                  = 5,  // 0x05
    Sleep                 = 6,  // 0x06
    Heartbeat             = 7,  // 0x07
    Bye                   = 8,  // 0x08
    Pause                 = 9,  // 0x09
    Resume                = 10, // 0x0A
    Request               = 11, // 0x0B
    Response              = 12, // 0x0C
    WriteAReview          = 13, // 0x0D
    Ready                 = 14, // 0x0E
    EasterEgg             = 15, // 0x0F
    DebugInfo             = 16, // 0x10
    Authenticate          = 17, // 0x11
    AuthenticationResult  = 18, // 0x12
    Unknown               = 19, // 0x13
    AudioV2               = 20, // 0x14
    VideoV2               = 21, // 0x15
    SelectProtocolVersion = 22, // 0x16
    Hello2                = 23  // 0x17
}

/// <summary>
/// Represents the 7-byte binary header framing all Camo TCP packets.
/// Format:
/// - 2 bytes UInt16 SequenceId (Little-Endian)
/// - 4 bytes UInt32 PayloadLength (Little-Endian)
/// - 1 byte CamoPacketType PacketType
/// Total = 7 bytes.
/// </summary>
public readonly struct CamoPacketHeader : IEquatable<CamoPacketHeader>
{
    public const int HeaderSize = 7;

    public ushort SequenceId { get; }
    public uint PayloadLength { get; }
    public CamoPacketType PacketType { get; }

    public CamoPacketHeader(ushort sequenceId, uint payloadLength, CamoPacketType packetType)
    {
        SequenceId = sequenceId;
        PayloadLength = payloadLength;
        PacketType = packetType;
    }

    /// <summary>
    /// Parses a 7-byte slice into a <see cref="CamoPacketHeader"/>.
    /// </summary>
    public static CamoPacketHeader Parse(ReadOnlySpan<byte> span)
    {
        if (span.Length < HeaderSize)
        {
            throw new ArgumentException($"Buffer too small to contain CamoPacketHeader. Expected {HeaderSize} bytes, got {span.Length}.", nameof(span));
        }

        ushort sequenceId = BinaryPrimitives.ReadUInt16LittleEndian(span[0..2]);
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(span[2..6]);
        CamoPacketType packetType = (CamoPacketType)span[6];

        return new CamoPacketHeader(sequenceId, payloadLength, packetType);
    }

    /// <summary>
    /// Writes this 7-byte header into the provided destination buffer.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"Destination buffer too small. Expected at least {HeaderSize} bytes, got {destination.Length}.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[0..2], SequenceId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..6], PayloadLength);
        destination[6] = (byte)PacketType;
    }

    /// <summary>
    /// Reads exactly 7 bytes from the stream and parses the header.
    /// </summary>
    public static async ValueTask<CamoPacketHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[HeaderSize];
        await stream.ReadExactlyAsync(buffer, 0, HeaderSize, cancellationToken).ConfigureAwait(false);
        return Parse(buffer);
    }

    /// <summary>
    /// Reads a complete packet (header + payload) asynchronously from the stream.
    /// </summary>
    public static async ValueTask<(CamoPacketHeader Header, byte[] Payload)> ReadPacketAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        CamoPacketHeader header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        byte[] payload = header.PayloadLength > 0 ? new byte[header.PayloadLength] : Array.Empty<byte>();

        if (header.PayloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload, 0, (int)header.PayloadLength, cancellationToken).ConfigureAwait(false);
        }

        return (header, payload);
    }

    /// <summary>
    /// Serializes and writes a full Camo packet (7-byte header + payload) to the stream.
    /// </summary>
    public static async ValueTask WritePacketAsync(
        Stream stream,
        ushort sequenceId,
        CamoPacketType packetType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        byte[] headerBuffer = new byte[HeaderSize];
        var header = new CamoPacketHeader(sequenceId, (uint)payload.Length, packetType);
        header.WriteTo(headerBuffer);

        await stream.WriteAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool Equals(CamoPacketHeader other) =>
        SequenceId == other.SequenceId &&
        PayloadLength == other.PayloadLength &&
        PacketType == other.PacketType;

    public override bool Equals(object? obj) => obj is CamoPacketHeader other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SequenceId, PayloadLength, PacketType);

    public static bool operator ==(CamoPacketHeader left, CamoPacketHeader right) => left.Equals(right);
    public static bool operator !=(CamoPacketHeader left, CamoPacketHeader right) => !left.Equals(right);

    public override string ToString() => $"[CamoPacket seq={SequenceId} len={PayloadLength} type={PacketType}]";
}

/// <summary>
/// Parsed video frame packet with PTS, flags, keyframe/SPS/PPS detection, and Annex-B NAL payload.
/// </summary>
public sealed class VideoFramePacket
{
    public CamoPacketType PacketType { get; init; }
    public ushort SequenceId { get; init; }
    public double TimestampSeconds { get; init; }
    public long PresentationTimestampUs { get; init; }
    public uint Flags { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
    public bool IsKeyFrame { get; init; }
    public bool HasSpsPps { get; init; }

    /// <summary>
    /// Parses Camo Video (1) and VideoV2 (21) packets and converts AVCC formatted NAL units to Annex-B.
    /// </summary>
    public static VideoFramePacket Parse(CamoPacketHeader header, ReadOnlyMemory<byte> rawPayload)
    {
        // Video (1): 0-byte sub-header
        // VideoV2 (21): 8-byte sub-header: double timestampSeconds
        int offset = header.PacketType == CamoPacketType.VideoV2 ? 8 : 0;
        double ptsSeconds = (header.PacketType == CamoPacketType.VideoV2 && rawPayload.Length >= 8)
            ? BitConverter.ToDouble(rawPayload.Span.Slice(0, 8))
            : 0.0;

        ReadOnlySpan<byte> span = rawPayload.Span.Slice(Math.Min(offset, rawPayload.Length));

        bool isKeyframe = false;
        bool hasSpsPps = false;
        byte[] annexB;

        // If buffer already starts with Annex-B start code (00 00 00 01 or 00 00 01)
        if (span.Length >= 4 &&
            ((span[0] == 0 && span[1] == 0 && span[2] == 0 && span[3] == 1) ||
             (span[0] == 0 && span[1] == 0 && span[2] == 1)))
        {
            annexB = span.ToArray();

            for (int i = 0; i < span.Length - 4; i++)
            {
                if (span[i] == 0 && span[i + 1] == 0)
                {
                    int nalStart = -1;
                    if (span[i + 2] == 1) nalStart = i + 3;
                    else if (span[i + 2] == 0 && span[i + 3] == 1) nalStart = i + 4;

                    if (nalStart > 0 && nalStart < span.Length)
                    {
                        byte nalHeader = span[nalStart];
                        int nalType = nalHeader & 0x1F;
                        if (nalType == 5) isKeyframe = true;
                        if (nalType == 7 || nalType == 8) hasSpsPps = true;
                    }
                }
            }
        }
        else
        {
            // Convert AVCC [4-byte Big-Endian len][NAL...] to Annex-B [00 00 00 01][NAL...]
            using var ms = new MemoryStream(span.Length);
            int pos = 0;
            byte[] startCode = { 0, 0, 0, 1 };

            while (pos + 4 <= span.Length)
            {
                uint nalLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4));
                if (nalLen == 0 || pos + 4 + (int)nalLen > span.Length)
                {
                    break;
                }

                byte nalHeader = span[pos + 4];
                int nalType = nalHeader & 0x1F;
                if (nalType == 5) isKeyframe = true;
                if (nalType == 7 || nalType == 8) hasSpsPps = true;

                ms.Write(startCode, 0, 4);
                ms.Write(span.Slice(pos + 4, (int)nalLen));

                pos += 4 + (int)nalLen;
            }

            annexB = ms.ToArray();
            if (annexB.Length == 0 && span.Length > 0)
            {
                annexB = span.ToArray();
            }
        }

        return new VideoFramePacket
        {
            PacketType = header.PacketType,
            SequenceId = header.SequenceId,
            TimestampSeconds = ptsSeconds,
            PresentationTimestampUs = (long)(ptsSeconds * 1_000_000.0),
            Flags = isKeyframe ? 1u : 0u,
            IsKeyFrame = isKeyframe,
            HasSpsPps = hasSpsPps,
            Payload = annexB
        };
    }
}
