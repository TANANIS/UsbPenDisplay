using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace UsbPenDisplay;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length == 0 || args.Contains("--desktop")) return DesktopPanel.Run();
        string Get(string key, string fallback) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        if (args.Contains("--verify")) return Verification.Run(Get("--vectors", ""));
        if (args.Contains("--verify-pipeline")) return PipelineVerification.Run(Get("--logs", Path.Combine(AppContext.BaseDirectory, "verification")));
        if (args.Contains("--verify-reconnect")) return ReconnectVerification.Run();
        string serial = Get("--serial", "");
        if (serial.Length == 0) { Console.Error.WriteLine("Specify --serial DEVICE_SERIAL, or launch without arguments to select a device."); return 1; }
        using Mutex singleton = new(true, "Local\\UsbPenDisplay-" + serial, out bool ownsMutex);
        if (!ownsMutex) { Console.Error.WriteLine("A host for this tablet is already running. Use Stop.ps1 first."); return 1; }
        string adb = Get("--adb", DesktopPanel.AdbPath);
        int width = int.Parse(Get("--width", "1280")), fps = int.Parse(Get("--fps", "60"));
        if (width < 320 || width > 3840 || fps < 5 || fps > 120) throw new ArgumentException("width=320..3840, fps=5..120");
        bool headless = args.Contains("--headless-benchmark");
        bool diagnostic = args.Contains("--diagnostic") || headless;
        if (args.Contains("--recovery-test") && !diagnostic) throw new ArgumentException("Recovery test requires the isolated diagnostic canvas.");
        Rectangle primary = Screen.PrimaryScreen!.Bounds;
        int window = int.Parse(Get("--window", "2"));
        if (window is < 1 or > 2) throw new ArgumentException("--window must be 1 or 2");
        bool optimized = Get("--capture", "gpu") != "legacy";
        string renderer = Get("--renderer", "canvas");
        Console.WriteLine($"USB Pen Display 0.3.0 | primary={primary} | serial={serial} | {DateTimeOffset.Now:O}");
        Console.WriteLine("USB only; local listeners; Ctrl+C stops host and removes its ADB reverse rules.");
        using CancellationTokenSource shutdown = new();
        using EventWaitHandle stopEvent = new(false, EventResetMode.ManualReset, "Local\\UsbPenDisplay-Stop-" + serial);
        _ = Task.Run(() => { WaitHandle.WaitAny(new WaitHandle[] { stopEvent, shutdown.Token.WaitHandle }); shutdown.Cancel(); });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        string logDir = Get("--logs", Path.Combine(AppContext.BaseDirectory, "logs")); Directory.CreateDirectory(logDir);
        int mirrorWidth = Math.Min(primary.Width, width), mirrorHeight = Math.Max(1, (int)Math.Round(primary.Height * Math.Min(primary.Width, width) / (double)primary.Width));
        using SyntheticSource? synthetic = headless ? new(mirrorWidth, mirrorHeight) : null;
        using Engine engine = new(primary, width, fps, diagnostic, logDir, shutdown.Token, window, optimized, args.Contains("--verify-frames"), synthetic == null ? null : synthetic.Next);
        Task? usbMonitor = null;
        AdbClient adbClient = new(adb, serial);
        Task? recoveryTest = null;
        try
        {
            string apk = Get("--apk", "");
            engine.Start();
            UsbConnection connection = new((stop, command) => adbClient.Run(stop, command), () => engine.IsReady,
                engine.ResetConnections, engine.Token, renderer, apk, diagnostic && !headless, expectedVersionCode: 4);
            usbMonitor = Task.Run(() => connection.Run(shutdown.Token));
            if (headless)
            {
                Console.WriteLine("BENCH_SOURCE synthetic; desktop capture and pen injection are excluded from this benchmark.");
                Stopwatch startup = Stopwatch.StartNew();
                while (!shutdown.IsCancellationRequested && synthetic!.Seconds < 26) {
                    if (!synthetic.Started && startup.Elapsed.TotalSeconds > 10) throw new TimeoutException("Tablet did not connect to benchmark source");
                    Thread.Sleep(25);
                }
                if (!shutdown.IsCancellationRequested) Console.WriteLine("BENCH_COMPLETE");
            }
            else if (diagnostic)
            {
                using DiagnosticCanvas canvas = new(primary, shutdown, args.Contains("--benchmark"));
                canvas.RequestStroke += engine.RequestDiagnostic;
                canvas.Shown += (_, _) => {
                    if (args.Contains("--recovery-test")) recoveryTest = Task.Run(async () => {
                        async Task WaitFor(Func<bool> condition, int milliseconds) {
                            long start = Stopwatch.GetTimestamp();
                            while (!condition()) {
                                shutdown.Token.ThrowIfCancellationRequested();
                                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > milliseconds) throw new TimeoutException("Recovery verification condition timed out");
                                await Task.Delay(25, shutdown.Token);
                            }
                        }
                        await WaitFor(() => engine.IsReady, 12000);
                        await Task.Delay(3000, shutdown.Token);
                        long initialConnections = engine.VideoConnections;
                        Console.WriteLine("RECOVERY_TEST remove this app's USB reverse rules and stop only its Android process");
                        foreach (string port in new[] { "tcp:37491", "tcp:37492" }) {
                            shutdown.Token.ThrowIfCancellationRequested(); AdbRead(adb, serial, 4000, "reverse", "--remove", port);
                        }
                        shutdown.Token.ThrowIfCancellationRequested();
                        AdbRead(adb, serial, 4000, "shell", "am", "force-stop", "dev.usbpendisplay");
                        await WaitFor(() => !engine.IsVideoReady && !engine.IsPenContact, 5000);
                        await WaitFor(() => {
                            string rules = AdbRead(adb, serial, 4000, "reverse", "--list");
                            return rules.Contains("tcp:37491 tcp:37491") && rules.Contains("tcp:37492 tcp:37492");
                        }, 8000);
                        await WaitFor(() => engine.IsReady && engine.VideoConnections > initialConnections, 12000);
                        Console.WriteLine("RECOVERY_PASS reverse rules restored automatically, pen released, new full frame acknowledged");
                    });
                };
                Application.Run(canvas);
                if (recoveryTest != null) recoveryTest.GetAwaiter().GetResult();
            }
            else
            {
                while (!shutdown.IsCancellationRequested) Thread.Sleep(100);
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            shutdown.Cancel();
            try { usbMonitor?.GetAwaiter().GetResult(); } catch { }
            foreach (string port in new[] { "tcp:37491", "tcp:37492" })
                try { using var cleanup = new CancellationTokenSource(1500); adbClient.Run(cleanup.Token, "reverse", "--remove", port).GetAwaiter().GetResult(); }
                catch (Exception e) { Console.Error.WriteLine("Cleanup " + port + ": " + e.Message); }
        }
    }
    static void Adb(string executable, string serial, params string[] args)
        => Console.Write(AdbRead(executable, serial, 30000, args));
    static string AdbRead(string executable, string serial, int timeout, params string[] args)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-s"); info.ArgumentList.Add(serial);
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process p = Process.Start(info)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout)) { p.Kill(true); throw new TimeoutException("ADB command timed out."); }
        if (p.ExitCode != 0) throw new InvalidOperationException(stderr.Result);
        return stdout.Result;
    }
}
