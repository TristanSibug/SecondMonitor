// ============================================================================
// StreamEngine  --  does the real work (no window code in here)
//
// What it does, in plain English:
//   1. Runs "adb reverse" so the tablet can reach this PC through the USB cable.
//   2. Waits for the tablet app to connect.
//   3. Starts ffmpeg (the video encoder). There are two ways to feed it:
//        STANDARD capture: we take screenshots ourselves and give them to ffmpeg.
//        GPU capture:      ffmpeg grabs the screen by itself using the graphics
//                          card (ddagrab), so almost nothing goes through the CPU.
//   4. Reads the H.264 video coming out of ffmpeg, cuts it into one piece per
//      picture, and sends every piece to the tablet.
//   5. Listens for touch messages from the tablet and moves/clicks the mouse.
//
// The window (MainForm) just tells this class what settings to use, and shows
// the messages it sends back (Log, Status, Stats).
// ============================================================================

using System;
using System.Collections.Concurrent;  // ConcurrentQueue (a list two threads can safely share)
using System.Collections.Generic;    // Dictionary (to remember each finger on the screen)
using System.ComponentModel;          // Win32Exception (the error when a program is not found)
using System.Diagnostics;             // Process (to run ffmpeg/adb), Stopwatch (timing)
using System.Drawing;                 // Bitmap, Graphics, Rectangle (for screenshots)
using System.Drawing.Imaging;         // PixelFormat, BitmapData
using System.Globalization;           // for parsing numbers like "0.5" the same on every PC
using System.IO;                      // streams (reading/writing bytes)
using System.Net;                     // IPAddress
using System.Net.Sockets;             // TcpListener, TcpClient (the network part)
using System.Runtime.InteropServices; // lets us call Windows functions (DllImport)
using System.Text.RegularExpressions; // to find "1280x800" inside ffmpeg's text
using System.Threading;               // Thread, Sleep
using System.Windows.Forms;           // Cursor, Cursors

class StreamEngine
{
    // ------------------------------------------------------------------
    // Fixed settings
    // ------------------------------------------------------------------
    const int Port = 7777;               // must match "adb reverse" and the Android app
    const string FfmpegPath = "ffmpeg";  // works if ffmpeg.exe is next to this program or in PATH

    // The tablet app's name, used to open and close it with adb.
    // If you rename the package in the Android project, change TabletPackage here too.
    const string TabletPackage = "com.example.secondmonitor";
    const string TabletAppComponent = TabletPackage + "/.MainActivity"; // "package name / activity name"

    // ------------------------------------------------------------------
    // Messages we send to the window. The window "subscribes" to these.
    // ------------------------------------------------------------------
    public event Action<string> Log;     // lines for the log box
    public event Action<string> Status;  // short status text ("Waiting for the tablet...")
    public event Action<string> Stats;   // the FPS text

    // ------------------------------------------------------------------
    // Windows functions we borrow from user32.dll / winmm.dll
    // ------------------------------------------------------------------

