using System.IO.Compression;

namespace UsbPenDisplay;

static class Verification
{
    public static int Run(string vectorDirectory = "")
    {
        try
        {
            int width = 131, height = 75;
            byte[] initial = new byte[width * height * 4];
            new Random(7).NextBytes(initial);
            byte[] replica = new byte[initial.Length];
            var first = TileEncoder.Build(initial, null, width, height);
            Require(first.KeyFrame && first.Tiles == 6, "edge tiles on full frame");
            Apply(first, replica, width);
            Require(replica.AsSpan().SequenceEqual(initial), "full frame reconstructs exactly");
            Require(TileEncoder.Build(initial, initial, width, height).Tiles == 0, "static frame sends no tiles");
            byte[] latest = (byte[])initial.Clone();
            latest[(74 * width + 130) * 4] ^= 0xff;
            latest[0] ^= 0x55;
            var delta = TileEncoder.Build(latest, initial, width, height);
            Require(delta.Tiles == 2 && !delta.KeyFrame, "two changed edge regions");
            Apply(delta, replica, width);
            Require(replica.AsSpan().SequenceEqual(latest), "delta preserves all pixels");
            // Intermediate frames are intentionally omitted; comparison is always
            // against the last ACKed baseline, so their surviving changes remain.
            byte[] newest = (byte[])latest.Clone(); newest[1000] ^= 0xaa;
            replica = (byte[])initial.Clone();
            Apply(TileEncoder.Build(newest, initial, width, height), replica, width);
            Require(replica.AsSpan().SequenceEqual(newest), "skipped captures do not lose persistent edits");
            byte[] compressed = Wire.Compress(first.Bytes);
            using var zipped = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var unzipped = new MemoryStream(); zipped.CopyTo(unzipped);
            Require(unzipped.ToArray().AsSpan().SequenceEqual(first.Bytes), "zlib roundtrip");
            byte[] lz4 = Wire.CompressLz4(first.Bytes), decoded = new byte[first.Bytes.Length];
            Require(K4os.Compression.LZ4.LZ4Codec.Decode(lz4, 0, lz4.Length, decoded, 0, decoded.Length) == decoded.Length &&
                decoded.AsSpan().SequenceEqual(first.Bytes), "LZ4 roundtrip");
            if (vectorDirectory.Length != 0)
            {
                Directory.CreateDirectory(vectorDirectory); Random random = new(123);
                foreach (int size in new[] { 1, 4, 7, 15, 64, 257, 65536, 1280 * 720 * 4 })
                for (int pattern = 0; pattern < 3; pattern++)
                {
                    byte[] raw = new byte[size];
                    if (pattern == 0) random.NextBytes(raw);
                    if (pattern == 1) Array.Fill(raw, (byte)42);
                    if (pattern == 2) for (int p = 0; p < size; p++) raw[p] = (byte)(p / 19 + p % 7);
                    byte[] encoded = Wire.CompressLz4(raw);
                    using FileStream vector = File.Create(Path.Combine(vectorDirectory, $"{size}-{pattern}.bin"));
                    Wire.Int(vector, raw.Length); Wire.Int(vector, encoded.Length); vector.Write(encoded); vector.Write(raw);
                }
            }
            using var auth = new MemoryStream(); Wire.Int(auth, Wire.Magic); Wire.Int(auth, 2); Wire.Int(auth, 4); auth.Write("ABCD"u8); auth.Position = 0;
            Require(Wire.Authenticate(auth, "ABCD"), "session token accepted"); auth.Position = 0;
            Require(!Wire.Authenticate(auth, "ABCE"), "wrong token rejected");
            using var pen = new PenInput(new Rectangle(0, 0, 100, 100)); // creates device; injects nothing
            Console.WriteLine("PASS: exact tiles, edge regions, latest-state deltas, zlib/LZ4, auth, native pen layout/device creation.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Require(bool ok, string message) { if (!ok) throw new Exception("Verification failed: " + message); }
    static void Apply(TilePacket packet, byte[] destination, int width)
    {
        using var s = new MemoryStream(packet.Bytes);
        for (int i = 0; i < packet.Tiles; i++)
        {
            int x = Wire.ReadInt(s), y = Wire.ReadInt(s), w = Wire.ReadInt(s), h = Wire.ReadInt(s);
            for (int row = 0; row < h; row++) s.ReadExactly(destination.AsSpan(((y + row) * width + x) * 4, w * 4));
        }
        Require(s.Position == s.Length, "tile packet consumed exactly");
    }
}
