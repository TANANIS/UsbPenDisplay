using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UsbPenDisplay;

// Benchmark-only source. Does not capture, focus, or inject into a Windows app.
// Capture correctness/performance is measured separately on the actual desktop.
sealed class SyntheticSource : IDisposable
{
    readonly int width, height;
    readonly byte[] pixels;
    readonly GCHandle pinned;
    readonly Bitmap bitmap;
    readonly Graphics graphics;
    long started;
    string phase = "";
    int previousStep = -1;
    public bool Started => Interlocked.Read(ref started) != 0;
    public double Seconds => Started ? Stopwatch.GetElapsedTime(Interlocked.Read(ref started)).TotalSeconds : 0;
    public SyntheticSource(int width, int height)
    {
        this.width = width; this.height = height; pixels = new byte[width * height * 4];
        pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, pinned.AddrOfPinnedObject());
        graphics = Graphics.FromImage(bitmap); graphics.SmoothingMode = SmoothingMode.None;
    }
    public CapturedImage? Next()
    {
        Interlocked.CompareExchange(ref started, Stopwatch.GetTimestamp(), 0);
        double t = Seconds;
        string nextPhase = t < 3 ? "settle" : t < 10 ? "stroke" : t < 22 ? "transform" : "idle";
        int step = nextPhase == "transform" ? (int)(t * 60) : nextPhase == "stroke" ? (int)(t * 30) : (int)(t * 10);
        if (nextPhase == phase && step == previousStep) return null;
        previousStep = step;
        if (phase != nextPhase) { phase = nextPhase; DiagnosticCanvas.SetPhase(phase); Console.WriteLine("BENCH_PHASE " + phase); }
        long process = Stopwatch.GetTimestamp();
        graphics.Clear(Color.FromArgb(24, 28, 36));
        if (phase == "transform")
        {
            GraphicsState saved = graphics.Save();
            graphics.TranslateTransform(width / 2, height / 2); graphics.RotateTransform((float)(t * 45));
            float scale = 1.1f + .5f * (float)Math.Sin(t * 2); graphics.ScaleTransform(scale, scale);
            using SolidBrush a = new(Color.FromArgb(45, 72, 110)), b = new(Color.FromArgb(130, 95, 53));
            for (int y = -height * 2; y < height * 2; y += 64)
            for (int x = -width * 2; x < width * 2; x += 64)
                graphics.FillRectangle(((x / 64 + y / 64) & 1) == 0 ? a : b, x, y, 64, 64);
            graphics.Restore(saved);
        }
        if (phase == "stroke")
        {
            int count = (int)((t % 2) / 2 * 60);
            for (int i = 1; i <= count; i++)
            {
                float a = (i - 1) / 60f, b = i / 60f;
                using Pen p = new(Color.LightGreen, 1 + b * 20);
                graphics.DrawLine(p, width * (.2f + .6f * a), height * (.45f + .12f * (float)Math.Sin(a * 6.28)),
                    width * (.2f + .6f * b), height * (.45f + .12f * (float)Math.Sin(b * 6.28)));
            }
        }
        graphics.FillRectangle(Brushes.Red, 24, height - 60, 32, 32);
        graphics.FillRectangle(Brushes.Lime, 64, height - 60, 32, 32);
        graphics.FillRectangle(Brushes.Blue, 104, height - 60, 32, 32);
        // Small deterministic clock stripe keeps idle at 10 updates/s.
        graphics.FillRectangle(Brushes.Cyan, width - 160, 24, 8 + ((int)(t * 10) % 140), 12);
        graphics.Flush();
        return new CapturedImage((byte[])pixels.Clone(), null, 0, 0, ((width + 63) / 64) * ((height + 63) / 64),
            0, Stopwatch.GetElapsedTime(process).TotalMilliseconds);
    }
    public void Dispose() { graphics.Dispose(); bitmap.Dispose(); pinned.Free(); }
}
