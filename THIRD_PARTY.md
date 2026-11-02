# References and third-party components

The host and Android application code were written for this prototype. No UScreen or VNC application code was copied into it.

- **K4os.Compression.LZ4 1.3.8** — MIT. Distributed as a .NET dependency in `release/host/`. Upstream: https://github.com/MiloszKrajewski/K4os.Compression.LZ4 . License: `licenses/K4os.Compression.LZ4-LICENSE`.
- **LZ4 1.10.0** — BSD 2-Clause. Original `lz4.c`, `lz4.h`, and license are included in `android/native/vendor/`; they are compiled into `libusbpen.so`. Source: https://github.com/lz4/lz4/tree/v1.10.0/lib . The JNI glue is original application code.
- **LZ4 block format** — used for the original Java fallback decoder: https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md . The fallback implementation was checked with C#-generated vectors, including overlapping matches and malformed input.

Architectural references:

- UScreen Android pen handling and video backpressure: https://github.com/majmichu1/UScreen
- RFB rectangular updates: https://github.com/LibVNC/libvncserver
- Desktop Duplication: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
- Windows synthetic pen input: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput

The proprietary CSP/Aseprite applications and their content are not bundled.

Packaged components in the Windows portable release:

- .NET 8 / Windows Desktop runtime, self-contained deployment. Runtime notices and licenses are copied from the restored runtime packages into `licenses/` in the release. Single-file deployment documentation: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/ .
- Android platform-tools ADB plus its Windows USB DLLs. The complete installed platform-tools `NOTICE.txt` is included as `licenses/Android-platform-tools-NOTICE.txt` in the release. ADB source/notice: https://android.googlesource.com/platform/packages/modules/adb/ . The platform-tools version is recorded in `tools/source.properties`; binaries are copied unmodified.

The source repository excludes all signing keys, device-specific logs, screenshots and compiled packages. Original application code currently has no separately granted reuse license; third-party licenses above continue to apply to their respective components.
