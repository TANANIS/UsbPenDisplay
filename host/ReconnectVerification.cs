namespace UsbPenDisplay;

static class ReconnectVerification
{
    public static int Run()
    {
        try { Verify().GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static async Task Verify()
    {
        string device = "absent", rules = "", pid = "123";
        bool ready = false; int resets = 0, launches = 0, installs = 0;
        List<string> commands = new(), logs = new();
        Task<AdbResult> Adb(CancellationToken stop, string[] args) {
            stop.ThrowIfCancellationRequested(); commands.Add(string.Join(' ', args.Take(3)));
            AdbResult result = args[0] switch {
                "get-state" => device == "device" ? new(0,"device\n") : new(1,"error: " + device),
                "install" => new(0,"Success"),
                "reverse" => args[1] == "--list" ? new(0,rules) : new(0,""),
                "shell" => args[1] == "pidof" ? new(pid.Length == 0 ? 1 : 0,pid) : new(0,"Starting"),
                _ => throw new Exception("Unexpected command")
            };
            if(args[0]=="install")installs++;
            if(args[0]=="reverse"&&args[1]!="--list")rules+=args[1]+" "+args[2]+"\n";
            if(args[0]=="shell"&&args[1]=="am"){launches++;pid="123";}
            return Task.FromResult(result);
        }
        UsbConnection manager = new(Adb,()=>ready,()=>{resets++;ready=false;},new string('a',48),"canvas","test.apk",false,logs.Add);
        await manager.Step(default);
        Require(manager.State=="WaitingForDevice" && installs==0 && launches==0,"offline startup must wait, not install or launch");
        device="unauthorized";await manager.Step(default);
        Require(manager.State=="NeedsUsbAuthorization","authorization classification");
        device="offline";await manager.Step(default);
        Require(manager.State=="DeviceOffline","offline classification");
        device="device";await manager.Step(default);
        Require(installs==1 && launches==1 && rules.Contains("tcp:37492 tcp:37492"),"first authorized connection must install/repair/launch");
        ready=true;await manager.Step(default);int count=commands.Count;
        await manager.Step(default);
        Require(manager.State=="Ready"&&commands.Count==count+1,"healthy monitor must probe only device state");
        ready=false;await manager.Step(default);
        Require(resets==1&&launches==1,"channel loss must reset once without repeatedly stealing foreground");
        await manager.Step(default);Require(resets==1&&installs==1&&launches==1,"background app must not be relaunched or reinstalled repeatedly");
        // Keep this deterministic: use a fresh manager to exercise cold-process launch.
        pid="";UsbConnection cold=new(Adb,()=>false,()=>{},new string('b',48),"canvas","",false,logs.Add);
        await cold.Step(default);Require(launches==2,"dead app must be restarted");
        ready=true;await manager.Step(default);device="absent";await manager.Step(default);
        Require(!ready&&manager.State=="WaitingForDevice"&&resets==2,"physical transport loss must invalidate channels immediately");
        using var stopped=new CancellationTokenSource();stopped.Cancel();count=commands.Count;
        try {await manager.Step(stopped.Token);throw new Exception("Cancellation not honored");}catch(OperationCanceledException){}
        Require(commands.Count==count,"stop must suppress all new device commands");
        using var cancelling=new CancellationTokenSource();int afterCancel=0;
        UsbConnection mid=new((stop,args)=>{
            afterCancel++;
            if(args[0]=="get-state")return Task.FromResult(new AdbResult(0,"device"));
            cancelling.Cancel();return Task.FromResult(new AdbResult(0,""));
        },()=>false,()=>{},new string('c',48),"canvas","",false,logs.Add);
        try {await mid.Step(cancelling.Token);throw new Exception("Mid-command cancellation not honored");}catch(OperationCanceledException){}
        Require(afterCancel==2,"cancel during repair must prevent launch");
        Require(!logs.Any(x=>x.Contains(new string('a',48))),"session token must not be logged");
        Console.WriteLine("PASS reconnect: absent/unauthorized/offline startup, deferred install, port repair, ready gate, channel loss, cold app, background app, transport loss, cancellation before/during repair");
    }
    static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