    // Windows normally wakes sleeping threads only every ~15 milliseconds.
    // That is too coarse for 60 frames per second (one frame = ~16 ms).
    // This asks Windows to wake us every 1 millisecond instead.
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint milliseconds);

    // Moves the mouse pointer to an exact pixel position on the whole desktop.
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    // Simulates mouse button presses (we use it for left button down / up).
    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // ------------------------------------------------------------------
    // Windows TOUCH INJECTION: lets a program pretend to be a touchscreen.
    // Windows then treats our fingers like real ones: swipe to scroll, pinch to zoom, etc.
    // These structures must match Windows' own layout EXACTLY, so do not rearrange them.
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType;            // 2 = touch
        public uint pointerId;              // which finger (0, 1, 2 ...)
        public uint frameId;
        public uint pointerFlags;           // down / moving / up
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;       // where on the screen (in pixels)
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public int buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;              // the little area the fingertip covers
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    // Turns touch injection on for this thread. maxCount = how many fingers at once.
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);

    // Sends the current state of all fingers to Windows.
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    const uint TOUCH_FEEDBACK_NONE = 0x3;       // do not draw touch circles on screen
    const uint PT_TOUCH = 2;                    // "this pointer is a touch"
    const uint POINTER_FLAG_INRANGE = 0x00000002;
    const uint POINTER_FLAG_INCONTACT = 0x00000004;
    const uint POINTER_FLAG_DOWN = 0x00010000;  // finger just touched
    const uint POINTER_FLAG_UPDATE = 0x00020000; // finger is still there (maybe moved)
    const uint POINTER_FLAG_UP = 0x00040000;    // finger just lifted
    const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    const uint TOUCH_MASK_ORIENTATION = 0x00000002;
    const uint TOUCH_MASK_PRESSURE = 0x00000004;
    const int MaxTouchPoints = 10;              // we follow up to 10 fingers

    // ------------------------------------------------------------------
    // Things this class remembers while it runs
    // ------------------------------------------------------------------
    AppSettings settings;                 // the options chosen in the window
    Rectangle monitorBounds;              // position + size of the monitor we share
    string monitorDeviceName;             // Windows' name for it, like \\.\DISPLAY8
    volatile bool running = false;        // true between Start() and Stop()
    volatile int generation = 0;          // goes up by 1 on every Start(), so old threads can tell they are outdated
    volatile TcpClient currentClient;     // the tablet connection (so Stop() can close it)
    volatile NetworkStream currentNet;    // the same connection as a stream (so we can say goodbye on it)
    readonly object netWriteLock = new object(); // only one thread may write to the tablet at a time

    // True once Windows is shutting down (or the user is logging off). From then on we never start
    // adb: Windows refuses to start new programs during shutdown and shows an "adb.exe" error.
    static volatile bool shuttingDown = false;
    volatile Process currentFfmpeg;       // the running ffmpeg (so Stop() can close it)
    volatile TcpListener currentListener; // the "door" waiting for the tablet (so Stop() can close it)
    volatile string deviceSerial;         // the plugged-in tablet's adb name (null = none known yet)
    bool adbMissingLogged = false;        // so we only tell you once that adb was not found
    bool touchErrorLogged = false;        // so we only tell you once that touch injection failed

    // Timing test (only used when "Show FPS number" is ticked, standard capture only).
    // The capture thread writes down the time each screenshot started; the sending thread
    // reads them back when that picture has been sent, and so measures how long the PC took.
    readonly ConcurrentQueue<long> frameStamps = new ConcurrentQueue<long>();

    // ==================================================================
    // Small helpers to send messages to the window
    // ==================================================================
    void WriteLog(string message)
    {
        Action<string> handler = Log;
        if (handler != null) handler(message);
    }

    void SetStatus(string message)
    {
        Action<string> handler = Status;
        if (handler != null) handler(message);
    }

    // ==================================================================
    // Start: called when you press the Start button.
    // Returns true if it started, false if something stopped it.
    // ==================================================================
    public bool Start(AppSettings newSettings, Screen screen)
    {
        if (running) return false;

        settings = newSettings;
        Rectangle bounds = screen.Bounds;
        monitorBounds = bounds;
        monitorDeviceName = screen.DeviceName; // remembered so we can find it again later

        // H.264 video needs an even width and height.
        if (bounds.Width % 2 != 0 || bounds.Height % 2 != 0)
        {
            WriteLog("The monitor size must be even numbers (like 1280x800).");
            return false;
        }

        // Ask Windows for 1 ms timer precision (see the comment on timeBeginPeriod)
        timeBeginPeriod(1);

        // Run below the game's priority (or normal priority), depending on the checkbox
        try
        {
            Process.GetCurrentProcess().PriorityClass =
                settings.LowerPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch
        {
            // Not allowed for some reason. Not important, keep going.
        }

        // Open a TCP "door" on this PC. IPAddress.Loopback = 127.0.0.1 = only this PC.
        // The USB cable reaches it because of "adb reverse".
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            WriteLog("Could not open port " + Port + ": " + ex.Message);
            WriteLog("Is another copy of this program still running?");
            return false;
        }

        currentListener = listener;
        generation++;                    // a new "version" of the run
        int myGeneration = generation;
        running = true;

        Thread listenThread = new Thread(() => ListenLoop(listener, myGeneration));
        listenThread.IsBackground = true; // dies automatically when the program closes
        listenThread.Start();

        // This thread watches for the tablet being plugged in. When it appears, it sets up the
        // USB tunnel ("adb reverse") and, if you want, opens the app on the tablet.
        Thread watchThread = new Thread(() => WatchDevices(myGeneration));
        watchThread.IsBackground = true;
        watchThread.Start();

        WriteLog("Sharing monitor " + monitorBounds.Width + "x" + monitorBounds.Height
            + " at (" + monitorBounds.X + "," + monitorBounds.Y + ")");
        WriteLog("Encoder: " + settings.Encoder + "   Capture: " + (settings.GpuCapture ? "GPU" : "standard")
            + "   Target: " + settings.Fps + " fps   Bitrate: " + settings.BitrateKbps + " kbit/s");
        SetStatus("Waiting for the tablet...");
        return true;
    }

    // ==================================================================
    // Stop: called when you press the Stop button (or close the window).
    // ==================================================================
    public void Stop()
    {
        running = false;
        generation++; // makes any old threads notice they are outdated

        // Closing the connection and killing ffmpeg makes every thread stop.
        try { if (currentClient != null) currentClient.Close(); } catch { }
        StopFfmpeg(currentFfmpeg);

        deviceSerial = null;

        // Closing the "door" also makes the thread that is waiting for a tablet stop waiting.
        try { if (currentListener != null) currentListener.Stop(); } catch { }

        SetStatus("Stopped");
        WriteLog("Stopped.");
    }

    // ==================================================================
    // SayGoodbye: tells the tablet app to close ITSELF, through the video connection.
    // No adb is needed, so this also works while Windows is shutting down.
    // (An old tablet app does not understand it and just reconnects later; harmless.)
    // ==================================================================
    public void SayGoodbye()
    {
        NetworkStream net = currentNet;
        if (net == null) return; // no tablet connected

        bool gotLock = false;
        try
        {
            // Wait at most half a second for the video sender to finish its current picture
            gotLock = Monitor.TryEnter(netWriteLock, 500);
            if (!gotLock) return;

            // A picture size of -1 is our secret "goodbye" signal
            byte[] bye = new byte[4];
            PutInt(bye, 0, -1);
            net.Write(bye, 0, 4);
            net.Flush();
        }
        catch
        {
            // The tablet is already gone. Nothing to do.
        }
        finally
        {
            if (gotLock) Monitor.Exit(netWriteLock);
        }

        Thread.Sleep(400); // give the message a moment to leave before the connection is closed
    }

    // ==================================================================
    // PrepareForShutdown: call this the moment Windows starts shutting down / logging off.
    // It stops all new adb runs and tells the tablet app to close itself.
    // ==================================================================
    public void PrepareForShutdown()
    {
        if (shuttingDown) return; // already done
        shuttingDown = true;
        SayGoodbye();
    }

    // ==================================================================
    // RunAdbReverse: runs "adb reverse tcp:7777 tcp:7777"
    // (public so the window's button can run it again after re-plugging the cable)
    // ==================================================================
    public void RunAdbReverse()
    {
        string text = RunAndCaptureText("adb", AdbPrefix() + "reverse tcp:" + Port + " tcp:" + Port, 8000);
        if (text == null)
        {
            WriteLog("Could not run adb. Is Android platform-tools in your PATH?");
        }
        else if (text.Trim().Length == 0 || text.Trim() == Port.ToString())
        {
            WriteLog("adb reverse OK (USB tunnel is ready).");
        }
        else
        {
            WriteLog("adb says: " + text.Trim());
        }
    }

    // ==================================================================
    // AdbPrefix: if we know which tablet is plugged in, adb commands must say
    // "-s <name>" so they go to that tablet (needed when more than one device is connected).
    // ==================================================================
    string AdbPrefix()
    {
        string serial = deviceSerial;
        return serial != null ? "-s " + serial + " " : "";
    }

    // ==================================================================
    // QueryDevice: asks adb which devices are plugged in.
    //   serial = the name of the first working device (or null)
    //   state  = "device" (ready), "unauthorized" (tablet must tap Allow),
    //            "offline", or "none" (nothing found / adb missing)
    // ==================================================================
    void QueryDevice(out string serial, out string state)
    {
        serial = null;
        state = "none";

        string text = RunAndCaptureText("adb", "devices", 8000);
        if (text == null)
        {
            if (shuttingDown) return; // Windows is shutting down: not an error worth reporting
            if (!adbMissingLogged)
            {
                WriteLog("Could not run adb. Is Android platform-tools in your PATH?");
                adbMissingLogged = true;
            }
            return;
        }

        // adb prints one line per device, like:   R52F123ABC	device
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*")) continue;

            string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (parts[1] == "device")
            {
                serial = parts[0];
                state = "device";
                return;
            }

            state = parts[1]; // "unauthorized" or "offline": remember it but keep looking
        }
    }

    // ==================================================================
    // WatchDevices: runs in the background while streaming is on.
    // Every few seconds it checks for the tablet. When the tablet APPEARS
    // (plugged in, or already plugged in when you pressed Start) it runs
    // "adb reverse" and opens the app on the tablet.
    // ==================================================================
    void WatchDevices(int myGeneration)
    {
        string lastSerial = null;       // the tablet we already set up (null = none)
        bool warnedUnauthorized = false;

        while (running && generation == myGeneration)
        {
            string serial;
            string state;
            QueryDevice(out serial, out state);

            if (state == "device")
            {
                warnedUnauthorized = false;
                if (serial != lastSerial)
                {
                    // A tablet just appeared
                    lastSerial = serial;
                    deviceSerial = serial;
                    WriteLog("Tablet detected (" + serial + ").");
                    RunAdbReverse();
                    if (settings.AutoOpenTabletApp) OpenTabletApp();
                }
            }
            else
            {
                if (lastSerial != null)
                {
                    WriteLog("Tablet unplugged.");
                    lastSerial = null;
                    deviceSerial = null;
                }

                if (state == "unauthorized" && !warnedUnauthorized)
                {
                    WriteLog("The tablet is waiting for you to allow USB debugging. Look at its screen and tap Allow.");
                    warnedUnauthorized = true;
                }
            }

            // Wait about 3 seconds, but in small pieces so we stop quickly when asked to
            for (int i = 0; i < 6 && running && generation == myGeneration; i++)
            {
                Thread.Sleep(500);
            }
        }
    }

    // ==================================================================
    // OpenTabletApp: opens the Second Monitor app on the tablet using adb.
    // (public so the window's "Open tablet app" button can use it)
    // ==================================================================
    public void OpenTabletApp()
    {
        string prefix = AdbPrefix();

        // Wake the tablet's screen first (key 224 = WAKEUP). Harmless if it is already on.
        RunAndCaptureText("adb", prefix + "shell input keyevent 224", 8000);

        // Start the app ("am start" = activity manager start)
        string text = RunAndCaptureText("adb", prefix + "shell am start -n " + TabletAppComponent, 8000);
        if (text == null)
        {
            WriteLog("Could not run adb. Is Android platform-tools in your PATH?");
        }
        else if (text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                 || text.IndexOf("no devices", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            WriteLog("Could not open the app on the tablet: " + text.Trim());
        }
        else
        {
            WriteLog("Opened the app on the tablet.");
        }
    }

    // ==================================================================
    // CloseTabletApp: closes the Second Monitor app on the tablet using adb
    // ("force-stop" = stop the app completely).
    // (public so the window can call it from a button, and when you press Stop)
    // ==================================================================
    public void CloseTabletApp()
    {
        string text = RunAndCaptureText("adb", AdbPrefix() + "shell am force-stop " + TabletPackage, 8000);
        if (text == null)
        {
            WriteLog("Could not run adb. Is Android platform-tools in your PATH?");
        }
        else if (text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                 || text.IndexOf("no devices", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            WriteLog("Could not close the app on the tablet: " + text.Trim());
        }
        else
        {
            WriteLog("Closed the app on the tablet.");
        }
    }

    // ==================================================================
    // RefreshMonitorBounds: finds our monitor again by its Windows name and
    // updates its position and size, in case you changed the display layout.
    // ==================================================================
    void RefreshMonitorBounds()
    {
        foreach (Screen s in Screen.AllScreens)
        {
            if (s.DeviceName == monitorDeviceName)
            {
                if (s.Bounds != monitorBounds)
                {
                    WriteLog("The monitor changed: now " + s.Bounds.Width + "x" + s.Bounds.Height
                        + " at (" + s.Bounds.X + "," + s.Bounds.Y + "). Using the new values.");
                    monitorBounds = s.Bounds;
                }
                return;
            }
        }

        WriteLog("Warning: the monitor " + monitorDeviceName + " was not found any more. Using the old values.");
    }

    // ==================================================================
    // ListenLoop: waits for a tablet, handles it, then waits for the next.
    // ==================================================================
    void ListenLoop(TcpListener myListener, int myGeneration)
    {
        try
        {
            while (running && generation == myGeneration)
            {
                TcpClient client;
                try
                {
                    client = myListener.AcceptTcpClient(); // waits here until the tablet connects
                }
                catch
                {
                    break; // the listener was closed
                }

                // If we were woken up by Stop(), leave without doing anything
                if (!running || generation != myGeneration)
                {
                    client.Close();
                    break;
                }

                currentClient = client;
                WriteLog("Tablet connected!");
                SetStatus("Tablet connected - streaming");
                Stopwatch sessionTimer = Stopwatch.StartNew();

                try
                {
                    HandleClient(client, myGeneration);
                }
                catch (Exception ex)
                {
                    // Happens normally when you unplug the cable or close the app
                    WriteLog("Connection ended: " + ex.Message);
                }
                finally
                {
                    client.Close();
                    currentClient = null;
                    currentNet = null;
                }

                // If it ended almost immediately, something is wrong (for example ffmpeg refused
                // to start). Wait a few seconds so the tablet does not retry every second
                // and fill the log with the same error.
                if (running && generation == myGeneration && sessionTimer.ElapsedMilliseconds < 3000)
                {
                    Thread.Sleep(3000);
                }

                if (running && generation == myGeneration)
                {
                    SetStatus("Waiting for the tablet...");
                    WriteLog("Waiting for the tablet again...");
                }
            }
        }
        finally
        {
            try { myListener.Stop(); } catch { }
        }
    }

    // ==================================================================
    // HandleClient: runs while one tablet is connected
    // ==================================================================
    void HandleClient(TcpClient client, int myGeneration)
    {
        client.NoDelay = true; // send small packets right away (less lag)
        NetworkStream net = client.GetStream();
        currentNet = net; // so SayGoodbye() can reach the tablet

        // The monitor may have been moved or resized in Windows' display settings since
        // we pressed Start, so look up its CURRENT position and size.
        RefreshMonitorBounds();
        if (monitorBounds.Width % 2 != 0 || monitorBounds.Height % 2 != 0)
        {
            WriteLog("The monitor size is now " + monitorBounds.Width + "x" + monitorBounds.Height
                + ". H.264 needs even numbers. Change its resolution in Windows display settings.");
            Thread.Sleep(3000);
            return;
        }

        int width = monitorBounds.Width;
        int height = monitorBounds.Height;

        // First thing the tablet receives: the video size (two 4-byte numbers).
        byte[] hello = new byte[8];
        PutInt(hello, 0, width);
        PutInt(hello, 4, height);
        net.Write(hello, 0, 8);

        // For GPU capture, ffmpeg needs to know WHICH screen (its own numbering).
        int gpuOutput = settings.GpuOutputIndex;
        if (settings.GpuCapture && gpuOutput < 0)
        {
            gpuOutput = DetectGpuOutput(width, height);
            if (gpuOutput < 0)
            {
                WriteLog("GPU capture: could not find the screen automatically.");
                WriteLog("Type its number in the 'GPU capture screen number' box, or switch to Standard capture.");
                Thread.Sleep(3000); // so we do not spam the log if the tablet keeps reconnecting
                return;
            }
            WriteLog("GPU capture will use screen number " + gpuOutput + ".");
        }

        // Start the video encoder program
        Process ffmpeg = StartFfmpeg(width, height, gpuOutput);
        currentFfmpeg = ffmpeg;

        // Thread 1: listens for touch messages from the tablet.
        // When the tablet goes away, this thread ends and also stops ffmpeg,
        // so everything else notices and ends too.
        Thread touchThread = new Thread(() =>
        {
            ReadTouchLoop(net);
            StopFfmpeg(ffmpeg);
        });
        touchThread.IsBackground = true;
        touchThread.Start();

        // Thread 2: shows ffmpeg's error messages in the log (helps when something is wrong)
        Thread errorThread = new Thread(() => EchoFfmpegErrors(ffmpeg));
        errorThread.IsBackground = true;
        errorThread.Start();

        // Thread 3: takes the encoded video out of ffmpeg and sends it to the tablet
        Thread pumpThread = new Thread(() => PumpVideoToTablet(ffmpeg, net));
        pumpThread.IsBackground = true;
        pumpThread.Start();

        try
        {
            if (settings.GpuCapture)
            {
                // ffmpeg captures the screen by itself, so there is nothing for us to feed it.
                // We just wait here until the video stops (tablet gone, ffmpeg closed, or Stop pressed).
                pumpThread.Join();
            }
            else
            {
                // Standard capture: this thread takes screenshots and feeds ffmpeg.
                // It runs until something breaks (tablet unplugged, ffmpeg closed...).
                CaptureLoop(ffmpeg, width, height);
            }
        }
        finally
        {
            StopFfmpeg(ffmpeg);
        }
    }

    // ==================================================================
    // StartFfmpeg: launches ffmpeg.exe with the right settings
    // ==================================================================
    Process StartFfmpeg(int width, int height, int gpuOutput)
    {
        string arguments = BuildFfmpegArguments(width, height, gpuOutput);
        WriteLog("Starting: ffmpeg " + arguments);

        ProcessStartInfo info = new ProcessStartInfo(FfmpegPath, arguments);
        info.UseShellExecute = false;        // needed so we can connect to its input/output
        info.RedirectStandardInput = true;   // standard capture: we WRITE raw pictures into ffmpeg
        info.RedirectStandardOutput = true;  // we READ encoded H.264 video out of ffmpeg
        info.RedirectStandardError = true;   // we READ ffmpeg's messages
        info.CreateNoWindow = true;          // no extra black window

        try
        {
            Process started = Process.Start(info);

            // Make ffmpeg lower priority than your game too (it does the heavy encoding work)
            if (settings.LowerPriority)
            {
                try { started.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch { /* not allowed for some reason; not important, keep going */ }
            }

            return started;
        }
        catch (Win32Exception)
        {
            WriteLog("Could not start ffmpeg. Put ffmpeg.exe next to SecondMonitor.exe,");
            WriteLog("or install it (winget install Gyan.FFmpeg) and restart this program.");
            throw;
        }
    }

    // ==================================================================
    // BuildFfmpegArguments: the text of options we give ffmpeg
    // ==================================================================
    string BuildFfmpegArguments(int width, int height, int gpuOutput)
    {
        string encoderName = settings.Encoder;
        int fps = settings.Fps;

        // ---------------- INPUT: where do the pictures come from? ----------------
        string input;
        if (!settings.GpuCapture)
        {
            // STANDARD: raw pictures arrive through the pipe ("-i -" = read from my input pipe).
            // "bgra" matches how Windows stores our screenshot pixels (Blue, Green, Red, Alpha).
            // nobuffer + low_delay = "don't hold pictures back, pass them on right away"
            input = "-hide_banner -loglevel warning -nostats -fflags nobuffer -flags low_delay "
                + "-f rawvideo -pix_fmt bgra "
                + "-video_size " + width + "x" + height + " "
                + "-framerate " + fps + " -i - ";
        }
        else
        {
            // GPU: ffmpeg's "ddagrab" filter grabs the screen using the graphics card.
            // The pictures stay in video memory, which is why this uses so little CPU.
            string capture = "ddagrab=output_idx=" + gpuOutput + ":framerate=" + fps + ":draw_mouse=1";
            string chain;
            string deviceOption = "";

            if (encoderName == "libx264" || encoderName == "h264_amf")
            {
                // The CPU encoder cannot read video memory, so copy each picture into normal memory.
                // AMD's encoder refused the video-memory picture on your PC (error 18),
                // so it gets the normal-memory picture too.
                chain = capture + ",hwdownload,format=bgra";
            }
            else if (encoderName == "h264_qsv")
            {
                // Intel's encoder wants its own kind of video-memory picture.
                chain = capture + ",hwmap=derive_device=qsv,format=qsv";
                deviceOption = "-init_hw_device d3d11va ";
            }
            else
            {
                // The NVIDIA encoder can take the video-memory picture directly.
                chain = capture;
            }

            // -nostdin = ffmpeg must not wait for keyboard input (we have no console)
            input = "-hide_banner -loglevel warning -nostats -nostdin " + deviceOption
                + "-filter_complex \"" + chain + "\" ";
        }

        // ---------------- ENCODER: which H.264 encoder, with "fast, no delay" settings ----------------
        string encoder;
        if (encoderName == "libx264")
        {
            // ultrafast + zerolatency = least possible delay. "main" profile = old tablets understand it.
            encoder = "-c:v libx264 -preset ultrafast -tune zerolatency -profile:v main -pix_fmt yuv420p";

            // Limit how many CPU threads the encoder uses (so your game keeps the rest)
            if (settings.EncoderThreads > 0)
            {
                encoder += " -threads " + settings.EncoderThreads;
            }
        }
        else if (encoderName == "h264_nvenc")
        {
            encoder = "-c:v h264_nvenc -preset p1 -tune ll -rc cbr -zerolatency 1 -profile:v main";
        }
        else if (encoderName == "h264_qsv")
        {
            encoder = "-c:v h264_qsv -preset veryfast -async_depth 1 -profile:v main";
        }
        else if (encoderName == "h264_amf")
        {
            // -usage lowlatency = "send each picture out as fast as possible"
            // -quality speed    = pick speed over compression
            encoder = "-c:v h264_amf -usage lowlatency -quality speed -profile:v main";

            // nv12 is the pixel layout AMD likes
            encoder += " -pix_fmt nv12";
        }
        else
        {
            encoder = "-c:v " + encoderName; // some other encoder name
        }

        // Hardware encoders do not always repeat the SPS/PPS settings block in the stream,
        // but the tablet needs it. These options make ffmpeg put it in front of every keyframe.
        // (libx264 already does this by itself, so we leave it alone.)
        string bitstreamFilters = "h264_metadata=aud=insert";
        if (encoderName != "libx264")
        {
            encoder += " -flags +global_header";
            bitstreamFilters = "dump_extra=freq=keyframe," + bitstreamFilters;
        }

        // ---------------- BITRATE: how much data per second the video may use ----------------
        string rate = " -b:v " + settings.BitrateKbps + "k -maxrate " + settings.BitrateKbps
            + "k -bufsize " + (settings.BitrateKbps / 2) + "k";

        // ---------------- OUTPUT ----------------
        //  -g                  = how often a full reference picture is made (once per second)
        //  h264_metadata=aud=insert = put a marker at the START of every picture so we can
        //                        cut the stream into one piece per picture
        //  -flush_packets 1    = hand data out immediately, don't wait to fill a buffer
        //  -f h264 pipe:1      = plain H.264 byte stream, written to our output pipe
        string output = " -g " + fps
            + " -bsf:v " + bitstreamFilters + " -flush_packets 1 -f h264 pipe:1";

        return input + encoder + rate + output;
    }

    // ==================================================================
    // DetectGpuOutput: finds which screen number ffmpeg's GPU capture uses
    // for our virtual monitor. We try screens 0, 1, 2... and look at the size
    // ffmpeg reports for each. The one with the same size as our monitor wins.
    // ==================================================================
    int DetectGpuOutput(int width, int height)
    {
        WriteLog("Looking for the GPU capture screen that is " + width + "x" + height + " ...");

        for (int i = 0; i < 8 && running; i++)
        {
            string args = "-hide_banner -loglevel info -nostdin -f lavfi "
                + "-i ddagrab=output_idx=" + i + ":framerate=2 -frames:v 1 -f null -";
            string text = RunAndCaptureText(FfmpegPath, args, 8000);
            if (text == null) return -1; // ffmpeg not found

            // ffmpeg prints a line like:  Stream #0:0: Video: wrapped_avframe, ..., 1280x800 ...
            Match m = Regex.Match(text, @"Video:[^\r\n]*?(\d{3,5})x(\d{3,5})");
            if (!m.Success) continue; // that screen number does not exist

            int w = int.Parse(m.Groups[1].Value);
            int h = int.Parse(m.Groups[2].Value);
            WriteLog("  GPU capture screen " + i + " is " + w + "x" + h);

            if (w == width && h == height) return i;
        }

        return -1;
    }

    // ==================================================================
    // RunAndCaptureText: runs a program, waits for it, returns everything it printed.
    // Returns null if the program could not be started at all.
    // ==================================================================
    static string RunAndCaptureText(string file, string arguments, int timeoutMs)
    {
        // During Windows shutdown, starting a program fails with an error pop-up. So do not even try.
        if (shuttingDown) return null;

        ProcessStartInfo info = new ProcessStartInfo(file, arguments);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        Process p;
        try
        {
            p = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return null; // program not found
        }

        // Read its output on two helper threads so it can never get stuck waiting for us
        string outText = "";
        string errText = "";
        Thread outReader = new Thread(() => { try { outText = p.StandardOutput.ReadToEnd(); } catch { } });
        Thread errReader = new Thread(() => { try { errText = p.StandardError.ReadToEnd(); } catch { } });
        outReader.Start();
        errReader.Start();

        // Wait for it to finish; if it takes too long, kill it
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
        }
        outReader.Join(1000);
        errReader.Join(1000);

        return outText + errText;
    }

    // ==================================================================
    // StopFfmpeg: closes ffmpeg (ignoring errors if it is already closed)
    // ==================================================================
    static void StopFfmpeg(Process ffmpeg)
    {
        if (ffmpeg == null) return;
        try
        {
            if (!ffmpeg.HasExited) ffmpeg.Kill();
        }
        catch
        {
            // already gone, nothing to do
        }
    }

    // ==================================================================
    // EchoFfmpegErrors: copies anything ffmpeg says (warnings/errors) into the log
    // ==================================================================
    void EchoFfmpegErrors(Process ffmpeg)
    {
        try
        {
            string line;
            while ((line = ffmpeg.StandardError.ReadLine()) != null)
            {
                WriteLog("[ffmpeg] " + line);
            }
        }
        catch
        {
            // ffmpeg closed, nothing to do
        }
    }

    // ==================================================================
    // CaptureLoop (STANDARD capture only):
    // screenshot -> raw bytes -> into ffmpeg, many times a second
    // ==================================================================
    void CaptureLoop(Process ffmpeg, int width, int height)
    {
        Stream toFfmpeg = ffmpeg.StandardInput.BaseStream;
        int fps = settings.Fps;

        // Bitmap + Graphics that we reuse every frame (making new ones each time would be slow)
        Bitmap shot = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        Graphics graphics = Graphics.FromImage(shot);

        // A plain byte array that holds one frame: width x height pixels, 4 bytes each
        byte[] frameBytes = new byte[width * height * 4];

        // Timing: we aim for one frame every (1/fps) second.
        // Stopwatch.GetTimestamp() counts in tiny "ticks"; Frequency = ticks per second.
        long ticksPerFrame = Stopwatch.Frequency / fps;
        long nextFrameAt = Stopwatch.GetTimestamp();

        // Timing test numbers (reported in the log every 5 seconds when "Show FPS number" is ticked)
        bool timing = settings.ShowFps;
        long sumShotTicks = 0, maxShotTicks = 0;   // time to take the screenshot and copy it
        long sumWriteTicks = 0, maxWriteTicks = 0; // time spent waiting to hand the frame to ffmpeg
        int timedFrames = 0;
        Stopwatch reportTimer = Stopwatch.StartNew();

        // Forget stamps left over from an earlier connection
        long oldStamp;
        while (frameStamps.TryDequeue(out oldStamp)) { }

        try
        {
            while (running)
            {
                long frameStart = Stopwatch.GetTimestamp(); // for the timing test

                // (a) Take a screenshot of just the virtual monitor
                graphics.CopyFromScreen(
                    monitorBounds.X, monitorBounds.Y, // where to start on the desktop
                    0, 0,                             // where to put it in our bitmap
                    monitorBounds.Size);              // how big

                // (b) Windows screenshots do NOT include the mouse pointer,
                // so we paint the pointer onto the screenshot ourselves.
                Point cursorPos = Cursor.Position;
                if (monitorBounds.Contains(cursorPos))
                {
                    Rectangle cursorBox = new Rectangle(
                        cursorPos.X - monitorBounds.X,
                        cursorPos.Y - monitorBounds.Y,
                        32, 32);
                    Cursors.Default.Draw(graphics, cursorBox);
                }

                // (c) Copy the pixels out of the bitmap into our byte array.
                // LockBits = "let me look at the raw pixel memory for a moment".
                BitmapData data = shot.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0, frameBytes, 0, frameBytes.Length);
                shot.UnlockBits(data);

                long shotDone = Stopwatch.GetTimestamp(); // for the timing test
                if (timing) frameStamps.Enqueue(frameStart); // the sending thread will match this to the picture

                // (d) Give the frame to ffmpeg. If ffmpeg is busy this waits, which
                // naturally slows us down to the speed of the encoder.
                toFfmpeg.Write(frameBytes, 0, frameBytes.Length);

                if (timing)
                {
                    long writeDone = Stopwatch.GetTimestamp();
                    long shotTicks = shotDone - frameStart;
                    long writeTicks = writeDone - shotDone;
                    sumShotTicks += shotTicks;
                    sumWriteTicks += writeTicks;
                    if (shotTicks > maxShotTicks) maxShotTicks = shotTicks;
                    if (writeTicks > maxWriteTicks) maxWriteTicks = writeTicks;
                    timedFrames++;

                    if (reportTimer.ElapsedMilliseconds >= 5000 && timedFrames > 0)
                    {
                        double toMs = 1000.0 / Stopwatch.Frequency; // ticks -> milliseconds
                        WriteLog("[timing] screenshot: average " + (sumShotTicks * toMs / timedFrames).ToString("F1", CultureInfo.InvariantCulture)
                            + " ms, worst " + (maxShotTicks * toMs).ToString("F1", CultureInfo.InvariantCulture)
                            + " ms   |   handing frame to ffmpeg: average " + (sumWriteTicks * toMs / timedFrames).ToString("F1", CultureInfo.InvariantCulture)
                            + " ms, worst " + (maxWriteTicks * toMs).ToString("F1", CultureInfo.InvariantCulture) + " ms");
                        sumShotTicks = 0; maxShotTicks = 0;
                        sumWriteTicks = 0; maxWriteTicks = 0;
                        timedFrames = 0;
                        reportTimer.Restart();
                    }
                }

                // (e) Wait until it is time for the next frame.
                nextFrameAt += ticksPerFrame;
                long now = Stopwatch.GetTimestamp();
                long waitTicks = nextFrameAt - now;
                if (waitTicks > 0)
                {
                    int waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                    if (waitMs > 0) Thread.Sleep(waitMs);
                }
                else if (waitTicks < -Stopwatch.Frequency / 10)
                {
                    // We fell more than 0.1 second behind: forget the backlog and start fresh.
                    nextFrameAt = now;
                }
            }
        }
        finally
        {
            graphics.Dispose();
            shot.Dispose();
        }
    }

    // ==================================================================
    // PumpVideoToTablet: reads H.264 bytes from ffmpeg, cuts them into
    // one piece per picture, and sends each piece to the tablet.
    //
    // How we know where one picture ends and the next begins:
    //   ffmpeg puts a small marker called an "AUD" at the start of every picture.
    //   In the byte stream it looks like:   00 00 01 09     (or 00 00 00 01 09)
    //   (The "09" says "this is an AUD".) So everything between two markers is
    //   exactly one picture.
    //
    // What we send to the tablet for each picture:
    //   [4 bytes: size][the picture's bytes]
    // ==================================================================
    void PumpVideoToTablet(Process ffmpeg, NetworkStream net)
    {
        Stream fromFfmpeg = ffmpeg.StandardOutput.BaseStream;

        byte[] readBuffer = new byte[64 * 1024];   // small piece we read each time
        byte[] pending = new byte[512 * 1024];     // bytes read but not yet sent (grows if needed)
        int pendingLength = 0;                     // how many bytes of 'pending' are used
        bool synced = false;                       // true once we have found the first marker
        byte[] sendBuffer = new byte[512 * 1024];  // [size][picture] we write to the network

        // FPS counter (only used when "Show FPS" is ticked in the window)
        int framesThisSecond = 0;
        long bytesThisSecond = 0;
        Stopwatch fpsTimer = Stopwatch.StartNew();

        // Timing test: how long from "screenshot started" until "picture sent to the tablet"
        long sumPcTicks = 0, maxPcTicks = 0;
        int pcFrames = 0;
        Stopwatch pcReportTimer = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                // Read whatever ffmpeg has produced (waits if nothing yet)
                int got = fromFfmpeg.Read(readBuffer, 0, readBuffer.Length);
                if (got <= 0) break; // ffmpeg closed its output

                // Add the new bytes to the end of 'pending' (make it bigger if it is full)
                if (pendingLength + got > pending.Length)
                {
                    Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + got));
                }
                Buffer.BlockCopy(readBuffer, 0, pending, pendingLength, got);
                pendingLength += got;

                // Cut out as many complete pictures as we can
                while (true)
                {
                    // Before we are synced, search from the very start.
                    // Once synced, byte 0 is the start of the current picture's own marker,
                    // so look for the NEXT marker after it (marker + header is at least 5 bytes).
                    int searchFrom = synced ? 5 : 0;
                    int marker = FindMarker(pending, searchFrom, pendingLength);
                    if (marker < 0) break; // no complete picture yet, read more bytes

                    // A marker may have a leading 00 (4-byte form). Include it in the boundary.
                    int boundary = marker;
                    if (marker > 0 && pending[marker - 1] == 0) boundary = marker - 1;

                    if (!synced)
                    {
                        // Throw away any bytes before the very first marker, then start properly.
                        RemoveFront(pending, ref pendingLength, boundary);
                        synced = true;
                        continue;
                    }

                    // 'pending' from 0 to boundary is exactly one complete picture. Send it.
                    int pictureSize = boundary;
                    if (sendBuffer.Length < pictureSize + 4)
                    {
                        sendBuffer = new byte[pictureSize + 4 + 64 * 1024];
                    }
                    PutInt(sendBuffer, 0, pictureSize);
                    Buffer.BlockCopy(pending, 0, sendBuffer, 4, pictureSize);
                    lock (netWriteLock)
                    {
                        net.Write(sendBuffer, 0, pictureSize + 4);
                    }

                    // Remove the sent picture from the front of 'pending'
                    RemoveFront(pending, ref pendingLength, boundary);

                    // Timing test (standard capture only: GPU capture has no screenshot time to compare with)
                    if (settings.ShowFps && !settings.GpuCapture)
                    {
                        // If pictures were skipped, the list of start times grows. Throw away the oldest ones.
                        long skipped;
                        while (frameStamps.Count > 20) frameStamps.TryDequeue(out skipped);

                        long startedAt;
                        if (frameStamps.TryDequeue(out startedAt))
                        {
                            long pcTicks = Stopwatch.GetTimestamp() - startedAt;
                            sumPcTicks += pcTicks;
                            if (pcTicks > maxPcTicks) maxPcTicks = pcTicks;
                            pcFrames++;

                            if (pcReportTimer.ElapsedMilliseconds >= 5000 && pcFrames > 0)
                            {
                                double toMs = 1000.0 / Stopwatch.Frequency;
                                WriteLog("[timing] PC total (screenshot start -> picture sent to tablet): average "
                                    + (sumPcTicks * toMs / pcFrames).ToString("F1", CultureInfo.InvariantCulture)
                                    + " ms, worst " + (maxPcTicks * toMs).ToString("F1", CultureInfo.InvariantCulture) + " ms");
                                sumPcTicks = 0; maxPcTicks = 0; pcFrames = 0;
                                pcReportTimer.Restart();
                            }
                        }
                    }

                    // FPS counter (only runs when the checkbox is ticked)
                    if (!settings.ShowFps) continue;
                    framesThisSecond++;
                    bytesThisSecond += pictureSize + 4;
                    if (fpsTimer.ElapsedMilliseconds >= 1000)
                    {
                        Action<string> statsHandler = Stats;
                        if (statsHandler != null)
                        {
                            statsHandler("FPS: " + framesThisSecond + "   data: " + (bytesThisSecond / 1024) + " KB/s");
                        }
                        framesThisSecond = 0;
                        bytesThisSecond = 0;
                        fpsTimer.Restart();
                    }
                }

                // Safety: if we never find a marker, something is wrong with ffmpeg's output.
                if (!synced && pendingLength > 2 * 1024 * 1024)
                {
                    WriteLog("No picture markers found in ffmpeg's output. Is your ffmpeg too old?");
                    break;
                }
            }
        }
        catch
        {
            // Tablet disconnected (network write failed) or ffmpeg closed. Fall through.
        }
        finally
        {
            // Make sure ffmpeg stops, so everything else notices and ends too.
            StopFfmpeg(ffmpeg);
        }
    }

    // ==================================================================
    // FindMarker: looks for the picture marker  00 00 01 09  in 'data'.
    // Searches positions from 'from' up to 'count'. Returns where the
    // "00 00 01" starts, or -1 if not found.
    // ==================================================================
    static int FindMarker(byte[] data, int from, int count)
    {
        for (int i = from; i + 3 < count; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1
                && (data[i + 3] & 0x1F) == 9)   // the low 5 bits = 9 means "AUD"
            {
                return i;
            }
        }
        return -1;
    }

    // ==================================================================
    // RemoveFront: deletes the first 'amount' bytes of 'data' by sliding
    // the rest down to the start.
    // ==================================================================
    static void RemoveFront(byte[] data, ref int length, int amount)
    {
        if (amount <= 0) return;
        Buffer.BlockCopy(data, amount, data, 0, length - amount); // BlockCopy handles overlap safely
        length -= amount;
    }

    // ==================================================================
    // PutInt: writes a whole number into 4 bytes, "big endian"
    // (most important byte first). Java's readInt() expects this.
    // ==================================================================
    static void PutInt(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    // ==================================================================
    // ReadTouchLoop: listens for touch lines from the tablet.
    //   New format:    "T D 1 0.25 0.50"   (T, action, finger number, x, y)
    //   Old format:    "D 0.25 0.50"       (action, x, y: one finger only)
    //   action: D = finger down, M = finger moved, U = finger lifted
    //   x and y are fractions of the screen (0.0 = left/top, 1.0 = right/bottom)
    // ==================================================================
    void ReadTouchLoop(NetworkStream stream)
    {
        TouchInjector injector = null;
        int mouseFinger = -1; // in mouse mode: which finger is acting as the mouse (-1 = none)

        try
        {
            // Start real touch input if the option is ticked
            if (settings.TouchScreenInput)
            {
                TouchInjector candidate = new TouchInjector();
                string message;
                if (candidate.Init(out message)) injector = candidate;
                WriteLog(message);
                if (injector == null) WriteLog("Falling back to mouse mode (one finger = mouse).");
            }
            else
            {
                WriteLog("Mouse mode: one finger = left mouse button.");
            }

            StreamReader reader = new StreamReader(stream);
            while (true)
            {
                string line = reader.ReadLine(); // waits for one full line
                if (line == null) return;        // null = tablet disconnected

                string[] parts = line.Split(' ');
                char action;
                int id;
                float fx, fy;

                if (parts.Length == 5 && parts[0] == "T")
                {
                    action = parts[1][0];
                    id = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    fx = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    fy = float.Parse(parts[4], CultureInfo.InvariantCulture);
                }
                else if (parts.Length == 3) // old app: a single finger
                {
                    action = parts[0][0];
                    id = 0;
                    fx = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    fy = float.Parse(parts[2], CultureInfo.InvariantCulture);
                }
                else
                {
                    continue; // ignore garbage
                }

                // Convert the 0..1 fractions into real desktop pixels on OUR monitor.
                int px = monitorBounds.X + (int)(fx * (monitorBounds.Width - 1));
                int py = monitorBounds.Y + (int)(fy * (monitorBounds.Height - 1));

                if (injector != null)
                {
                    if (!injector.Handle(action, id, px, py))
                    {
                        WriteLog("Windows refused the touch input (error " + injector.LastError + "). Switching to mouse mode.");
                        injector.ReleaseAll();
                        injector = null;
                    }
                    else
                    {
                        continue; // handled as real touch
                    }
                }

                // ---- Mouse mode: only the first finger counts ----
                if (action == 'D' && mouseFinger == -1)
                {
                    mouseFinger = id;
                    SetCursorPos(px, py);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                }
                else if (id == mouseFinger)
                {
                    SetCursorPos(px, py);
                    if (action == 'U')
                    {
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        mouseFinger = -1;
                    }
                }
            }
        }
        catch
        {
            // Connection closed. Nothing to do.
        }
        finally
        {
            // Never leave a finger or the mouse button stuck down
            if (injector != null) injector.ReleaseAll();
            if (mouseFinger != -1) mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
