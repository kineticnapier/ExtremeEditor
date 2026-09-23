using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ExtremeEditor.AssetExtractor;

internal static class PngWriter
{
    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void WriteRgba32(
        string path,
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        bool flipVertically)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");

        int stride = checked(width * 4);
        int expectedLength = checked(stride * height);
        if (rgba.Length < expectedLength)
            throw new InvalidDataException(
                $"Decoded texture data is too short: expected {expectedLength} bytes, got {rgba.Length}.");

        byte[] scanlines = new byte[checked((stride + 1) * height)];
        int destination = 0;
        for (int y = 0; y < height; y++)
        {
            scanlines[destination++] = 0; // PNG filter: None
            int sourceY = flipVertically ? height - 1 - y : y;
            rgba.Slice(sourceY * stride, stride).CopyTo(scanlines.AsSpan(destination, stride));
            destination += stride;
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(scanlines);
            compressed = compressedStream.ToArray();
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream output = File.Create(path);
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), checked((uint)height));
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        if (type.Length != 4)
            throw new ArgumentException("PNG chunk type must be exactly four characters.", nameof(type));

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, checked((uint)data.Length));
        output.Write(lengthBytes);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type.AsSpan(), typeBytes);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = 0xFFFFFFFFu;
        UpdateCrc(ref crc, typeBytes);
        UpdateCrc(ref crc, data);
        crc = ~crc;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1u) != 0 ? 0xEDB88320u : 0u);
        }
    }
}
