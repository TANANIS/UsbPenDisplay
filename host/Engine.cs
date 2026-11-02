using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UsbPenDisplay;

record CapturedImage(byte[] Pixels, int[]? Versions, int Epoch, int ReadbackBytes, int DirtyTiles, double WaitMs = 0, double ProcessMs = 0);
record Snapshot(CapturedImage Image, long Sequence, long Started, double CaptureMs);
record PendingFrame(long Sequence, long Base, Snapshot Snapshot, bool Full, string Phase,
    double CompareMs, double CompressMs, long SendStart, int Tiles, int Bytes)
{
    public double SendMs;
}

sealed class Engine : IDisposable
{
    readonly Rectangle primary;
    readonly int width, height, fps, window;
    readonly bool diagnostic, optimized, verifyFrames;
    readonly CancellationToken stopping;
    readonly TcpListener video, input;
    readonly Func<CapturedImage?>? testSource;
    readonly StreamWriter statsCsv, framesCsv;
    readonly PenInput pen;
    readonly List<Task> workers = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly object penLock = new();
    readonly object connectionsLock = new();
    TcpClient? activeVideo, activeInput;
    CancellationTokenSource? activeVideoLifetime;
    int transportEpoch;
    volatile bool hasPresented;
    Snapshot? latest;
    long captured, sent, acknowledged, bytes;
    double captureMs, ackMs, applyMs, swapMs, inputRttMs, injectionMs;
    int pendingCount, maxPending;
    int diagnosticRequest;
    long videoConnections;
    volatile bool videoConnected, inputConnected;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public bool IsVideoReady => videoConnected && hasPresented;
    public bool IsReady => IsVideoReady && inputConnected;
    public long VideoConnections => Interlocked.Read(ref videoConnections);
    public bool IsPenContact { get { lock (penLock) return pen.IsContact; } }

