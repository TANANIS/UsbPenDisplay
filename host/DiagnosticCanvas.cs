using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsbPenDisplay;

sealed class DiagnosticCanvas : Form
{
    static nint current;
    readonly CancellationTokenSource shutdown;
    readonly List<(Point point, uint pressure, bool start)> points = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    long events;
    uint minPressure = 1024, maxPressure;
    bool contact;
    bool motion;
    public event Action? RequestStroke;
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly bool automatic;
    int lastStroke = -1;
    public static string Phase { get; private set; } = "manual";
    public static void SetPhase(string value) => Phase = value;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    public static bool IsForeground => current != 0 && GetForegroundWindow() == current;
    public DiagnosticCanvas(Rectangle primary, CancellationTokenSource shutdown, bool automatic = false)
    {
        this.shutdown = shutdown;
        this.automatic = automatic;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = primary; TopMost = true; DoubleBuffered = true; BackColor = Color.FromArgb(24, 28, 36);
        KeyPreview = true; Text = "USB Pen Display — isolated input diagnostic";
        KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.T) RequestStroke?.Invoke();
            if (e.KeyCode == Keys.B) { motion = !motion; Console.WriteLine(motion ? "BENCH_START full-screen transforms" : "BENCH_STOP full-screen transforms"); }
        };
        timer.Tick += (_, _) => {
            if (shutdown.IsCancellationRequested) { Close(); return; }
            if (automatic) {
                double seconds = uptime.Elapsed.TotalSeconds;
                string phase = seconds < 3 ? "settle" : seconds < 10 ? "stroke" : seconds < 22 ? "transform" : "idle";
                if (phase != Phase) { Phase = phase; Console.WriteLine("BENCH_PHASE " + phase); }
                motion = phase == "transform";
                int stroke = (int)((seconds - 3) / 2);
                if (phase == "stroke" && stroke != lastStroke) { lastStroke = stroke; RequestStroke?.Invoke(); }
                if (seconds >= 26) { Console.WriteLine("BENCH_COMPLETE"); Close(); return; }
            }
            Invalidate();
        }; timer.Start();
        Shown += (_, _) => { current = Handle; ShowWindow(Handle, 5); Activate(); };
        FormClosed += (_, _) => { current = 0; timer.Stop(); timer.Dispose(); shutdown.Cancel(); Console.WriteLine($"CANVAS_RESULT events={events} pressure_min={minPressure} pressure_max={maxPressure}"); };
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x245 or 0x246 or 0x247)
        {
            uint id = (uint)(m.WParam.ToInt64() & 0xffff);
            if (PenInput.GetPointerPenInfo(id, out var pen))
            {
                bool down = m.Msg == 0x246, up = m.Msg == 0x247;
                if (down) contact = true;
                if (contact)
                {
                    Point point = PointToClient(new Point(pen.Info.Pixel.X, pen.Info.Pixel.Y));
                    points.Add((point, pen.Pressure, down)); events++;
                    if (!up) { minPressure = Math.Min(minPressure, pen.Pressure); maxPressure = Math.Max(maxPressure, pen.Pressure); }
                    if (down || up) Console.WriteLine($"CANVAS_PEN message={m.Msg:X} pressure={pen.Pressure} at={point}");
                    Invalidate();
                }
                if (up) contact = false;
            }
        }
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); Graphics g = e.Graphics;
        if (motion)
        {
            var state = g.Save();
            g.TranslateTransform(Width / 2, Height / 2);
            g.RotateTransform((float)(uptime.Elapsed.TotalSeconds * 45));
            float scale = 1.1f + .5f * (float)Math.Sin(uptime.Elapsed.TotalSeconds * 2);
            g.ScaleTransform(scale, scale);
            for (int y = -Height * 2; y < Height * 2; y += 100)
            for (int x = -Width; x < Width; x += 100)
            {
                using SolidBrush brush = new(((x / 100 + y / 100) & 1) == 0 ? Color.FromArgb(45, 72, 110) : Color.FromArgb(130, 95, 53));
                g.FillRectangle(brush, x, y, 100, 100);
            }
            g.Restore(state);
        }
        using Font title = new("Segoe UI", 28, FontStyle.Bold), body = new("Segoe UI", 17);
        g.DrawString("USB PEN DISPLAY · INPUT + MIRROR TEST", title, Brushes.White, 40, 30);
        g.DrawString("T: test stroke · B: full-screen pan/zoom/rotate workload · ESC: close", body, Brushes.LightGray, 42, 86);
        g.DrawString($"Events: {events}     Pressure range: {minPressure}–{maxPressure} / 1024", body, Brushes.Cyan, 42, 130);
        g.FillRectangle(Brushes.Red, 40, Height - 100, 60, 60); g.FillRectangle(Brushes.Lime, 110, Height - 100, 60, 60);
        g.FillRectangle(Brushes.Blue, 180, Height - 100, 60, 60);
        g.DrawString($"Live {uptime.Elapsed.TotalSeconds:F1}s", body, Brushes.White, Width - 220, Height - 80);
        if (automatic && Phase == "stroke")
        {
            // Deterministic small-region graphics workload, independent of focus
            // and of the separately counted Android->Windows pen test.
            int samples = (int)((uptime.Elapsed.TotalSeconds % 2) / 2 * 60);
            for (int i = 1; i <= samples; i++)
            {
                float a = (i - 1) / 60f, b = i / 60f;
                using Pen p = new(Color.LightGreen, 1 + b * 20);
                g.DrawLine(p, Width * (.2f + .6f * a), Height * (.45f + .12f * (float)Math.Sin(a * 6.28)),
                    Width * (.2f + .6f * b), Height * (.45f + .12f * (float)Math.Sin(b * 6.28)));
            }
        }
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].start) continue;
            using Pen p = new(Color.FromArgb(255, 244, 191, 72), 1 + points[i].pressure / 1024f * 22);
            p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawLine(p, points[i - 1].point, points[i].point);
        }
    }
}
