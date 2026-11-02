using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UsbPenDisplay;

sealed class DesktopPanel : Form
{
    sealed record Device(string Serial, string Model) { public override string ToString() => Model + " · " + Serial[^Math.Min(4,Serial.Length)..]; }
    sealed record Preferences(string Serial = "", int Width = 1280);
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UsbPenDisplay");
    public static string AdbPath => File.Exists(Path.Combine(AppContext.BaseDirectory,"tools","adb.exe"))
        ? Path.Combine(AppContext.BaseDirectory,"tools","adb.exe")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Android","Sdk","platform-tools","adb.exe");
    readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 490 };
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 490 };
    readonly Label state = new() { Text = "請接上平板，並允許 USB 偵錯連線。", AutoSize = false, Height = 66, Width = 490 };
    readonly Button start = new() { Text = "開始繪圖連線", Width = 320, Height = 46 };
    readonly Button stop = new() { Text = "停止", Width = 150, Height = 46, Enabled = false };
    readonly System.Windows.Forms.Timer scan = new() { Interval = 2500 };
    readonly CancellationTokenSource lifetime = new();
    readonly NotifyIcon tray;
    Preferences preferences = new();
    Process? engine;
    Task? output, errors;
    string serial = "";
    bool scanning, exiting, stopping;
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window,int command);
    public static int Run()
    {
        FreeConsole();
        using Mutex single = new(true,"Local\\UsbPenDisplay-Panel",out bool owns);
        if(!owns){MessageBox.Show("USB Pen Display 已在執行，請從系統匣開啟。","USB Pen Display");return 0;}
        Application.Run(new DesktopPanel());return 0;
    }
    public DesktopPanel()
    {
        Directory.CreateDirectory(Data);
        try {preferences=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Data,"settings.json")))??new();}catch{}
        Text="USB Pen Display";ClientSize=new(560,480);MinimumSize=Size;MaximumSize=Size;
        StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;
        BackColor=Color.FromArgb(32,35,43);ForeColor=Color.FromArgb(232,233,237);Font=new("Microsoft JhengHei UI",10);
        var content=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(30,24,30,18), AutoScroll=true };
        Controls.Add(content);
        content.Controls.Add(new Label{Text="繪圖連線",Font=new(Font.FontFamily,22,FontStyle.Bold),AutoSize=true,Margin=new(0,0,0,8)});
        content.Controls.Add(new Label{Text="完整主螢幕映射 · USB · S Pen",AutoSize=true,ForeColor=Color.Silver,Margin=new(0,0,0,24)});
        content.Controls.Add(new Label{Text="平板裝置",AutoSize=true});content.Controls.Add(devices);
        state.Margin=new(0,14,0,8);content.Controls.Add(state);
        content.Controls.Add(new Label{Text="畫面品質",AutoSize=true});
        quality.Items.AddRange(new object[]{"流暢 · 1280","清晰 · 1600","原始解析度"});
        quality.SelectedIndex=preferences.Width==1600?1:preferences.Width==3840?2:0;content.Controls.Add(quality);
        devices.SelectedIndexChanged+=(_,_)=>{if(engine==null)start.Enabled=devices.SelectedItem!=null;};
        content.Controls.Add(new Label{Text="斷線時自動等候恢復；完整同步後繼續接收筆輸入。",AutoSize=true,ForeColor=Color.Silver,Margin=new(0,14,0,16)});
        var actions=new FlowLayoutPanel{Width=500,Height=56,Margin=Padding.Empty};actions.Controls.Add(start);actions.Controls.Add(stop);content.Controls.Add(actions);
        start.BackColor=Color.FromArgb(210,177,129);start.ForeColor=Color.FromArgb(30,30,30);start.FlatStyle=FlatStyle.Flat;
        stop.FlatStyle=FlatStyle.Flat;
        var links=new FlowLayoutPanel{Width=500,Height=35,Margin=Padding.Empty};
        Button logs=new(){Text="開啟紀錄",Width=150,Height=30,FlatStyle=FlatStyle.Flat};
        Button hide=new(){Text="收至系統匣",Width=150,Height=30,FlatStyle=FlatStyle.Flat};
        links.Controls.Add(logs);links.Controls.Add(hide);content.Controls.Add(links);
        tray=new NotifyIcon{Icon=SystemIcons.Application,Text="USB Pen Display",Visible=true};
        var menu=new ContextMenuStrip();menu.Items.Add("開啟面板",null,(_,_)=>Restore());menu.Items.Add("停止連線",null,async(_,_)=>await StopEngine());
        menu.Items.Add("結束程式",null,async(_,_)=>{if(await StopEngine()){exiting=true;Close();}});tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Restore();
        start.Click+=async(_,_)=>await StartEngine();stop.Click+=async(_,_)=>await StopEngine();hide.Click+=(_,_)=>Hide();
        logs.Click+=(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",Data){UseShellExecute=true});
        scan.Tick+=async(_,_)=>await Scan();Shown+=async(_,_)=>{ShowWindow(Handle,5);Activate();await Scan();scan.Start();};
        FormClosing+=(_,e)=>{if(!exiting){e.Cancel=true;Hide();}};
        FormClosed+=(_,_)=>{lifetime.Cancel();scan.Dispose();tray.Dispose();lifetime.Dispose();};
    }
    void Restore(){Show();WindowState=FormWindowState.Normal;Activate();}
    async Task Scan()
    {
        if(scanning||engine!=null||lifetime.IsCancellationRequested)return;scanning=true;
        try {
            if(!File.Exists(AdbPath))throw new IOException("缺少 USB 工具，請重新解壓完整套件。");
            using Process p=new(){StartInfo=new(AdbPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
            p.StartInfo.ArgumentList.Add("devices");p.StartInfo.ArgumentList.Add("-l");p.Start();
            var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(4000);
            try{await p.WaitForExitAsync(timeout.Token);}catch{try{p.Kill(true);}catch{}throw;}
            string text=await stdout;await stderr;if(p.ExitCode!=0)throw new IOException("USB 工具尚未就緒，正在重試。");
            string selected=(devices.SelectedItem as Device)?.Serial??preferences.Serial;
            var found=text.Split('\n').Select(x=>x.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))
                .Where(x=>x.Length>=2&&x[1]=="device").Select(x=>new Device(x[0],x.FirstOrDefault(v=>v.StartsWith("model:"))?[6..].Replace('_',' ')??"Android")).ToArray();
            devices.Items.Clear();devices.Items.AddRange(found);
            Device? preferred=found.FirstOrDefault(x=>x.Serial==selected)??found.FirstOrDefault(x=>x.Model=="SM P610")??(found.Length==1?found[0]:null);
            if(preferred!=null)devices.SelectedItem=preferred;
            start.Enabled=devices.SelectedItem!=null;
            state.Text=found.Length>0?"選擇平板後按「開始繪圖連線」。":text.Contains("unauthorized")?"請解鎖平板，允許 USB 偵錯連線。":"正在等候 USB 裝置…";
        }catch(OperationCanceledException){if(!lifetime.IsCancellationRequested)state.Text="USB 裝置探測逾時，正在重試。";}
        catch(Exception e){state.Text=e.Message;start.Enabled=false;}
        finally{scanning=false;}
    }
    async Task StartEngine()
    {
        if(engine!=null||devices.SelectedItem is not Device device)return;
        try{
            serial=device.Serial;int width=quality.SelectedIndex==1?1600:quality.SelectedIndex==2?3840:1280;
            preferences=new(serial,width);File.WriteAllText(Path.Combine(Data,"settings.json"),JsonSerializer.Serialize(preferences));
            string logs=Path.Combine(Data,"logs",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(logs);
            ProcessStartInfo info=new(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"--run","--serial",serial,"--width",width.ToString(),"--adb",AdbPath,"--logs",logs})info.ArgumentList.Add(arg);
            string apk=Path.Combine(AppContext.BaseDirectory,"UsbPenDisplay.apk");if(File.Exists(apk)){info.ArgumentList.Add("--apk");info.ArgumentList.Add(apk);}
            engine=Process.Start(info)!;Process running=engine;
            start.Enabled=false;devices.Enabled=false;quality.Enabled=false;stop.Enabled=true;state.Text="正在建立連線…";
            output=Read(running.StandardOutput,Path.Combine(logs,"host.log"));errors=Read(running.StandardError,Path.Combine(logs,"host.err"));
            await running.WaitForExitAsync();await Task.WhenAll(output,errors);
            state.Text=running.ExitCode==0?"連線已停止。":"連線程序結束，請查看紀錄後重新開始。";
            engine=null;running.Dispose();devices.Enabled=true;quality.Enabled=true;stop.Enabled=false;start.Enabled=devices.SelectedItem!=null;
        }catch(Exception e){state.Text=e.Message;if(engine==null)start.Enabled=true;}
    }
    async Task Read(StreamReader reader,string path)
    {
        using var file=new StreamWriter(path){AutoFlush=true};string? line;
        while((line=await reader.ReadLineAsync())!=null){await file.WriteLineAsync(line);
            if(line.StartsWith("CONNECTION "))state.Text=Translate(line[11..]);
        }
    }
    static string Translate(string value)=>value switch{
        "Ready"=>"已連線 · 可以繪圖", "WaitingForDevice"=>"USB 已中斷，正在等候重新接線…",
        "NeedsUsbAuthorization"=>"請解鎖平板，允許 USB 偵錯連線。", "DeviceOffline"=>"平板暫不可用，請確認線材與解鎖狀態。",
        "InstallingApp"=>"正在更新平板 App…", "StartingApp"=>"正在開啟平板 App…", "Synchronizing"=>"正在同步完整畫面…",
        "Stopped"=>"連線已停止。", "Connecting" or "Recovering" or "WaitingForAppRestart"=>"正在恢復 USB 連線…",
        "WaitingForAppOrSync"=>"等候平板 App／畫面同步；若在背景，請開啟 App。",
        _=>value.StartsWith("NeedsHelp")?"恢復未成功，請接回 USB 或停止後重試；詳情見紀錄。":"正在重試連線；請確認 USB 與平板狀態。"};
    async Task<bool> StopEngine()
    {
        if(stopping)return false;if(engine==null)return true;stopping=true;stop.Enabled=false;
        try{
            using var signal=EventWaitHandle.OpenExisting("Local\\UsbPenDisplay-Stop-"+serial);signal.Set();
            Process running=engine;
            try{using var timeout=new CancellationTokenSource(12000);await running.WaitForExitAsync(timeout.Token);return true;}
            catch(OperationCanceledException){state.Text="仍在停止中，請稍後再試。";return false;}
        }catch(WaitHandleCannotBeOpenedException){state.Text="程序尚在啟動，請稍候再按停止。";return false;}
        finally{stopping=false;if(engine!=null)stop.Enabled=true;}
    }
}