    public Engine(Rectangle primary, int maxWidth, int fps, bool diagnostic, string logs, CancellationToken stop,
        int window = 2, bool optimized = true, bool verifyFrames = false, Func<CapturedImage?>? testSource = null, int videoPort = 37491, int inputPort = 37492)
    {
        this.primary = primary; this.fps = fps; this.diagnostic = diagnostic; stopping = stop;
        this.window = window; this.optimized = optimized; this.verifyFrames = verifyFrames;
        this.testSource = testSource; video = new(IPAddress.Loopback, videoPort); input = new(IPAddress.Loopback, inputPort);
        width = Math.Min(primary.Width, maxWidth); height = Math.Max(1, (int)Math.Round(primary.Height * width / (double)primary.Width));
        pen = new(primary);
        string file = Path.Combine(logs, $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}-w{window}.csv");
        statsCsv = new StreamWriter(file) { AutoFlush = true };
        framesCsv = new StreamWriter(Path.ChangeExtension(file, ".frames.csv")) { AutoFlush = true };
        statsCsv.WriteLine("utc,phase,video,input,captured,sent,acked,wire_bytes,pending,max_pending,capture_ms,capture_to_ack_ms,apply_ms,swap_ms,input_rtt_ms,injection_ms,pen_events,pen_errors");
        framesCsv.WriteLine("sequence,base,phase,keyframe,verified,host_elapsed_ms,capture_ms,capture_wait_ms,capture_process_ms,compare_ms,compress_ms,send_ms,capture_to_ack_ms,send_to_ack_ms,receive_ms,decode_ms,upload_ms,draw_ms,swap_ms,render_queue_ms,verify_ms,tiles,readback_bytes,wire_bytes,pending,input_rtt_ms,injection_ms");
        Console.WriteLine($"CONFIG mirror={width}x{height} fps={fps} window={window} capture={(testSource != null ? "synthetic-NOT-desktop" : optimized ? "gpu" : "legacy")} verify={verifyFrames} logs={file}");
    }
    public void Start()
    {
        video.Start(1); input.Start(1);
        workers.Add(Task.Run(CaptureLoop)); workers.Add(Task.Run(VideoLoop));
        workers.Add(Task.Run(InputLoop)); workers.Add(Task.Run(StatsLoop));
    }
    public void RequestDiagnostic() => Interlocked.Exchange(ref diagnosticRequest, 1);
    public void ResetConnections()
    {
        lock (penLock) { transportEpoch++; hasPresented = false; videoConnected = false; inputConnected = false; pen.Reset(); }
        lock (connectionsLock) {
            try { activeVideoLifetime?.Cancel(); } catch (ObjectDisposedException) { }
            activeVideo?.Dispose(); activeInput?.Dispose();
        }
    }
    void CaptureLoop()
    {
        DxgiCapture? dxgi = null;
        bool gpuActive = optimized;
        try { if (testSource == null) { dxgi = new(primary) { ValidateGpu = verifyFrames }; Console.WriteLine("CAPTURE DXGI initialized"); } }
        catch (Exception e) { Console.WriteLine("CAPTURE GDI fallback: " + e.Message); }
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (!videoConnected) { Thread.Sleep(20); continue; }
                if (Screen.PrimaryScreen!.Bounds != primary) throw new InvalidOperationException("Primary screen changed; restart host to remap safely.");
                long started = Stopwatch.GetTimestamp();
                try
                {
                    CapturedImage? image;
                    if (testSource != null) image = testSource();
                    else if (dxgi == null) image = new(CaptureGdi(), null, 0, primary.Width * primary.Height * 4, ((width + 63) / 64) * ((height + 63) / 64));
                    else if (gpuActive) image = dxgi.CaptureOptimized(width, height);
                    else { byte[]? pixels = dxgi.Capture(width, height); image = pixels == null ? null : new(pixels, null, 0, primary.Width * primary.Height * 4, ((width + 63) / 64) * ((height + 63) / 64), dxgi.LastWaitMs, dxgi.LastProcessMs); }
                    if (image != null)
                    {
                        captureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        Volatile.Write(ref latest, new Snapshot(image, Interlocked.Increment(ref captured), started, captureMs));
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("CAPTURE_RESTART " + e.Message); dxgi?.Dispose(); dxgi = null;
                    if (gpuActive) { gpuActive = false; Console.WriteLine("GPU_DISABLED: falling back to legacy DXGI capture; inspect the preceding failure."); }
                    Thread.Sleep(250);
                    try { dxgi = new(primary) { ValidateGpu = verifyFrames }; } catch (Exception retry) { Console.WriteLine("CAPTURE GDI fallback: " + retry.Message); }
                }
                int delay = (int)(1000.0 / fps - Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (delay > 0) Thread.Sleep(delay);
            }
        }
        catch (Exception e) { Console.WriteLine("CAPTURE_STOP " + e.Message); }
        finally { dxgi?.Dispose(); }
    }
    byte[] CaptureGdi()
    {
        using Bitmap native = new(primary.Width, primary.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(native)) g.CopyFromScreen(primary.Location, Point.Empty, primary.Size, CopyPixelOperation.SourceCopy);
        using Bitmap resized = new(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(resized)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.DrawImage(native, new Rectangle(0, 0, width, height)); }
        BitmapData bits = resized.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] output = new byte[width * height * 4];
            for (int y = 0; y < height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, output, y * width * 4, width * 4);
            for (int i = 3; i < output.Length; i += 4) output[i] = 255;
            return output;
        }
        finally { resized.UnlockBits(bits); }
    }
    void VideoLoop()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = video.AcceptTcpClient();
                int epoch = Volatile.Read(ref transportEpoch);
                lock (connectionsLock) activeVideo = client;
                using var stopRegistration = stopping.Register(client.Dispose);
                client.NoDelay = true; client.SendBufferSize = 65536; client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                if (!Wire.Authenticate(stream, Token)) { Console.WriteLine("VIDEO rejected session"); continue; }
                client.ReceiveTimeout = 0;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                lock (connectionsLock) activeVideoLifetime = lifetime;
                using var credits = new SemaphoreSlim(window, window);
                using var pending = new BlockingCollection<PendingFrame>(window);
                using var closeRegistration = lifetime.Token.Register(client.Dispose);
                using MemoryStream hello = new();
                foreach (int value in new[] { Wire.Magic, 2, width, height, primary.Width, primary.Height, 64, window }) Wire.Int(hello, value);
                stream.Write(hello.ToArray());
                lock (penLock) { if (epoch != transportEpoch) throw new IOException("Video session invalidated"); hasPresented = false; videoConnected = true; }
                Interlocked.Increment(ref videoConnections);
                Console.WriteLine("VIDEO connected, bounded window=" + window);
                Task ackTask = Task.Run(() =>
                {
                    try
                    {
                        foreach (PendingFrame frame in pending.GetConsumingEnumerable(lifetime.Token))
                        {
                            byte[] reply = new byte[40];
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(5000);
                            stream.ReadExactlyAsync(reply, deadline.Token).AsTask().GetAwaiter().GetResult();
                            using MemoryStream ack = new(reply);
                            if (Wire.ReadInt(ack) != Wire.Ack || Wire.ReadLong(ack) != frame.Sequence) throw new InvalidDataException("ACK sequence mismatch");
                            double receive = Wire.ReadInt(ack) / 1000.0, decode = Wire.ReadInt(ack) / 1000.0,
                                upload = Wire.ReadInt(ack) / 1000.0, draw = Wire.ReadInt(ack) / 1000.0,
                                swap = Wire.ReadInt(ack) / 1000.0, queue = Wire.ReadInt(ack) / 1000.0,
                                verify = Wire.ReadInt(ack) / 1000.0;
                            ackMs = Stopwatch.GetElapsedTime(frame.Snapshot.Started).TotalMilliseconds;
                            applyMs = decode + upload; swapMs = swap;
                            double sendToAck = Stopwatch.GetElapsedTime(frame.SendStart).TotalMilliseconds;
                            int inFlight = Interlocked.Decrement(ref pendingCount);
                            framesCsv.WriteLine(FormattableString.Invariant($"{frame.Sequence},{frame.Base},{frame.Phase},{frame.Full},{frame.Full || verifyFrames},{elapsed.Elapsed.TotalMilliseconds:F3},{frame.Snapshot.CaptureMs:F3},{frame.Snapshot.Image.WaitMs:F3},{frame.Snapshot.Image.ProcessMs:F3},{frame.CompareMs:F3},{frame.CompressMs:F3},{frame.SendMs:F3},{ackMs:F3},{sendToAck:F3},{receive:F3},{decode:F3},{upload:F3},{draw:F3},{swap:F3},{queue:F3},{verify:F3},{frame.Tiles},{frame.Snapshot.Image.ReadbackBytes},{frame.Bytes},{inFlight},{inputRttMs:F3},{injectionMs:F3}"));
                            Interlocked.Increment(ref acknowledged); credits.Release();
                            lock (penLock) { if (epoch != transportEpoch) throw new IOException("Stale frame ACK"); hasPresented = true; }
                        }
                    }
                    catch (Exception e) { lock (penLock) { hasPresented = false; pen.Reset(); } if (!stopping.IsCancellationRequested) Console.WriteLine("ACK_STOP " + e.Message); lifetime.Cancel(); throw; }
                });
                try
                {
                    Snapshot? baseline = null; long previousCapture = -1, sequence = 0; long lastFull = Stopwatch.GetTimestamp(), lastPeerCheck = 0;
                    while (!lifetime.IsCancellationRequested)
                    {
                        credits.Wait(lifetime.Token); // choose the newest snapshot AFTER obtaining a credit
                        // No ACK reader is active when nothing is in flight. Detect peer EOF
                        // even when a static desktop produces no next frame to write.
                        if (Volatile.Read(ref pendingCount) == 0 && Stopwatch.GetElapsedTime(lastPeerCheck).TotalMilliseconds > 250) {
                            lastPeerCheck = Stopwatch.GetTimestamp();
                            if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0) throw new EndOfStreamException("Idle video peer closed");
                        }
                        Snapshot? frame = Volatile.Read(ref latest);
                        bool requestTest = Interlocked.Exchange(ref diagnosticRequest, 0) != 0;
                        if (frame == null || (frame.Sequence == previousCapture && !requestTest)) { credits.Release(); Thread.Sleep(1); continue; }
                        previousCapture = frame.Sequence;
                        bool full = baseline == null || requestTest || Stopwatch.GetElapsedTime(lastFull).TotalSeconds > 30;
                        if (full) lastFull = Stopwatch.GetTimestamp();
                        long compareStart = Stopwatch.GetTimestamp();
                        TilePacket packet = TileEncoder.Build(frame.Image.Pixels, full ? null : baseline!.Image.Pixels, width, height,
                            frame.Image.Versions, !full && frame.Image.Epoch == baseline!.Image.Epoch ? baseline.Image.Versions : null);
                        double compare = Stopwatch.GetElapsedTime(compareStart).TotalMilliseconds;
                        if (packet.Tiles == 0) { credits.Release(); continue; }
                        long compressStart = Stopwatch.GetTimestamp(); byte[] compressed = Wire.CompressLz4(packet.Bytes);
                        bool useCompressed = compressed.Length + 32 < packet.Bytes.Length;
                        byte[] payload = useCompressed ? compressed : packet.Bytes;
                        double compress = Stopwatch.GetElapsedTime(compressStart).TotalMilliseconds;
                        long baseSequence = full ? 0 : sequence;
                        int flags = (full ? 1 : 0) | (requestTest ? 2 : 0) | (verifyFrames ? 4 : 0);
                        using MemoryStream update = new(44 + payload.Length);
                        Wire.Int(update, Wire.Frame); Wire.Long(update, ++sequence); Wire.Long(update, baseSequence);
                        foreach (int value in new[] { flags, packet.Tiles, packet.Bytes.Length, useCompressed ? 2 : 0, payload.Length,
                            full || verifyFrames ? Wire.PixelHash(frame.Image.Pixels) : 0 }) Wire.Int(update, value);
                        update.Write(payload);
                        long sendStart = Stopwatch.GetTimestamp();
                        PendingFrame pendingFrame = new(sequence, baseSequence, frame, full, DiagnosticCanvas.Phase,
                            compare, compress, sendStart, packet.Tiles, (int)update.Length);
                        int count = Interlocked.Increment(ref pendingCount); maxPending = Math.Max(maxPending, count);
                        stream.Write(update.GetBuffer(), 0, (int)update.Length);
                        pendingFrame.SendMs = Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds;
                        pending.Add(pendingFrame, lifetime.Token);
                        baseline = frame; // ordered TCP + explicit base sequence; never discard an in-flight delta
                        Interlocked.Increment(ref sent); Interlocked.Add(ref bytes, update.Length);
                    }
                }
                finally
                {
                    pending.CompleteAdding(); lifetime.Cancel(); client.Dispose();
                    try { ackTask.GetAwaiter().GetResult(); } catch { }
                }
            }
            catch (Exception e) { if (!stopping.IsCancellationRequested) Console.WriteLine("VIDEO disconnected: " + e.Message); }
            finally { lock (connectionsLock) { activeVideo = null; activeVideoLifetime = null; } lock (penLock) { hasPresented = false; videoConnected = false; pen.Reset(); } Interlocked.Exchange(ref pendingCount, 0); }
        }
    }
    void InputLoop()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = input.AcceptTcpClient(); using var registration = stopping.Register(client.Dispose);
                int epoch = Volatile.Read(ref transportEpoch);
                lock (connectionsLock) activeInput = client;
                client.NoDelay = true; client.ReceiveTimeout = 4500; client.SendTimeout = 3000;
                using NetworkStream stream = client.GetStream(); if (!Wire.Authenticate(stream, Token)) continue;
                lock (penLock) { if (epoch != transportEpoch) throw new IOException("Input session invalidated"); inputConnected = true; }
                Console.WriteLine("INPUT connected");
                while (!stopping.IsCancellationRequested)
                {
                    if (Wire.ReadInt(stream) != Wire.Pen) throw new InvalidDataException("Bad pen packet");
                    int kind = Wire.ReadInt(stream); float x = Wire.ReadFloat(stream), y = Wire.ReadFloat(stream), pressure = Wire.ReadFloat(stream);
                    float tx = Wire.ReadFloat(stream), ty = Wire.ReadFloat(stream); int buttons = Wire.ReadInt(stream), tool = Wire.ReadInt(stream);
                    long seq = Wire.ReadLong(stream), eventTime = Wire.ReadLong(stream), sentNs = Wire.ReadLong(stream);
                    inputRttMs = Wire.ReadInt(stream) / 1000.0; int senderQueueUs = Wire.ReadInt(stream);
                    long injectStart = Stopwatch.GetTimestamp();
                    lock (penLock)
                        if (epoch == transportEpoch && kind != 0 && inputConnected && videoConnected && hasPresented && (!diagnostic || DiagnosticCanvas.IsForeground))
                            pen.Send(kind, x, y, pressure, tx, ty, buttons, tool);
                    injectionMs = Stopwatch.GetElapsedTime(injectStart).TotalMilliseconds;
                    using MemoryStream ack = new(); Wire.Int(ack, 0x50414b32); Wire.Long(ack, seq); Wire.Long(ack, sentNs); Wire.Int(ack, (int)(injectionMs * 1000));
                    stream.Write(ack.ToArray());
                    if (kind is 1 or 3 or 6) Console.WriteLine($"PEN kind={kind} pressure={pressure:F3} events={pen.Events} errors={pen.Errors} queue_us={senderQueueUs}");
                }
            }
            catch (Exception e) { if (!stopping.IsCancellationRequested) Console.WriteLine("INPUT disconnected: " + e.Message); }
            finally { lock (connectionsLock) activeInput = null; lock (penLock) { inputConnected = false; pen.Reset(); } }
        }
    }
    void StatsLoop()
    {
        long oldAck = 0, oldBytes = 0;
        while (!stopping.WaitHandle.WaitOne(2000))
        {
            long a = Interlocked.Read(ref acknowledged), b = Interlocked.Read(ref bytes);
            Console.WriteLine($"STATUS phase={DiagnosticCanvas.Phase} video={videoConnected} input={inputConnected} fps={(a - oldAck) / 2.0:F1} MB/s={(b - oldBytes) / 2000000.0:F2} capture={captureMs:F2}ms capture_ACK={ackMs:F2}ms apply={applyMs:F2}ms swap={swapMs:F2}ms pending={pendingCount}/{window} input_RTT={inputRttMs:F2}ms pen={pen.Events} errors={pen.Errors}");
            statsCsv.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:O},{DiagnosticCanvas.Phase},{videoConnected},{inputConnected},{captured},{sent},{a},{b},{pendingCount},{maxPending},{captureMs:F3},{ackMs:F3},{applyMs:F3},{swapMs:F3},{inputRttMs:F3},{injectionMs:F3},{pen.Events},{pen.Errors}"));
            oldAck = a; oldBytes = b;
        }
    }
    public void Dispose()
    {
        video.Stop(); input.Stop(); try { Task.WaitAll(workers.ToArray(), 6000); } catch { }
        statsCsv.Dispose(); framesCsv.Dispose(); pen.Dispose();
    }
}
