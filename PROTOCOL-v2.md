# USB Pen Display protocol v2

All scalar fields are big-endian; tile pixel bytes are tightly packed BGRA with opaque alpha. The two servers bind only to 127.0.0.1. Android connects to 127.0.0.1 through serial-specific ADB reverse rules on ports 37491 (video) and 37492 (pen). No Wi-Fi service is opened.

## Session handshake

Each connection sends `u32 0x55504431`, `u32 version=2`, `u32 tokenLength`, then 48 ASCII hex characters from a random per-host-run token. The Windows host compares tokens in constant time. Old protocol versions fail closed.

Video response: eight u32 values: magic, version, mirror width, mirror height, primary-screen width, primary-screen height, tile size=64, maximum in-flight updates (1 or 2).

## Video updates

44-byte header:

| Field | Type |
|---|---|
| marker=0x46524d31 | u32 |
| sequence, starts at 1 | u64 |
| base sequence, or 0 for a full update | u64 |
| flags: 1=full, 2=diagnostic stroke request, 4=verify entire reconstruction | u32 |
| tile count | u32 |
| decompressed length | u32 |
| codec: 0=raw, 2=LZ4 block; receiver also understands legacy 1=zlib | u32 |
| payload length | u32 |
| FNV-1a-32 of the entire reconstructed BGRA image when flags 1/4 are set | u32 |

Uncompressed payload repeats `u32 x,y,width,height` followed by width×height×4 pixel bytes. Rectangles are unique, aligned to the 64-pixel grid, with exact edge dimensions. Full updates cover every cell. Dimensions, lengths, duplicates, dependencies, and enabled checksums are checked before ACK.

The sender has 1/2 credits. It acquires a credit before choosing its newest capture, so no queue of encoded obsolete frames is built. A delta references the last successfully sent image, which can still be unacknowledged. TCP ordering, explicit base sequence, and ordered device application preserve this chain. In-flight deltas are never dropped. Skipped captures remain represented by cumulative per-tile versions; capture epochs invalidate version comparisons after capture recreation.

GL has at most one active job plus one queued job, and two reusable native decode buffers. Canvas decodes/applies serially using one reusable native buffer. EGL retains a full texture plus a CPU upload backing image; only changed tiles are applied. Dense groups are uploaded as a combined rectangle. Renderer changes create a new SurfaceView, avoiding competing Canvas/EGL producers.

## ACK and timing

40-byte ACK: marker=0x41434b31 (u32), sequence (u64), followed by seven u32 durations in microseconds: payload receive, decompression, upload/apply, draw, swap, device queue, checksum validation.

GL ACK follows successful `eglSwapBuffers`; Canvas ACK follows Surface submission. Neither boundary means the physical panel has lit up. Checksum readback adds deliberate GPU synchronization and is excluded from normal non-keyframe performance comparisons.

Host frame CSV records capture wait/process, tile comparison, compression, stream write, capture-to-ACK, send-to-ACK, pixel readback bytes, transmitted bytes, and the device durations. Stream write completion does not mean USB delivery has completed. Payload receive time is not a measurement of the whole USB transport. Stages can overlap, so subtracting their sum does not yield a trustworthy one-way wire latency.

## Pen channel

68-byte sample: marker=0x50454e31 (u32), kind (u32: 0 heartbeat, 1 down, 2 move, 3 up, 4 hover, 5 leave, 6 cancel), normalized x/y/pressure/tilt-x/tilt-y (five f32), buttons/tool (two u32), sequence/event timestamp/send timestamp (three u64), latest echo RTT and sender-queue duration (two u32).

Echo: marker=0x50414b32 (u32), sequence and original device send timestamp (two u64), host injection API duration (u32). Android measures roundtrip on its own clock. This does not measure one-way input delivery, application processing, or pen-to-photon latency. Historical samples are forwarded; pressure is mapped to Windows Pointer API's 0–1024 range.

Pen injection requires an acknowledged active video connection. Diagnostic injection additionally requires the isolated test canvas to be foreground. Video/input failure releases pen state; successful reconnect requires a full image starting at sequence 1.

## Recovery and verification

An outstanding video ACK has a five-second deadline. Socket failure, invalid sequence or failed reconstruction closes that connection. Missing ADB reverse rules are repaired by the host while either channel is unavailable. Since 0.2.1 a serial-specific device-state probe runs every two seconds while healthy, to detect physical transport loss even on a static desktop; expensive rule/process queries are restricted to recovery. The host never restarts ADB, changes USB/OS settings, or touches the other device.

0.2.1 explicitly cancels the idle sender on transport reset, checks idle peer EOF without waiting for a new desktop update, and invalidates both channels with a transport epoch so stale ACK/input work cannot re-enable input. Input heartbeats have a 4.5-second host read timeout. Ready requires the first full-frame ACK and an authenticated pen connection. Recovery commands are serialized and cancellable; deferred APK installation allows startup without an attached device. A live background Activity is not repeatedly foregrounded. A foreground Activity with a stale/empty session can be refreshed with the current token; repeated launch attempts are bounded. Android masks the old image until a full reconstructed frame has been submitted and the input channel is active.

`--verify-pipeline` uses distinct loopback ports and a deterministic synthetic source. It checks exact reconstruction, skipped captures, capture-epoch changes, credit limits, invalid ACK rejection and full-state reset after reconnect, for both window sizes.

`Verify.ps1` combines protocol checks, loopback checks and an actual tablet run that compares GPU readback against the same original source image and verifies every receiver reconstruction. `Benchmark.ps1` defaults to a deterministic, clock-driven synthetic graphics source; it excludes desktop capture and pen injection. `-Desktop` uses the real desktop and requires the test window to remain visible. `Summarize.ps1` reports linear-interpolated p50/p95 and update rate. An incomplete or disconnected case is rejected rather than silently included. Periodic full frames may reuse an older snapshot; full/verified frames are excluded from performance summaries.
