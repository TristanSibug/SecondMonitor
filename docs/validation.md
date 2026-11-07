# Development validation

The reliability changes preserve the tablet wire protocol: two big-endian integers
for width/height, followed by a big-endian length and an Annex B H.264 access unit
per picture. The optional `-1` close signal is unchanged. No Android update is
required for the new framing on the PC's local ffmpeg pipe.

## Automated checks

With .NET SDK 10 installed, run from the repository root:

```sh
dotnet run --project tests/SecondMonitor.Tests.csproj -c Release
```

Include the real encoder/decoder integration tests when ffmpeg and ffprobe are in PATH:

```sh
dotnet run --project tests/SecondMonitor.Tests.csproj -c Release -- --ffmpeg
```

The dependency-free test executable returns nonzero on failure. It covers legacy
and current touch messages, nonfinite and oversized input, USB retry/backoff,
diagnostics, session isolation and serialized restart, subprocess failures/timeouts/
cancellation, fragmented AVI reads, odd padding, final frames, and corrupt/oversized
chunks. Integration tests encode and decode 30 frames with inline and global
encoder headers, and verify that a single raw input frame is delivered without
waiting for a second input frame or closing stdin.

ffmpeg writes AVI chunks only between its process and the PC app. This supplies
explicit packet lengths instead of requiring the next frame's AUD to delimit the
current one. The PC strips all AVI metadata before forwarding raw H.264. B frames
are disabled to keep packet order equal to presentation order. The rawvideo input
does not use `nobuffer`, which drops the initial probe frame in the integration test.

On Linux, cross-compile the Windows artifacts with:

```sh
dotnet publish pc/SecondMonitorServer.csproj -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true -o app
dotnet publish pc/SecondMonitorServer.csproj -c Release -r win-x64 --self-contained false -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=none -o app-single
```

On Windows, the existing build scripts remain supported. The single-file build
requires NuGet network access. ffmpeg needs its AVI muxer and H.264 bitstream
filters; standard full ffmpeg builds include them.

## Windows and tablet checks still required

These checks cannot run in the Linux development environment. Use a Windows
10/11 PC, .NET 10 Desktop Runtime, ffmpeg, adb, the virtual display driver and an
authorized USB-connected tablet running the existing APK.

1. Press **Check setup**. Verify monitor presence, ffmpeg encoder results and tablet
   app installation. Repeat with ffmpeg unavailable, USB authorization revoked,
   and the APK absent. The window should remain responsive and offer specific guidance.
2. Stream with CPU/Standard at 30 and 60 FPS. Verify video, reconnects, all touch
   actions, mouse fallback, and the close-tablet option. Record capture-to-display
   latency on actual hardware; the tests verify buffering behavior, not an
   end-to-end latency figure.
3. Make the first `adb reverse` attempt fail, then restore adb/USB access without
   changing the serial. Verify retries (3, 6, 12, 24, then at most 30 seconds) and
   successful reconnection. Successful tunnel/app setup gets a 10-second retry
   interval while no tablet client connects. Active clients are left alone.
4. Stop and immediately restart repeatedly, including during initial connection,
   GPU-output detection and adb calls. Verify no old-session status replaces the
   current one, no duplicate ffmpeg processes remain, and touch is released.
   When close-tablet is selected, Start stays disabled until closing finishes.
5. Move/resize the selected monitor and disable/re-enable it while streaming.
   Verify a reconnect with new dimensions, correct touch coordinates, and no
   fallback to a different display when the selected monitor is absent.
6. Repeat streaming and display changes with GPU capture and each available
   hardware encoder (AMD, Intel, NVIDIA). CPU-based automated tests cannot validate
   these device/driver-specific paths.
7. Exit normally, close to tray, log off and shut down. Verify that no input stays
   down and no adb is launched after shutdown starts.

Both Windows publish modes and all 16 regression/integration tests passed in the
Linux environment using .NET SDK 10.0.403 and ffmpeg 7.1.5. The Windows GUI,
USB hardware, display changes and hardware encoders have not been runtime-tested
there. Keep these distinct from the automated results before making a release.
