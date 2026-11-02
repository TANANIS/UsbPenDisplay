using System.Buffers.Binary;
using System.IO.Compression;

namespace UsbPenDisplay;

static class Wire
{
    public const int Magic = 0x55504431, Frame = 0x46524d31, Ack = 0x41434b31, Pen = 0x50454e31;
    public static int ReadInt(Stream s) { Span<byte> b = stackalloc byte[4]; s.ReadExactly(b); return BinaryPrimitives.ReadInt32BigEndian(b); }
    public static long ReadLong(Stream s) { Span<byte> b = stackalloc byte[8]; s.ReadExactly(b); return BinaryPrimitives.ReadInt64BigEndian(b); }
    public static float ReadFloat(Stream s) => BitConverter.Int32BitsToSingle(ReadInt(s));
    public static void Int(Stream s, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); s.Write(b); }
    public static void Long(Stream s, long value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); s.Write(b); }
    public static byte[] Compress(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream z = new(output, CompressionLevel.Fastest, true)) z.Write(raw);
        return output.ToArray();
    }
    public static byte[] CompressLz4(byte[] raw)
    {
        byte[] output = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(raw.Length)];
        int length = K4os.Compression.LZ4.LZ4Codec.Encode(raw, 0, raw.Length, output, 0, output.Length);
        if (length <= 0) throw new InvalidDataException("LZ4 encode failed");
        Array.Resize(ref output, length); return output;
    }
    public static int PixelHash(byte[] pixels)
    {
        uint hash = 2166136261;
        foreach (byte value in pixels) hash = unchecked((hash ^ value) * 16777619);
        return unchecked((int)hash);
    }
    public static bool Authenticate(Stream stream, string token)
    {
        if (ReadInt(stream) != Magic || ReadInt(stream) != 2) return false;
        int length = ReadInt(stream);
        if (length != token.Length || length > 128) return false;
        byte[] supplied = new byte[length]; stream.ReadExactly(supplied);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(supplied, System.Text.Encoding.ASCII.GetBytes(token));
    }
}

record TilePacket(byte[] Bytes, int Tiles, bool KeyFrame);
static class TileEncoder
{
    public const int TileSize = 64;
    public static TilePacket Build(byte[] pixels, byte[]? baseline, int width, int height, int[]? versions = null, int[]? baselineVersions = null)
    {
        using MemoryStream raw = new(); int count = 0;
        for (int y = 0; y < height; y += TileSize)
        for (int x = 0; x < width; x += TileSize)
        {
            int index = (y / TileSize) * ((width + TileSize - 1) / TileSize) + x / TileSize;
            if (baseline != null && versions != null && baselineVersions != null && versions[index] == baselineVersions[index]) continue;
            int w = Math.Min(TileSize, width - x), h = Math.Min(TileSize, height - y);
            bool changed = baseline == null;
            if (!changed)
                for (int row = 0; row < h; row++)
                {
                    int offset = ((y + row) * width + x) * 4;
                    if (!pixels.AsSpan(offset, w * 4).SequenceEqual(baseline!.AsSpan(offset, w * 4))) { changed = true; break; }
                }
            if (!changed) continue;
            Wire.Int(raw, x); Wire.Int(raw, y); Wire.Int(raw, w); Wire.Int(raw, h);
            for (int row = 0; row < h; row++) raw.Write(pixels, ((y + row) * width + x) * 4, w * 4);
            count++;
        }
        return new TilePacket(raw.ToArray(), count, baseline == null);
    }
}
