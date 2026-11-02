using System.Diagnostics;

namespace UsbPenDisplay;

record AdbResult(int Code, string Output)
{
    public void Require() { if (Code != 0) throw new IOException(Output.Trim()); }
}

sealed class AdbClient(string executable, string serial)
{
    public async Task<AdbResult> Run(CancellationToken stop, params string[] arguments)
    {
        stop.ThrowIfCancellationRequested();
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-s"); info.ArgumentList.Add(serial);
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(arguments[0] == "install" ? 30000 : 4000);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) {
            try { process.Kill(true); } catch { }
            stop.ThrowIfCancellationRequested();
            throw new IOException("ADB command timed out");
        }
        return new(process.ExitCode, (await stdout) + (await stderr));
    }
}

// Owns every connection-management command. It never restarts the shared ADB server.
sealed class UsbConnection(
    Func<CancellationToken, string[], Task<AdbResult>> adb,
    Func<bool> ready, Action reset, string token, string renderer, string apk, bool diagnostic,
    Action<string>? log = null, int expectedVersionCode = 0)
{
    string state = "";
    bool online, initialized, wasReady;
    long lastLaunch, lastForegroundProbe;
    int attempts;
    public string State => state;
    void StateChanged(string value) { if (state == value) return; state = value; (log ?? Console.WriteLine)("CONNECTION " + value); }
    async Task<AdbResult> Command(CancellationToken stop, params string[] args) {
        stop.ThrowIfCancellationRequested();
        AdbResult result = await adb(stop, args);
        stop.ThrowIfCancellationRequested();
        return result;
    }
    public async Task Run(CancellationToken stop)
    {
        int failures = 0;
        try {
            while (!stop.IsCancellationRequested) {
                try { await Step(stop); failures = 0; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) {
                    reset(); online = false; initialized = false; wasReady = false;
                    StateChanged("Retrying: " + e.Message.Trim()); failures = Math.Min(failures + 1, 3);
                }
                await Task.Delay(ready() ? 2000 : 500 * (1 << failures), stop);
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { reset(); StateChanged("Stopped"); }
    }
    // Single step is also used by deterministic recovery tests.
    public async Task Step(CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        AdbResult device = await Command(stop, "get-state");
        if (device.Code != 0 || device.Output.Trim() != "device") {
            if (online || wasReady) reset();
            online = false; initialized = false; wasReady = false;
            StateChanged(device.Output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ? "NeedsUsbAuthorization" :
                device.Output.Contains("offline", StringComparison.OrdinalIgnoreCase) ? "DeviceOffline" : "WaitingForDevice");
            return;
        }
        if (!online) { online = true; initialized = false; attempts = 0; StateChanged("Connecting"); }
        if (ready()) { wasReady = true; attempts = 0; StateChanged("Ready"); return; }
        if (wasReady) { wasReady = false; reset(); StateChanged("Recovering"); }
        if (apk.Length != 0) {
            AdbResult package = await Command(stop, "shell", "dumpsys", "package", "dev.usbpendisplay");
            bool current = expectedVersionCode > 0 && package.Code == 0 && System.Text.RegularExpressions.Regex.IsMatch(package.Output,
                @"\bversionCode=" + expectedVersionCode + @"\b");
            if (!current) { StateChanged("InstallingApp"); (await Command(stop, "install", "-r", apk)).Require(); }
            apk = "";
        }
        AdbResult rules = await Command(stop, "reverse", "--list"); rules.Require();
        foreach (string port in new[] { "tcp:37491", "tcp:37492" }) {
            if (!rules.Output.Contains(port + " " + port, StringComparison.Ordinal)) {
                (await Command(stop, "reverse", port, port)).Require();
                (log ?? Console.WriteLine)("USB_RULE_RESTORED " + port);
            }
        }
        // Initial/replug/host-restart launch replaces an obsolete token. A live background
        // Activity is left alone: onStart reconnects when the user returns to it.
        AdbResult process = await Command(stop, "shell", "pidof", "dev.usbpendisplay");
        bool missing = process.Code == 1 && string.IsNullOrWhiteSpace(process.Output);
        if (process.Code != 0 && !missing) process.Require();
        bool foregroundRepair = false;
        bool canLaunch = lastLaunch == 0 || Stopwatch.GetElapsedTime(lastLaunch).TotalSeconds >= 5;
        if (initialized && !missing && canLaunch && (lastForegroundProbe == 0 || Stopwatch.GetElapsedTime(lastForegroundProbe).TotalSeconds >= 5)) {
            lastForegroundProbe = Stopwatch.GetTimestamp();
            AdbResult activities = await Command(stop, "shell", "dumpsys", "activity", "activities"); activities.Require();
            foregroundRepair = activities.Output.Split('\n').Any(line =>
                (line.Contains("topResumedActivity") || line.Contains("ResumedActivity")) && line.Contains("dev.usbpendisplay/.MainActivity"));
        }
        if (attempts >= 3) { StateChanged("NeedsHelp: reconnect USB or restart host after checking tablet"); return; }
        if ((!initialized || missing || foregroundRepair) && canLaunch) {
            StateChanged("StartingApp");
            AdbResult launch = await Command(stop, "shell", "am", "start", "-n", "dev.usbpendisplay/.MainActivity",
                "--es", "session", token, "--es", "renderer", renderer, "--ez", "run_test", diagnostic ? "true" : "false");
            launch.Require();
            if (launch.Output.Contains("Error:", StringComparison.OrdinalIgnoreCase)) throw new IOException("App unavailable: reinstall USB Pen Display");
            initialized = true; attempts++; lastLaunch = Stopwatch.GetTimestamp(); StateChanged("Synchronizing");
        } else StateChanged(missing ? "WaitingForAppRestart" : "WaitingForAppOrSync");
    }
}
