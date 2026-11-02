using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace UsbPenDisplay;

unsafe sealed partial class DxgiCapture
{
    nint gpuSource, sourceView, gpuScaled, targetView, gpuReadback, compute, constants;
    nint dirtyBuffer, dirtyView, dirtyReadback;
    byte[]? persistent;
    int[]? tileVersions;
    int gpuWidth, gpuHeight, generation;
    static int nextEpoch;
    readonly int epoch = Interlocked.Increment(ref nextEpoch);
    public bool ValidateGpu { get; set; }
    [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint bytes, usage, bind, access, misc, stride; }
    [StructLayout(LayoutKind.Sequential)] struct InitialData { public nint data; public uint pitch, slice; }
    [StructLayout(LayoutKind.Sequential)] struct Box { public uint left, top, front, right, bottom, back; }
    [StructLayout(LayoutKind.Sequential)] struct Move { public int x, y; public Rect destination; }
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] source, nuint size, string? name, nint defines, nint include,
        string entry, string target, uint flags, uint effects, out nint code, out nint errors);
    const string ScaleShader = """
        Texture2D<float4> Source : register(t0);
        RWTexture2D<uint> Target : register(u0);
        RWStructuredBuffer<uint> Dirty : register(u1);
        cbuffer Dimensions : register(b0) { uint sw; uint sh; uint tw; uint th; };
        [numthreads(8,8,1)] void main(uint3 p : SV_DispatchThreadID) {
            if (p.x >= tw || p.y >= th) return;
            uint2 s = uint2(p.x * sw / tw, p.y * sh / th);
            uint3 c = (uint3)round(saturate(Source.Load(int3(s,0)).bgr) * 255.0);
            uint value = 0xff000000 | c.x | (c.y << 8) | (c.z << 16);
            if (Target[p.xy] != value) {
                Target[p.xy] = value;
                InterlockedOr(Dirty[(p.y / 64) * ((tw + 63) / 64) + p.x / 64], 1);
            }
        }
        """;

    void InitializeGpu(TextureDesc sourceDesc, int w, int h)
    {
        DisposeGpu(); gpuWidth = w; gpuHeight = h;
        nint code = 0, errors = 0;
        try
        {
            byte[] shader = Encoding.ASCII.GetBytes(ScaleShader);
            int hr = D3DCompile(shader, (nuint)shader.Length, null, 0, 0, "main", "cs_5_0", 1 << 15, 0, out code, out errors);
            if (hr < 0)
            {
                string message = errors == 0 ? "Compute shader compilation failed" : Marshal.PtrToStringAnsi(
                    ((delegate* unmanaged[Stdcall]<nint, nint>)Method(errors, 3))(errors))!;
                throw new InvalidOperationException(message);
            }
            nint data = ((delegate* unmanaged[Stdcall]<nint, nint>)Method(code, 3))(code);
            nuint size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Method(code, 4))(code);
            nint created = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Method(device, 18))(device, data, size, 0, &created));
            compute = created;
            sourceDesc.usage = 0; sourceDesc.bindFlags = 8; sourceDesc.cpuAccess = 0; sourceDesc.misc = 0; sourceDesc.format = 87;
            sourceDesc.mipLevels = 1; sourceDesc.arraySize = 1; sourceDesc.sampleCount = 1; sourceDesc.sampleQuality = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &sourceDesc, 0, &created));
            gpuSource = created;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(device, 7))(device, gpuSource, 0, &created)); sourceView = created;
            TextureDesc target = new() { width = (uint)w, height = (uint)h, mipLevels = 1, arraySize = 1, format = 42, sampleCount = 1, bindFlags = 128 };
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &target, 0, &created)); gpuScaled = created;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(device, 8))(device, gpuScaled, 0, &created)); targetView = created;
            target.usage = 3; target.bindFlags = 0; target.cpuAccess = 0x20000;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &target, 0, &created)); gpuReadback = created;
            uint* dimensions = stackalloc uint[4] { sourceDesc.width, sourceDesc.height, (uint)w, (uint)h };
            InitialData initial = new() { data = (nint)dimensions };
            BufferDesc buffer = new() { bytes = 16, bind = 4 };
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, InitialData*, nint*, int>)Method(device, 3))(device, &buffer, &initial, &created)); constants = created;
            uint cells = (uint)(((w + 63) / 64) * ((h + 63) / 64));
            BufferDesc flags = new() { bytes = cells * 4, bind = 128, misc = 0x40, stride = 4 };
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Method(device, 3))(device, &flags, 0, &created)); dirtyBuffer = created;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(device, 8))(device, dirtyBuffer, 0, &created)); dirtyView = created;
            flags.usage = 3; flags.bind = 0; flags.misc = 0; flags.stride = 0; flags.access = 0x20000;
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Method(device, 3))(device, &flags, 0, &created)); dirtyReadback = created;
            persistent = new byte[w * h * 4]; tileVersions = new int[((w + 63) / 64) * ((h + 63) / 64)];
            Console.WriteLine($"GPU_CAPTURE compute scale {sourceDesc.width}x{sourceDesc.height}->{w}x{h}, exact GPU dirty-tile detection + region readback");
        }
        finally { Release(ref code); Release(ref errors); }
    }

    public CapturedImage? CaptureOptimized(int w, int h)
    {
        FrameInfo info = default; nint resource = 0, texture = 0;
        long acquireStart = Stopwatch.GetTimestamp();
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Method(duplication, 8))(duplication, 16, &info, &resource);
        if (hr == unchecked((int)0x887A0027)) return null;
        Check(hr);
        LastWaitMs = Stopwatch.GetElapsedTime(acquireStart).TotalMilliseconds;
        long processStart = Stopwatch.GetTimestamp();
        try
        {
            if (info.present == 0 && persistent != null) return null; // pointer-only change; pixels are unchanged
            texture = Query(resource, "6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            TextureDesc desc = default; ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Method(texture, 10))(texture, &desc);
            if (desc.width != bounds.Width || desc.height != bounds.Height || (desc.format != 87 && desc.format != 91))
                throw new NotSupportedException("GPU capture requires an unrotated SDR BGRA primary display.");
            bool first = persistent == null || w != gpuWidth || h != gpuHeight;
            int sourceW = (int)desc.width, sourceH = (int)desc.height;
            if (first) InitializeGpu(desc, w, h);
            int columns = (w + 63) / 64, rows = (h + 63) / 64;
            bool[] dirty = new bool[columns * rows];
            if (first || info.metadata == 0) Array.Fill(dirty, true);
            else
            {
                byte[] meta = new byte[info.metadata]; uint used = 0;
                fixed (byte* ptr = meta)
                {
                    hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte*, uint*, int>)Method(duplication, 9))(duplication, (uint)meta.Length, ptr, &used);
                    if (hr < 0) Array.Fill(dirty, true);
                    else for (int i = 0; i < used / sizeof(Rect); i++) Mark(((Rect*)ptr)[i]);
                    hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte*, uint*, int>)Method(duplication, 10))(duplication, (uint)meta.Length, ptr, &used);
                    if (hr < 0) Array.Fill(dirty, true);
                    else for (int i = 0; i < used / sizeof(Move); i++)
                    {
                        Move move = ((Move*)ptr)[i]; Mark(move.destination);
                        Mark(new Rect { left = move.x, top = move.y,
                            right = move.x + move.destination.right - move.destination.left,
                            bottom = move.y + move.destination.bottom - move.destination.top });
                    }
                }
            }
            if (!dirty.Any(value => value)) return null;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, gpuSource, texture);
            nint srv = sourceView, cb = constants;
            nint* uavs = stackalloc nint[2] { targetView, dirtyView };
            uint* zero = stackalloc uint[4]; zero[0] = zero[1] = zero[2] = zero[3] = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)Method(context, 51))(context, dirtyView, zero);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, 67))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Method(context, 68))(context, 0, 2, uavs, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Method(context, 69))(context, compute, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, 71))(context, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Method(context, 41))(context, (uint)(w + 7) / 8, (uint)(h + 7) / 8, 1);
            srv = 0; uavs[0] = uavs[1] = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, 67))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Method(context, 68))(context, 0, 2, uavs, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, dirtyReadback, dirtyBuffer);
            Mapped flagsMap = default;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(context, 14))(context, dirtyReadback, 0, 1, 0, &flagsMap));
            try { for (int i = 0; i < dirty.Length; i++) dirty[i] = first || ((uint*)flagsMap.data)[i] != 0; }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(context, 15))(context, dirtyReadback, 0); }
            if (!dirty.Any(value => value))
            {
                if (ValidateGpu) ValidateSource(texture, desc, w, h);
                return null;
            }
            List<Rectangle> regions = new();
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                if (!dirty[row * columns + col]) continue;
                int start = col; while (col + 1 < columns && dirty[row * columns + col + 1]) col++;
                regions.Add(Rectangle.FromLTRB(start * 64, row * 64, Math.Min(w, (col + 1) * 64), Math.Min(h, (row + 1) * 64)));
            }
            foreach (Rectangle region in regions)
            {
                Box box = new() { left = (uint)region.Left, top = (uint)region.Top, right = (uint)region.Right, bottom = (uint)region.Bottom, back = 1 };
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Method(context, 46))
                    (context, gpuReadback, 0, box.left, box.top, 0, gpuScaled, 0, &box);
            }
            Mapped mapped = default;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(context, 14))(context, gpuReadback, 0, 1, 0, &mapped));
            int readBytes = 0;
            try
            {
                foreach (Rectangle region in regions)
                {
                    for (int row = region.Top; row < region.Bottom; row++)
                        Marshal.Copy(mapped.data + (int)(row * mapped.rowPitch) + region.Left * 4, persistent!, (row * w + region.Left) * 4, region.Width * 4);
                    readBytes += region.Width * region.Height * 4;
                }
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(context, 15))(context, gpuReadback, 0); }
            generation++;
            for (int i = 0; i < dirty.Length; i++) if (dirty[i]) tileVersions![i] = generation;
            if (ValidateGpu) ValidateSource(texture, desc, w, h);
            byte[] snapshot = (byte[])persistent!.Clone();
            LastProcessMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
            return new CapturedImage(snapshot, (int[])tileVersions!.Clone(), epoch, readBytes, dirty.Count(value => value), LastWaitMs, LastProcessMs);

            void Mark(Rect region)
            {
                // Expand conservatively around nearest-neighbor sample boundaries.
                int x0 = Math.Clamp((int)((long)region.left * w / sourceW) - 1, 0, w - 1);
                int y0 = Math.Clamp((int)((long)region.top * h / sourceH) - 1, 0, h - 1);
                int x1 = Math.Clamp((int)(((long)region.right * w + sourceW - 1) / sourceW) + 1, 1, w);
                int y1 = Math.Clamp((int)(((long)region.bottom * h + sourceH - 1) / sourceH) + 1, 1, h);
                for (int ty = y0 / 64; ty <= (y1 - 1) / 64; ty++)
                for (int tx = x0 / 64; tx <= (x1 - 1) / 64; tx++) dirty[ty * columns + tx] = true;
            }
        }
        finally
        {
            Release(ref texture); Release(ref resource);
            ((delegate* unmanaged[Stdcall]<nint, int>)Method(duplication, 14))(duplication);
        }
    }
    void ValidateSource(nint texture, TextureDesc desc, int w, int h)
    {
        if (staging == 0)
        {
            desc.usage = 3; desc.bindFlags = 0; desc.cpuAccess = 0x20000; desc.misc = 0;
            nint created = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &desc, 0, &created)); staging = created;
        }
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(context, 47))(context, staging, texture);
        Mapped mapped = default;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Method(context, 14))(context, staging, 0, 1, 0, &mapped));
        try
        {
            fixed (byte* bytes = persistent)
            for (int y = 0; y < h; y++)
            {
                uint* source = (uint*)((byte*)mapped.data + (long)(y * (int)desc.height / h) * mapped.rowPitch);
                uint* actual = (uint*)bytes + y * w;
                for (int x = 0; x < w; x++)
                    if (actual[x] != (source[x * (int)desc.width / w] | 0xff000000))
                        throw new InvalidDataException($"GPU/dirty-region validation mismatch at {x},{y}");
            }
        }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(context, 15))(context, staging, 0); }
    }
    void DisposeGpu()
    {
        Release(ref dirtyView); Release(ref dirtyReadback); Release(ref dirtyBuffer);
        Release(ref constants); Release(ref compute); Release(ref targetView); Release(ref sourceView);
        Release(ref gpuReadback); Release(ref gpuScaled); Release(ref gpuSource); persistent = null; tileVersions = null;
    }
}
