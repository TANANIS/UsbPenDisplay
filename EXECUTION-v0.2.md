# v0.2 execution and acceptance

Scope: execute latency work 1–4, preserving full-primary-screen mirroring and USB-only transport. No canvas crop, virtual monitor, wireless stack or OS configuration changes.

1. Baseline and measurement: per-frame CSV, separate capture/compare/compress/send/device receive/decode/upload/draw/swap/ACK timings, input echo RTT. Use identical deterministic drawing and full-screen transform workloads. Software timestamps are not physical pen-to-photon measurements.
2. Bounded pipeline: selectable window 1 or 2, explicit base sequence, ordered deltas, sender baseline tracks last sent state, receiver verifies dependencies, bounded device jobs and a timeout/reconnect path. No arbitrary delta dropping.
3. Capture: compute-shader nearest-neighbor scaling, DXGI dirty/move metadata, target-sized region readback, versioned dirty tiles that survive skipped captures. Conservative full-frame and legacy capture fallbacks remain selectable.
4. Presentation: persistent OpenGL texture, native glTexSubImage2D with dense-region batching, shader channel conversion, explicit EGL swap, bounded render queue. Both GL and Canvas use reusable native decode buffers. Canvas is the measured default on this tablet; GL remains selectable.

Acceptance: builds and protocol tests; exact-pixel integrity checks during delta and transform workloads; window 1/2 and legacy/optimized comparisons; idle, reconnect, cancellation and pen release checks. Record observed failures and fallback use. CSP/Aseprite human feel and comparison against a Huion display remain separate acceptance steps.

Engineering status, 2026-11-02: work 1–4 implemented, final build and protocol/actual-engine loopback tests passed. Device integrity passed 677 GL/window2 frames, 312 Canvas/window1 recovery frames, and 382 Canvas/window2 frames. Reverse-rule repair and app restart/full-state reset passed. Latest recovery run had no foreground pen contact, so it checks released state rather than proving release during a physical held-pen disconnect. Four fixed-source transport/presentation cases completed without unexpected faults. Earlier actual-desktop comparisons were affected by foreground changes; no causal baseline speedup is claimed. See RESULTS-v0.2.md and evidence/v0.2 for exact scope and numbers.

Human acceptance remains open: physical S Pen/CSP/Aseprite, long-session stability, held-pen physical unplug, and pen-to-photon comparison against Huion. The reliable engineering chain is complete; the original physical latency target is not marked achieved.
