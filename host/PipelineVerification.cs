using System.Net.Sockets;

namespace UsbPenDisplay;

static class PipelineVerification
{
    public static int Run(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (int window in new[] { 1, 2 })
            {
                using var stop = new CancellationTokenSource();
                Rectangle screen = Screen.PrimaryScreen!.Bounds;
                int w = 128, h = (int)Math.Round(screen.Height * w / (double)screen.Width), generation = 0;
                byte[] state = new byte[w * h * 4]; int[] versions = new int[((w + 63) / 64) * ((h + 63) / 64)];
                CapturedImage Source()
                {
                    generation++; int pixel = (generation * 137) % (w * h), offset = pixel * 4;
                    state[offset] ^= (byte)(generation | 1); state[offset + 3] = 255;
                    if (generation % 20 == 0) Array.Fill(versions, 0);
                    versions[(pixel / w / 64) * ((w + 63) / 64) + pixel % w / 64] = generation % 20 + 1;
                    return new((byte[])state.Clone(), (int[])versions.Clone(), generation / 20, state.Length, versions.Length);
                }
                using Engine engine = new(screen, w, 120, false, directory, stop.Token, window, false, true, Source, 37501, 37502);
                engine.Start();
                using (TcpClient client = Connect(engine.Token, window, out NetworkStream stream))
                {
                    byte[] replica = new byte[w * h * 4]; long previous = 0;
                    List<long> pending = new();
                    for (int i = 0; i < window; i++) pending.Add(ReadFrame(stream, replica, w, ref previous));
                    Thread.Sleep(80);
                    Require(!stream.DataAvailable, "sender exceeded bounded window");
                    foreach (long seq in pending) Ack(stream, seq);
                    for (int i = 0; i < 12; i++)
                    {
                        long seq = ReadFrame(stream, replica, w, ref previous); Thread.Sleep(15); Ack(stream, seq);
                    }
                    long next = ReadFrame(stream, replica, w, ref previous);
                    Ack(stream, next + 1000); // receiver corruption must force a closed connection
                    client.ReceiveTimeout = 3000;
                    byte[] drain = new byte[65536]; bool closed = false;
                    try { while (stream.Read(drain) > 0) { } closed = true; }
                    catch (IOException e) when (e.InnerException is not SocketException { SocketErrorCode: SocketError.TimedOut }) { closed = true; }
                    Require(closed, "invalid ACK did not terminate connection");
                }
                using (TcpClient client = Connect(engine.Token, window, out NetworkStream stream))
                {
                    byte[] replica = new byte[w * h * 4]; long previous = 0;
                    Require(ReadFrame(stream, replica, w, ref previous) == 1, "reconnect must restart with full frame sequence 1");
                    Ack(stream, 1);
                }
                stop.Cancel();
                Console.WriteLine($"PASS pipeline window={window}: exact deltas, skipped captures, epoch changes, credit bound, bad ACK rejection, reconnect full-state reset");
            }
            VerifyIdle(directory);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void VerifyIdle(string directory)
    {
        using var stop = new CancellationTokenSource();
        Rectangle screen = Screen.PrimaryScreen!.Bounds; int w = 128, h = (int)Math.Round(screen.Height * w / (double)screen.Width);
        int calls = 0; byte[] pixels = new byte[w * h * 4];
        using Engine engine = new(screen,w,60,false,directory,stop.Token,2,false,true,
            () => Interlocked.Increment(ref calls)==1 ? new CapturedImage(pixels,null,0,pixels.Length,1) : null,37501,37502);
        engine.Start();
        void Wait(Func<bool> condition) { if(!SpinWait.SpinUntil(condition,2500))throw new Exception("Idle recovery timed out"); }
        using(TcpClient first=Connect(engine.Token,2,out NetworkStream stream)) {
            long previous=0;byte[] replica=new byte[pixels.Length];ReadFrame(stream,replica,w,ref previous);Ack(stream,1);
            Wait(()=>engine.IsVideoReady);
        }
        Wait(()=>!engine.IsVideoReady); // no new capture, no outstanding ACK: peer EOF must still be detected
        using(TcpClient second=Connect(engine.Token,2,out NetworkStream stream)) {
            long previous=0;byte[] replica=new byte[pixels.Length];Require(ReadFrame(stream,replica,w,ref previous)==1,"idle reconnect full reset");Ack(stream,1);
            Wait(()=>engine.IsVideoReady);engine.ResetConnections();
            Require(!engine.IsVideoReady&&!engine.IsPenContact,"explicit reset must invalidate presentation and release pen state");
            second.ReceiveTimeout=2500;Require(stream.ReadByte()==-1,"explicit reset must close idle socket");
        }
        using(TcpClient third=Connect(engine.Token,2,out NetworkStream stream)) {
            long previous=0;byte[] replica=new byte[pixels.Length];Require(ReadFrame(stream,replica,w,ref previous)==1,"explicit idle reset blocked new connection");Ack(stream,1);
        }
        stop.Cancel();Console.WriteLine("PASS idle pipeline: no new frames, peer EOF, explicit cancellation, new full frame after both reconnects");
    }
    static TcpClient Connect(string token, int window, out NetworkStream stream)
    {
        TcpClient client = new() { NoDelay = true, ReceiveTimeout = 5000 }; client.Connect("127.0.0.1", 37501); stream = client.GetStream();
        using MemoryStream hello = new(); Wire.Int(hello, Wire.Magic); Wire.Int(hello, 2); Wire.Int(hello, token.Length); hello.Write(System.Text.Encoding.ASCII.GetBytes(token)); stream.Write(hello.ToArray());
        Require(Wire.ReadInt(stream) == Wire.Magic && Wire.ReadInt(stream) == 2, "server version");
        for (int i = 0; i < 5; i++) Wire.ReadInt(stream);
        Require(Wire.ReadInt(stream) == window, "window negotiation");
        return client;
    }
    static long ReadFrame(Stream stream, byte[] replica, int width, ref long previous)
    {
        Require(Wire.ReadInt(stream) == Wire.Frame, "frame marker");
        long seq = Wire.ReadLong(stream), baseline = Wire.ReadLong(stream);
        int flags = Wire.ReadInt(stream), tiles = Wire.ReadInt(stream), rawSize = Wire.ReadInt(stream), codec = Wire.ReadInt(stream), size = Wire.ReadInt(stream), hash = Wire.ReadInt(stream);
        Require(seq == previous + 1 && (((flags & 1) != 0 && baseline == 0) || baseline == previous), "delta dependency");
        byte[] encoded = new byte[size]; stream.ReadExactly(encoded); byte[] raw;
        if (codec == 2) { raw = new byte[rawSize]; Require(K4os.Compression.LZ4.LZ4Codec.Decode(encoded, 0, size, raw, 0, rawSize) == rawSize, "decode"); }
        else { Require(codec == 0, "codec"); raw = encoded; }
        using MemoryStream packet = new(raw);
        for (int i = 0; i < tiles; i++)
        {
            int x = Wire.ReadInt(packet), y = Wire.ReadInt(packet), w = Wire.ReadInt(packet), h = Wire.ReadInt(packet);
            for (int row = 0; row < h; row++) packet.ReadExactly(replica.AsSpan(((y + row) * width + x) * 4, w * 4));
        }
        Require(packet.Position == packet.Length && Wire.PixelHash(replica) == hash, "reconstructed pixels differ from latest sender state");
        previous = seq; return seq;
    }
    static void Ack(Stream stream, long sequence)
    {
        using MemoryStream ack = new(); Wire.Int(ack, Wire.Ack); Wire.Long(ack, sequence);
        for (int i = 0; i < 7; i++) Wire.Int(ack, 0); stream.Write(ack.ToArray());
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
