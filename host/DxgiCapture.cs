using System.Runtime.InteropServices;
using System.Diagnostics;

namespace UsbPenDisplay;

// Direct COM calls keep the prototype dependency-free. All indices are from
// dxgi1_2.h / d3d11.h; this application is deliberately x64-only.
unsafe sealed partial class DxgiCapture : IDisposable
{
    nint device, context, duplication, staging;
    int sourceWidth, sourceHeight;
    readonly Rectangle bounds;
    public string Name => "DXGI Desktop Duplication";
    public double LastWaitMs { get; private set; }
    public double LastProcessMs { get; private set; }
    static void* Method(nint obj, int index) => (void*)(*(nint**)obj)[index];
    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    static void Release(ref nint obj)
    {
        if (obj != 0) { ((delegate* unmanaged[Stdcall]<nint, uint>)Method(obj, 2))(obj); obj = 0; }
    }
    static nint Query(nint obj, string iid)
    {
        Guid id = new(iid); nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(obj, 0))(obj, &id, &result));
        return result;
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter, uint type, nint software,
        uint flags, nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct OutputDesc
    {
        public fixed char name[32]; public Rect rect; public uint attached, rotation; public nint monitor;
    }
    [StructLayout(LayoutKind.Sequential)] struct FrameInfo
    {
        public long present, mouse; public uint accumulated, coalesced, protectedContent;
        public int pointerX, pointerY; public uint visible, metadata, pointerShape;
    }
    [StructLayout(LayoutKind.Sequential)] struct TextureDesc
    {
        public uint width, height, mipLevels, arraySize, format, sampleCount, sampleQuality,
            usage, bindFlags, cpuAccess, misc;
    }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public nint data; public uint rowPitch, depthPitch; }

    public DxgiCapture(Rectangle primaryBounds)
    {
        bounds = primaryBounds;
        nint factory = 0, adapter = 0, output = 0, output1 = 0;
        try
        {
            Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
            Check(CreateDXGIFactory1(ref iid, out factory));
            bool found = false;
            for (uint a = 0; !found; a++)
            {
                nint candidate = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(factory, 12))(factory, a, &candidate);
                if (hr == unchecked((int)0x887A0002)) break;
                Check(hr);
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        nint candidateOutput = 0;
                        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(candidate, 7))(candidate, o, &candidateOutput);
                        if (hr == unchecked((int)0x887A0002)) break;
                        Check(hr);
                        OutputDesc desc = default;
                        hr = ((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Method(candidateOutput, 7))(candidateOutput, &desc);
                        if (hr >= 0 && desc.attached != 0 && desc.rect.left == bounds.Left && desc.rect.top == bounds.Top
                            && desc.rect.right - desc.rect.left == bounds.Width && desc.rect.bottom - desc.rect.top == bounds.Height)
                        {
                            adapter = candidate; candidate = 0; output = candidateOutput; found = true; break;
                        }
                        Release(ref candidateOutput);
                    }
                }
                finally { Release(ref candidate); }
            }
            if (!found) throw new InvalidOperationException("No DXGI output matches the primary screen.");
            Check(D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7, out device, out _, out context));
            output1 = Query(output, "00cddea8-939b-4b83-a340-a685226666cc");
            nint dup = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Method(output1, 22))(output1, device, &dup));
            duplication = dup;
        }
        catch { Dispose(); throw; }
        finally { Release(ref output1); Release(ref output); Release(ref adapter); Release(ref factory); }
    }

    public byte[]? Capture(int targetWidth, int targetHeight)
    {
        FrameInfo info = default; nint resource = 0, texture = 0;
        long acquireStart = Stopwatch.GetTimestamp();
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Method(duplication, 8))
            (duplication, 16, &info, &resource);
        if (hr == unchecked((int)0x887A0027)) return null; // WAIT_TIMEOUT, desktop unchanged
        Check(hr);
        LastWaitMs = Stopwatch.GetElapsedTime(acquireStart).TotalMilliseconds;
        long processStart = Stopwatch.GetTimestamp();
        try
        {
            texture = Query(resource, "6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            TextureDesc desc = default;
            ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Method(texture, 10))(texture, &desc);
            if (desc.width != bounds.Width || desc.height != bounds.Height)
                throw new NotSupportedException("Rotated desktop output is not supported by this prototype.");
            if (desc.format != 87 && desc.format != 91)
                throw new NotSupportedException($"Expected BGRA desktop, DXGI format={desc.format}.");
            if (staging == 0 || sourceWidth != desc.width || sourceHeight != desc.height)
            {
                Release(ref staging); sourceWidth = (int)desc.width; sourceHeight = (int)desc.height;
                desc.usage = 3; desc.bindFlags = 0; desc.cpuAccess = 0x20000; desc.misc = 0;
                desc.mipLevels = 1; desc.arraySize = 1; desc.sampleCount = 1; desc.sampleQuality = 0;
                nint created = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &desc, 0, &created));
                staging = created;
            }
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, staging, texture);
            Mapped mapped = default;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(context, 14))
                (context, staging, 0, 1, 0, &mapped));
            try
            {
                byte[] pixels = GC.AllocateUninitializedArray<byte>(targetWidth * targetHeight * 4);
                fixed (byte* output = pixels)
                {
                    for (int y = 0; y < targetHeight; y++)
                    {
                        uint* src = (uint*)((byte*)mapped.data + (long)(y * sourceHeight / targetHeight) * mapped.rowPitch);
                        uint* dst = (uint*)output + y * targetWidth;
                        for (int x = 0; x < targetWidth; x++) dst[x] = src[x * sourceWidth / targetWidth] | 0xff000000;
                    }
                }
                LastProcessMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds; return pixels;
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(context, 15))(context, staging, 0); }
        }
        finally
        {
            Release(ref texture); Release(ref resource);
            ((delegate* unmanaged[Stdcall]<nint, int>)Method(duplication, 14))(duplication);
        }
    }

    public void Dispose() { DisposeGpu(); Release(ref staging); Release(ref duplication); Release(ref context); Release(ref device); }
}
