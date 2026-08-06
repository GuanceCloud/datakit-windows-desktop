using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Guance.Windows.Queue;

internal static class BatchFileFormat
{
    public const int HeaderSize = 256;
    private const int Version = 1;
    private const int ContentTypeOffset = 48;
    private const int MaxContentTypeBytes = HeaderSize - ContentTypeOffset;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GCBATCH1");

    public static byte[] CreateHeader(
        BatchStreamKind kind,
        DateTimeOffset createdAt,
        int recordCount,
        string contentType,
        long payloadLength,
        uint checksum)
    {
        var contentTypeBytes = Encoding.UTF8.GetBytes(contentType);
        if (contentTypeBytes.Length > MaxContentTypeBytes)
        {
            throw new InvalidDataException($"Batch content type exceeds {MaxContentTypeBytes} UTF-8 bytes.");
        }

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), (int)kind);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), createdAt.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), recordCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), contentTypeBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), checksum);
        contentTypeBytes.CopyTo(header, ContentTypeOffset);
        return header;
    }

    public static BatchHeader ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid cache batch magic.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported cache batch version {version}.");
        }

        var kindValue = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
        if (!Enum.IsDefined(typeof(BatchStreamKind), kindValue))
        {
            throw new InvalidDataException("Invalid cache batch stream kind.");
        }

        var contentTypeLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28, 4));
        if (contentTypeLength < 0 || contentTypeLength > MaxContentTypeBytes)
        {
            throw new InvalidDataException("Invalid cache batch content type length.");
        }

        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(24, 4));
        var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(32, 8));
        if (recordCount <= 0 || payloadLength <= 0)
        {
            throw new InvalidDataException("Cache batch is empty.");
        }

        DateTimeOffset createdAt;
        try
        {
            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Invalid cache batch creation timestamp.", ex);
        }

        return new BatchHeader(
            (BatchStreamKind)kindValue,
            createdAt,
            recordCount,
            Encoding.UTF8.GetString(header.Slice(ContentTypeOffset, contentTypeLength)),
            payloadLength,
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(40, 4)));
    }
}

internal sealed record BatchHeader(
    BatchStreamKind Kind,
    DateTimeOffset CreatedAt,
    int RecordCount,
    string ContentType,
    long PayloadLength,
    uint Checksum);

internal struct BatchCrc32
{
    private uint value;

    public static BatchCrc32 Create() => new() { value = uint.MaxValue };

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var current in bytes)
        {
            value ^= current;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ (0xEDB88320u & (uint)-(int)(value & 1));
            }
        }
    }

    public readonly uint GetCurrentHash() => value ^ uint.MaxValue;

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = Create();
        crc.Append(bytes);
        return crc.GetCurrentHash();
    }
}
