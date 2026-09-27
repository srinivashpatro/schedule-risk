using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>The little of PNG the exports need: an image's size, and writing a plain RGB image (the logo mark).</summary>
public static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Width and height from the IHDR chunk.</summary>
    public static (int Width, int Height) Size(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(Signature) || Encoding.ASCII.GetString(png, 12, 4) != "IHDR")
            throw new InvalidDataException("Not a PNG image.");
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }

    /// <summary>An 8-bit RGB image; <paramref name="rgb"/> holds 3 bytes per pixel, row by row.</summary>
    public static byte[] Encode(int width, int height, byte[] rgb)
    {
        var raw = new byte[height * (1 + 3 * width)];
        for (int y = 0; y < height; y++) Array.Copy(rgb, y * 3 * width, raw, y * (1 + 3 * width) + 1, 3 * width);
        var ms = new MemoryStream();
        ms.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;  // bits per channel
        ihdr[9] = 2;  // truecolour
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", Zlib(raw));
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>A block of one colour, given as six hex digits.</summary>
    public static byte[] Solid(int width, int height, string hex)
    {
        byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex[2..4], 16), b = Convert.ToByte(hex[4..6], 16);
        var rgb = new byte[3 * width * height];
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b; }
        return Encode(width, height, rgb);
    }

    /// <summary>zlib-wrapped deflate, the form PNG and PDF's FlateDecode both use.</summary>
    public static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
        s.Write(len);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
