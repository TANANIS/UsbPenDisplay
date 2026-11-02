using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsbPenDisplay;

sealed class PenInput : IDisposable
{
    readonly nint device;
    readonly Rectangle screen;
    bool contact, range;
    Point last;
    public long Events, Errors;
    public float LastPressure;
    public bool IsContact => contact;
    public PenInput(Rectangle screen)
    {
        this.screen = screen;
        if (Marshal.SizeOf<PointerInfo>() != 96 || Marshal.SizeOf<PointerTypeInfo>() != 152)
            throw new InvalidOperationException("Unsupported pointer structure layout.");
        device = CreateSyntheticPointerDevice(3, 1, 3);
        if (device == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct PointerInfo
    {
        public uint Type, Id, Frame, Flags; public nint Source, Target;
        public NativePoint Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History; public int InputData; public uint Keys;
        public ulong Performance; public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PenInfo
    {
        public PointerInfo Info; public uint Flags, Mask, Pressure, Rotation; public int TiltX, TiltY;
    }
    [StructLayout(LayoutKind.Explicit, Size = 152)] struct PointerTypeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public PenInfo Pen;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern nint CreateSyntheticPointerDevice(uint type, uint max, uint mode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectSyntheticPointerInput(nint device, ref PointerTypeInfo info, uint count);
    [DllImport("user32.dll")] static extern void DestroySyntheticPointerDevice(nint device);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerPenInfo(uint id, out PenInfo info);

    public void Send(int kind, float x, float y, float pressure, float tiltX, float tiltY, int buttons, int tool)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure) ||
            !float.IsFinite(tiltX) || !float.IsFinite(tiltY) || kind < 1 || kind > 6) return;
        last = new Point(screen.Left + (int)Math.Round(Math.Clamp(x, 0, 1) * (screen.Width - 1)),
            screen.Top + (int)Math.Round(Math.Clamp(y, 0, 1) * (screen.Height - 1)));
        bool wasContact = contact;
        if (kind == 1) contact = true;
        if (kind is 3 or 5 or 6) contact = false;
        bool inRange = kind is not (5 or 6);
        uint flags = 0x2000u; // primary
        if (inRange) flags |= 2;
        if (!range && inRange) flags |= 1;
        if (contact) flags |= 4 | 0x10;
        flags |= !wasContact && contact ? 0x10000u : wasContact && !contact ? 0x40000u : 0x20000u;
        PointerTypeInfo packet = new() { Type = 3 };
        packet.Pen.Info = new PointerInfo { Type = 3, Id = 1, Flags = flags,
            Pixel = new NativePoint { X = last.X, Y = last.Y },
            ButtonChange = !wasContact && contact ? 1u : wasContact && !contact ? 2u : 0u };
        packet.Pen.Flags = ((buttons & 2) != 0 ? 1u : 0u) | (tool == 4 ? 6u : 0u);
        packet.Pen.Mask = 1 | 4 | 8;
        packet.Pen.Pressure = contact ? (uint)Math.Round(Math.Clamp(pressure, 0, 1) * 1024) : 0;
        packet.Pen.TiltX = (int)Math.Clamp(tiltX, -90, 90);
        packet.Pen.TiltY = (int)Math.Clamp(tiltY, -90, 90);
        if (!InjectSyntheticPointerInput(device, ref packet, 1))
        {
            Errors++; int error = Marshal.GetLastWin32Error();
            if (Errors < 6) Console.WriteLine($"PEN_ERROR win32={error} kind={kind} flags={flags:X}");
        }
        else { Events++; LastPressure = pressure; contact = inRange && contact; range = inRange; }
    }
    public void Reset()
    {
        if (contact || range)
        {
            float x = (last.X - screen.Left) / (float)Math.Max(1, screen.Width - 1);
            float y = (last.Y - screen.Top) / (float)Math.Max(1, screen.Height - 1);
            Send(6, x, y, 0, 0, 0, 0, 2);
        }
        contact = range = false;
    }
    public void Dispose() { Reset(); DestroySyntheticPointerDevice(device); }
}
