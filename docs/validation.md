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

Shutdown regression tests also cover a queued launch that passed an earlier check,
serialization of process creation with shutdown, Windows session-message handling,
aborted shutdown, cancellation of a real running helper process, diagnostics stopping
before adb, and the active session still receiving goodbye preparation. All helper
launches share the same guard; diagnostics do not bypass it.

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
7. Exit normally, close to tray, log off and shut down. Repeat while streaming,
   while USB recovery is retrying, and while **Check setup** is running. Verify that
   no input stays down, no new adb command starts after the shutdown notification,
   and no adb startup-error dialog blocks Windows. The tablet should receive goodbye
   over the existing connection, or close after its existing 10-second disconnect
   timeout if Windows terminates the adb tunnel first. No APK update is needed.

## Monitor connection toggle

Connection changes use QueryDisplayConfig, DisplayConfigGetDeviceInfo and
SetDisplayConfig. The selected target restores its last source dimensions, position, rotation and
refresh from monitor-layouts.json, keyed by Windows monitor device path. With no saved
layout it uses preferred dimensions beside the rightmost display. Windows chooses
compatible target timing. Current paths and modes for the other displays are
supplied unchanged, without SDC_ALLOW_CHANGES. The selector groups available paths
by target identity instead of showing every dormant GDI adapter/source. This also
supports reconnecting after restarting the app with no saved DEVMODE cache.

The Linux suite also exercises secondary-only selection, stopping before disconnect,
reconnecting the same device, failures to stop, topology changes during stop, shutdown,
closing cancellation, Windows refusal, and placement next to an offset desktop.
The native Windows display APIs are compiled but cannot be executed on Linux.

On Windows, use at least three displays (including the primary) and record their
positions, sizes and primary designation before testing:

1. The old tablet buttons are replaced by one **Connect monitor** / **Disconnect
   monitor** button beside **Start** and **Stop**. Selecting the primary disables
   the toggle. Disconnect a secondary while streaming: confirm streaming stops
   before it leaves the desktop and the close-tablet checkbox is honored.
2. Confirm the detached device remains selected/listed, **Start** and resolution
   changes are disabled for it, and **Connect monitor** restores its last resolution
   and desktop position, then starts streaming automatically. Confirm every other display retains its original
   mode, position and primary designation. Repeat with negative coordinates and
   a rightmost display above/below the primary.
3. Confirm unused DISPLAY2..DISPLAY7 source routes do not appear as phantom
   monitors, and only one row is shown per available disconnected target. Add/remove
   another display and confirm the existing selection never changes target identity.
   Close the app while the secondary is disconnected, then launch it with **Start
   streaming when opened** unchecked. Confirm it reconnects the saved secondary and
   starts streaming without a click. Reopening when it is already connected should
   still honor the checkbox. Confirm automatic tablet opening follows its checkbox. Repeat
   Stop/disconnect with automatic closing checked and unchecked.
4. Change the primary or unplug the selected display while Stop is pending.
   Confirm the operation refuses a newly primary/missing device and never
   substitutes another display. Test Windows refusal and shutdown/logoff during
   Stop: no queued connection operation or adb launch should start afterward.

5. Set a non-default resolution and a negative desktop position in Windows Display
   settings. Disconnect/reconnect and exit/relaunch with the display disconnected;
   confirm both settings survive. Repeat with display numbers changed and with
   another monitor present. An unavailable saved target must never be substituted.
6. Close or shut down during connection readiness polling; confirm no stream starts
   afterward. Simulate a rejected connection and verify no Start follows it. If a
   saved layout is no longer valid, confirm the error leaves other displays intact.

Both Windows publish modes and all 39 regression/integration tests passed in the
Linux environment using .NET SDK 10.0.403 and ffmpeg 7.1.5. The monitor tests exercise policy, ordering, native structure sizes, DisplayConfig
planning, target filtering, reconnection without GDI modes, and topology changes
using a simulated display API. The Windows GUI,
USB hardware, display changes, actual Windows shutdown/logoff messages, and hardware
encoders have not been runtime-tested there. Session-message tests simulate the
notification values, and the helper cancellation test uses a local test process.
Keep these distinct from the automated results before making a release.
