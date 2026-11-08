// ============================================================================
// MainForm  --  the settings window
//
// What it does, in plain English:
//   - Shows drop-down lists and tick boxes for every option.
//   - Fills them with the options you used last time (from settings.txt).
//   - Can change the virtual monitor's resolution (the drop-down lists only
//     the modes your virtual display driver offers).
//   - When you press Start, it reads your choices, saves them, and tells the
//     StreamEngine to start.
//   - Automation: it can start with Windows, start streaming by itself, hide
//     in the system tray, and open the app on the tablet by itself.
//   - It shows the engine's messages (status, FPS, log) in the window.
//
// There is no "designer" file: every control is created here in code, so you
// can read exactly what is on the window.
// ============================================================================

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32; // Registry (used for "Run when Windows starts")

class MainForm : Form
{
    // ------------------------------------------------------------------
    // The engine that does the real work, and the options being used
    // ------------------------------------------------------------------
    StreamEngine engine = new StreamEngine(serial => new StreamSession(serial));
    AppSettings settings;

    // ------------------------------------------------------------------
    // The controls on the window
    // ------------------------------------------------------------------
    ComboBox monitorBox;          // drop-down: which monitor
    ComboBox resolutionBox;       // drop-down: which resolution to set on that monitor
    ComboBox encoderBox;          // drop-down: CPU / AMD / Intel / NVIDIA
    ComboBox captureBox;          // drop-down: standard or GPU capture
    NumericUpDown fpsBox;         // number box: frames per second
    NumericUpDown bitrateBox;     // number box: video quality budget
    NumericUpDown threadsBox;     // number box: CPU threads for libx264
    NumericUpDown gpuOutputBox;   // number box: GPU capture screen number
    CheckBox priorityCheck;       // tick box: lower priority
    CheckBox showFpsCheck;        // tick box: show FPS
    CheckBox autoOpenCheck;       // tick box: open the tablet app automatically
    CheckBox touchCheck;          // tick box: real touchscreen input (swipe to scroll)
    CheckBox closeTabletCheck;    // tick box: close the tablet app when streaming stops
    CheckBox runWithWindowsCheck; // tick box: run when Windows starts
    CheckBox autoStartCheck;      // tick box: start streaming when the program opens
    CheckBox startInTrayCheck;    // tick box: start hidden in the tray
    CheckBox closeToTrayCheck;    // tick box: closing the window keeps it running in the tray
    Button refreshButton;         // button: refresh the monitor list
    Button applyResButton;        // button: apply the chosen resolution now
    Button checkSetupButton;
    Button startButton;
    Button stopButton;
    Button openTabletButton;      // button: open the app on the tablet now
    Button closeTabletButton;     // button: close the app on the tablet now
    Label statusLabel;
    Label statsLabel;
    TextBox logBox;               // the big text area at the bottom

    // The little icon near the clock (system tray)
    NotifyIcon trayIcon;

    // The monitors Windows knows about (filled by FillMonitorList)
    Screen[] screens;

    // true while the engine is streaming (the option controls are locked then)
    bool isRunningNow = false;
    bool checkingSetup = false;

    // true while we are refilling the lists ourselves, so the "selection changed"
    // handlers do not react to our own changes
    bool loading = false;

    // false = the window must stay hidden (used for "start hidden in the tray")
    bool allowVisible = true;

    // true when the program is really being closed (Exit in the tray menu)
    bool exitRequested = false;
    volatile bool showRequested = false;   // set when a second copy of the program asks us to show the window
    System.Windows.Forms.Timer showTimer;  // checks that flag a few times a second
    EventHandler displayChangedHandler;
    Microsoft.Win32.SessionEndingEventHandler sessionEndingHandler; // told when Windows shuts down / logs off

    // The encoder choices. Both lists are in the SAME order:
    // item 0 of the names goes with item 0 of the codes, and so on.
    static readonly string[] EncoderNames = new string[]
    {
        "CPU - libx264 (works on every PC)",
        "AMD graphics - h264_amf",
        "Intel graphics - h264_qsv",
        "NVIDIA graphics - h264_nvenc"
    };
    static readonly string[] EncoderCodes = new string[]
    {
        "libx264",
        "h264_amf",
        "h264_qsv",
        "h264_nvenc"
    };

    // The first item of the resolution drop-down
    const string LeaveAsIs = "Leave as is";

    // Where Windows keeps the list of programs that start with Windows (for the current user)
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "SecondMonitor";

    // ==================================================================
    // Constructor: runs once when the window is created
    // ==================================================================
    public MainForm()
    {
        settings = AppSettings.Load();

        // If "start hidden in the tray" is ticked, the window must not appear when the program opens
        allowVisible = !settings.StartInTray;

        // Window basics
        Text = "Second Monitor";
        AutoScaleDimensions = new SizeF(96F, 96F); // lets the window grow on high-DPI screens
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 670);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        BuildControls();
        BuildTrayIcon();
        FillMonitorList();
        LoadControlsFromSettings();
        SetRunning(false);

        // From now on, react when you change these
        monitorBox.SelectedIndexChanged += OnMonitorChanged;
        runWithWindowsCheck.CheckedChanged += OnRunWithWindowsChanged;

        // Listen to the engine's messages. The engine runs on other threads,
        // and only the window's own thread may touch controls, so every message
        // goes through RunOnUi() first.
        engine.Log += delegate (string message) { RunOnUi(delegate { AppendLog(message); }); };
        engine.Status += delegate (string message) { RunOnUi(delegate { statusLabel.Text = "Status: " + message; }); };
        engine.Stats += delegate (string message) { RunOnUi(delegate { statsLabel.Text = message; }); };

        FormClosing += OnFormClosing;
        displayChangedHandler = delegate { RunOnUi(delegate { engine.DisplayChanged(); }); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += displayChangedHandler;

        // Windows tells us BEFORE it shuts down or logs off. React at once: stop using adb
        // (Windows cannot start programs any more) and tell the tablet app to close itself.
        sessionEndingHandler = delegate (object sender, Microsoft.Win32.SessionEndingEventArgs args)
        {
            engine.PrepareForShutdown();
        };
        Microsoft.Win32.SystemEvents.SessionEnding += sessionEndingHandler;

        // If someone opens SecondMonitor.exe a second time, that copy closes itself and asks THIS
        // copy to show its window instead. This timer notices the request.
        showTimer = new System.Windows.Forms.Timer();
        showTimer.Interval = 300;
        showTimer.Tick += delegate
        {
            if (showRequested)
            {
                showRequested = false;
                ShowFromTray();
            }
        };
        showTimer.Start();

        // Make sure the window's handle exists even if the window stays hidden,
        // otherwise the messages above could not reach the log.
        IntPtr handle = Handle;

        // "Start streaming when the program opens"
        if (settings.AutoStartStreaming)
        {
            TryStart(false);
        }
    }

    // ==================================================================
    // SetVisibleCore: Windows Forms calls this when the window is about to be shown.
    // We use it to keep the window hidden at startup (tray mode).
    // ==================================================================
    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(allowVisible && value);
    }

    // ==================================================================
    // BuildControls: creates every control and puts it on the window
    // ==================================================================
    void BuildControls()
    {
        // Each row: a label on the left (x = 16) and its control on the right (x = 250).

        AddLabel("Monitor to send to the tablet:", 20);
        monitorBox = new ComboBox();
        monitorBox.DropDownStyle = ComboBoxStyle.DropDownList; // pick from the list only
        monitorBox.SetBounds(250, 16, 220, 24);
        Controls.Add(monitorBox);

        refreshButton = new Button();
        refreshButton.Text = "Refresh";
        refreshButton.SetBounds(478, 15, 66, 26);
        refreshButton.Click += delegate { RefreshMonitors(); };
        Controls.Add(refreshButton);

        AddLabel("Monitor resolution:", 54);
        resolutionBox = new ComboBox();
        resolutionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        resolutionBox.SetBounds(250, 50, 220, 24);
        Controls.Add(resolutionBox);

        applyResButton = new Button();
        applyResButton.Text = "Apply";
        applyResButton.SetBounds(478, 49, 66, 26);
        applyResButton.Click += delegate { ApplySelectedResolution(); };
        Controls.Add(applyResButton);

        AddLabel("Video encoder:", 88);
        encoderBox = new ComboBox();
        encoderBox.DropDownStyle = ComboBoxStyle.DropDownList;
        encoderBox.Items.AddRange(EncoderNames);
        encoderBox.SetBounds(250, 84, 294, 24);
        Controls.Add(encoderBox);

        AddLabel("Screen capture:", 122);
        captureBox = new ComboBox();
        captureBox.DropDownStyle = ComboBoxStyle.DropDownList;
        captureBox.Items.Add("Standard (CPU capture, most compatible)");
        captureBox.Items.Add("GPU capture (experimental, lightest)");
        captureBox.SetBounds(250, 118, 294, 24);
        Controls.Add(captureBox);

        AddLabel("Frames per second:", 156);
        fpsBox = AddNumber(250, 152, 10, 120, 5);

        AddLabel("Bitrate (kbit/s):", 190);
        bitrateBox = AddNumber(250, 186, 1000, 50000, 1000);

        AddLabel("CPU encoder threads (0 = auto):", 224);
        threadsBox = AddNumber(250, 220, 0, 16, 1);

        AddLabel("GPU capture screen number (-1 = auto):", 258);
        gpuOutputBox = AddNumber(250, 254, -1, 15, 1);

        // Tick boxes, in two columns (left x = 16, right x = 282)
        priorityCheck = AddCheck("Lower priority (game gets CPU first)", 16, 290);
        showFpsCheck = AddCheck("Show FPS number", 16, 316);
        autoOpenCheck = AddCheck("Open tablet app automatically", 16, 342);
        runWithWindowsCheck = AddCheck("Run when Windows starts", 16, 368);
        closeTabletCheck = AddCheck("Close tablet app when stopped", 16, 394);

        autoStartCheck = AddCheck("Start streaming when opened", 282, 290);
        startInTrayCheck = AddCheck("Start hidden in the tray", 282, 316);
        closeToTrayCheck = AddCheck("Keep running in tray when closed", 282, 342);
        touchCheck = AddCheck("Real touchscreen input (swipe)", 282, 368);

        checkSetupButton = new Button();
        checkSetupButton.Text = "Check setup";
        checkSetupButton.SetBounds(282, 394, 130, 26);
        checkSetupButton.Click += OnCheckSetupClicked;
        Controls.Add(checkSetupButton);

        startButton = new Button();
        startButton.Text = "Start";
        startButton.SetBounds(16, 430, 92, 32);
        startButton.Click += OnStartClicked;
        Controls.Add(startButton);

        stopButton = new Button();
        stopButton.Text = "Stop";
        stopButton.SetBounds(114, 430, 92, 32);
        stopButton.Click += OnStopClicked;
        Controls.Add(stopButton);

        openTabletButton = new Button();
        openTabletButton.Text = "Open tablet app";
        openTabletButton.SetBounds(212, 430, 160, 32);
        openTabletButton.Click += OnOpenTabletClicked;
        Controls.Add(openTabletButton);

        closeTabletButton = new Button();
        closeTabletButton.Text = "Close tablet app";
        closeTabletButton.SetBounds(384, 430, 160, 32);
        closeTabletButton.Click += OnCloseTabletClicked;
        Controls.Add(closeTabletButton);

        statusLabel = new Label();
        statusLabel.Text = "Status: Stopped";
        statusLabel.SetBounds(16, 474, 528, 20);
        Controls.Add(statusLabel);

        statsLabel = new Label();
        statsLabel.Text = "";
        statsLabel.SetBounds(16, 496, 528, 20);
        Controls.Add(statsLabel);

        logBox = new TextBox();
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.SetBounds(16, 524, 528, 130);
        Controls.Add(logBox);
    }

    // Small helper: adds a text label at the left of a row
    void AddLabel(string text, int y)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(16, y, 230, 20);
        Controls.Add(label);
    }

    // Small helper: adds a number box (with up/down arrows)
    NumericUpDown AddNumber(int x, int y, int min, int max, int step)
    {
        NumericUpDown box = new NumericUpDown();
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = step;
        box.SetBounds(x, y, 100, 24);
        Controls.Add(box);
        return box;
    }

    // Small helper: adds a tick box
    CheckBox AddCheck(string text, int x, int y)
    {
        CheckBox box = new CheckBox();
        box.Text = text;
        box.SetBounds(x, y, 262, 24);
        Controls.Add(box);
        return box;
    }

    // ==================================================================
    // BuildTrayIcon: the little icon near the clock. Double-click to open the
    // window; right-click for a menu.
    // ==================================================================
    void BuildTrayIcon()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Open window", null, delegate { ShowFromTray(); });
        menu.Items.Add("Exit", null, delegate { exitRequested = true; Close(); });

        trayIcon = new NotifyIcon();
        trayIcon.Icon = LoadAppIcon(SystemInformation.SmallIconSize); // your icon.ico (or a plain icon if there is none)
        Icon = LoadAppIcon(SystemInformation.SmallIconSize);        // the same icon in the window's title bar
        trayIcon.Text = "Second Monitor";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += delegate { ShowFromTray(); };
        trayIcon.Visible = true;
    }

    // ==================================================================
    // LoadAppIcon: loads "icon.ico" (it is put inside the program when you build it).
    // If there is no icon.ico, a plain built-in icon is used instead.
    // 'size' picks the best-fitting picture inside the .ico file, so the icon stays sharp.
    // ==================================================================
    static Icon LoadAppIcon(Size size)
    {
        try
        {
            using (System.IO.Stream stream =
                typeof(MainForm).Assembly.GetManifestResourceStream("icon.ico"))
            {
                if (stream != null) return new Icon(stream, size);
            }
        }
        catch
        {
            // Broken icon file: fall through to the plain icon.
        }
        return SystemIcons.Application;
    }

    // Called (from another thread) when a second copy of the program was started.
    // It only sets a flag; the timer above then shows the window on the window's own thread.
    public void RequestShow()
    {
        showRequested = true;
    }

    // Brings the window back from the tray
    void ShowFromTray()
    {
        allowVisible = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ==================================================================
    // "Run when Windows starts": adds/removes this program in Windows' startup list.
    // ==================================================================
    static bool IsRunWithWindows()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }
        catch
        {
            return false;
        }
    }

    void SetRunWithWindows(bool enable)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;

                if (enable)
                {
                    // Application.ExecutablePath = the full path of this .exe
                    key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    AppendLog("Will start with Windows.");
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                    AppendLog("Will no longer start with Windows.");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog("Could not change the Windows startup setting: " + ex.Message);
        }
    }

    void OnRunWithWindowsChanged(object sender, EventArgs e)
    {
        if (loading) return;
        SetRunWithWindows(runWithWindowsCheck.Checked);
    }

    // ==================================================================
    // FillMonitorList: lists every monitor Windows knows about
    // ==================================================================
    void FillMonitorList()
    {
        screens = Screen.AllScreens;
        monitorBox.Items.Clear();

        for (int i = 0; i < screens.Length; i++)
        {
            Screen s = screens[i];
            string name = "[" + i + "] " + s.DeviceName.Replace("\\\\.\\", "")
                + "  " + s.Bounds.Width + "x" + s.Bounds.Height;
            if (s.Primary) name += "  (main screen)";
            monitorBox.Items.Add(name);
        }

        if (monitorBox.Items.Count > 0 && monitorBox.SelectedIndex < 0)
        {
            monitorBox.SelectedIndex = 0;
        }
    }

    // Picks the monitor you used last time. We look for it by its Windows name first
    // (the list numbers can change between restarts), then by list number. If neither
    // fits, pick the first monitor that is not the main screen.
    void LoadMonitorSelection()
    {
        if (settings.MonitorName.Length > 0)
        {
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].DeviceName == settings.MonitorName)
                {
                    monitorBox.SelectedIndex = i;
                    return;
                }
            }
        }

        int wanted = settings.MonitorIndex;
        if (wanted >= 0 && wanted < screens.Length)
        {
            monitorBox.SelectedIndex = wanted;
            return;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            if (!screens[i].Primary)
            {
                monitorBox.SelectedIndex = i;
                return;
            }
        }

        if (monitorBox.Items.Count > 0) monitorBox.SelectedIndex = 0;
    }

    // ==================================================================
    // RefreshMonitors: re-reads the monitor list (and the resolution list),
    // keeping the monitor and resolution you had picked if they still exist.
    // ==================================================================
    void RefreshMonitors()
    {
        // Remember what is chosen now
        string keepName = null;
        if (screens != null && monitorBox.SelectedIndex >= 0 && monitorBox.SelectedIndex < screens.Length)
        {
            keepName = screens[monitorBox.SelectedIndex].DeviceName;
        }
        ModeInfo keepMode = resolutionBox.SelectedItem as ModeInfo;
        string keepKey = keepMode != null ? keepMode.Key : "";

        loading = true;
        FillMonitorList();

        // Find the same monitor again by its Windows name
        if (keepName != null)
        {
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].DeviceName == keepName)
                {
                    monitorBox.SelectedIndex = i;
                    break;
                }
            }
        }

        FillResolutionList();
        SelectResolutionByKey(keepKey);
        loading = false;
    }

    // ==================================================================
    // FillResolutionList: fills the resolution drop-down with the modes
    // the selected monitor offers.
    // ==================================================================
    void FillResolutionList()
    {
        resolutionBox.Items.Clear();
        resolutionBox.Items.Add(LeaveAsIs);

        if (screens != null && monitorBox.SelectedIndex >= 0 && monitorBox.SelectedIndex < screens.Length)
        {
            Screen s = screens[monitorBox.SelectedIndex];
            foreach (ModeInfo mode in DisplayModes.List(s.DeviceName))
            {
                resolutionBox.Items.Add(mode);
            }
        }

        resolutionBox.SelectedIndex = 0; // "Leave as is"
        UpdateResolutionControls();
    }

    // Selects the item whose key matches (like "1280x800@60"). Does nothing if there is none.
    void SelectResolutionByKey(string key)
    {
        if (key == null || key.Length == 0) return;

        for (int i = 0; i < resolutionBox.Items.Count; i++)
        {
            ModeInfo mode = resolutionBox.Items[i] as ModeInfo;
            if (mode != null && mode.Key == key)
            {
                resolutionBox.SelectedIndex = i;
                return;
            }
        }
    }

    // The resolution controls are only usable when we are NOT streaming and the
    // selected monitor is NOT your main screen (we never change your main screen).
    void UpdateResolutionControls()
    {
        bool canChange = !isRunningNow;

        if (screens != null && monitorBox.SelectedIndex >= 0 && monitorBox.SelectedIndex < screens.Length
            && screens[monitorBox.SelectedIndex].Primary)
        {
            canChange = false;
        }

        resolutionBox.Enabled = canChange;
        applyResButton.Enabled = canChange;
    }

    // Runs when you pick a different monitor in the list
    void OnMonitorChanged(object sender, EventArgs e)
    {
        if (loading) return;
        FillResolutionList();
    }

    // ==================================================================
    // ApplySelectedResolution: changes the selected monitor to the chosen
    // resolution. Returns true if that worked (or if there was nothing to do).
    // ==================================================================
    bool ApplySelectedResolution()
    {
        ModeInfo mode = resolutionBox.SelectedItem as ModeInfo;
        if (mode == null)
        {
            AppendLog("Resolution: leaving the monitor as it is.");
            return true;
        }

        if (screens == null || monitorBox.SelectedIndex < 0 || monitorBox.SelectedIndex >= screens.Length)
        {
            AppendLog("Pick a monitor first.");
            return false;
        }

        Screen s = screens[monitorBox.SelectedIndex];
        if (s.Primary)
        {
            AppendLog("That is your main screen. This program only changes the virtual monitor.");
            return false;
        }

        string message;
        bool ok = DisplayModes.Apply(s.DeviceName, mode, out message);
        AppendLog(message);

        if (ok)
        {
            // Give Windows a moment to finish switching, then re-read the monitors
            Thread.Sleep(1000);
            RefreshMonitors();
        }

        return ok;
    }

    // ==================================================================
    // LoadControlsFromSettings: shows the saved options in the controls
    // ==================================================================
    void LoadControlsFromSettings()
    {
        LoadMonitorSelection();
        FillResolutionList();
        SelectResolutionByKey(settings.Resolution);

        int encoderIndex = Array.IndexOf(EncoderCodes, settings.Encoder);
        encoderBox.SelectedIndex = encoderIndex >= 0 ? encoderIndex : 0;

        captureBox.SelectedIndex = settings.GpuCapture ? 1 : 0;

        fpsBox.Value = Clamp(settings.Fps, fpsBox);
        bitrateBox.Value = Clamp(settings.BitrateKbps, bitrateBox);
        threadsBox.Value = Clamp(settings.EncoderThreads, threadsBox);
        gpuOutputBox.Value = Clamp(settings.GpuOutputIndex, gpuOutputBox);
        priorityCheck.Checked = settings.LowerPriority;
        showFpsCheck.Checked = settings.ShowFps;
        autoOpenCheck.Checked = settings.AutoOpenTabletApp;
        touchCheck.Checked = settings.TouchScreenInput;
        closeTabletCheck.Checked = settings.CloseTabletOnStop;
        autoStartCheck.Checked = settings.AutoStartStreaming;
        startInTrayCheck.Checked = settings.StartInTray;
        closeToTrayCheck.Checked = settings.CloseToTray;

        // This one is not in settings.txt: Windows itself remembers it
        runWithWindowsCheck.Checked = IsRunWithWindows();
    }

    // Keeps a number inside what the number box allows (so a bad saved value cannot crash us)
    static decimal Clamp(int value, NumericUpDown box)
    {
        if (value < box.Minimum) return box.Minimum;
        if (value > box.Maximum) return box.Maximum;
        return value;
    }

    // ==================================================================
    // ReadControlsIntoSettings: copies what you chose into the settings
    // ==================================================================
    void ReadControlsIntoSettings()
    {
        settings.MonitorIndex = monitorBox.SelectedIndex;
        if (screens != null && monitorBox.SelectedIndex >= 0 && monitorBox.SelectedIndex < screens.Length)
        {
            settings.MonitorName = screens[monitorBox.SelectedIndex].DeviceName;
        }

        ModeInfo mode = resolutionBox.SelectedItem as ModeInfo;
        settings.Resolution = mode != null ? mode.Key : "";

        settings.Encoder = EncoderCodes[encoderBox.SelectedIndex];
        settings.GpuCapture = captureBox.SelectedIndex == 1;
        settings.Fps = (int)fpsBox.Value;
        settings.BitrateKbps = (int)bitrateBox.Value;
        settings.EncoderThreads = (int)threadsBox.Value;
        settings.GpuOutputIndex = (int)gpuOutputBox.Value;
        settings.LowerPriority = priorityCheck.Checked;
        settings.ShowFps = showFpsCheck.Checked;
        settings.AutoOpenTabletApp = autoOpenCheck.Checked;
        settings.TouchScreenInput = touchCheck.Checked;
        settings.CloseTabletOnStop = closeTabletCheck.Checked;
        settings.AutoStartStreaming = autoStartCheck.Checked;
        settings.StartInTray = startInTrayCheck.Checked;
        settings.CloseToTray = closeToTrayCheck.Checked;
    }

    // ==================================================================
    // SetRunning: while streaming, the option controls are locked
    // (change them by pressing Stop first)
    // ==================================================================
    void SetRunning(bool isRunning)
    {
        isRunningNow = isRunning;
        checkSetupButton.Enabled = !isRunning && !checkingSetup;

        monitorBox.Enabled = !isRunning;
        refreshButton.Enabled = !isRunning;
        encoderBox.Enabled = !isRunning;
        captureBox.Enabled = !isRunning;
        fpsBox.Enabled = !isRunning;
        bitrateBox.Enabled = !isRunning;
        threadsBox.Enabled = !isRunning;
        gpuOutputBox.Enabled = !isRunning;
        priorityCheck.Enabled = !isRunning;
        showFpsCheck.Enabled = !isRunning;
        autoOpenCheck.Enabled = !isRunning;
        touchCheck.Enabled = !isRunning;
        closeTabletCheck.Enabled = !isRunning;
        autoStartCheck.Enabled = !isRunning;
        startInTrayCheck.Enabled = !isRunning;
        closeToTrayCheck.Enabled = !isRunning;
        startButton.Enabled = !isRunning;
        stopButton.Enabled = isRunning;
        UpdateResolutionControls(); // resolution controls also depend on the main-screen check

        if (!isRunning) statsLabel.Text = "";
    }

    // ==================================================================
    // TryStart: everything that happens when streaming starts.
    // 'interactive' is true when YOU pressed Start (we may show a message box),
    // false when the program started streaming by itself at startup.
    // ==================================================================
    void TryStart(bool interactive)
    {
        if (screens == null || monitorBox.SelectedIndex < 0 || monitorBox.SelectedIndex >= screens.Length)
        {
            if (interactive)
            {
                MessageBox.Show("Please pick a monitor first (press Refresh if the list is empty).");
            }
            else
            {
                AppendLog("Could not start by itself: no monitor found. Open the window and press Start.");
            }
            return;
        }

        ReadControlsIntoSettings();
        settings.Save(); // remember these choices for next time

        Screen chosen = screens[monitorBox.SelectedIndex];

        // If you picked a resolution, set it first (only when it differs from the current one)
        ModeInfo wanted = resolutionBox.SelectedItem as ModeInfo;
        if (wanted != null && !chosen.Primary)
        {
            ModeInfo current = DisplayModes.GetCurrent(chosen.DeviceName);
            if (current == null || current.Key != wanted.Key)
            {
                if (!ApplySelectedResolution()) return; // it already wrote the reason in the log
                chosen = screens[monitorBox.SelectedIndex]; // the lists were refreshed
            }
        }

        if (chosen.Primary)
        {
            AppendLog("Note: you picked your MAIN screen, so the tablet will just mirror it.");
        }

        bool started = engine.Start(settings, new MonitorTarget(chosen.DeviceName, chosen.Bounds));
        SetRunning(started);
    }

    // ==================================================================
    // Button handlers
    // ==================================================================
    void OnCheckSetupClicked(object sender, EventArgs e)
    {
        ReadControlsIntoSettings();
        AppSettings snapshot = settings.Copy();
        bool available = false;
        foreach (Screen screen in Screen.AllScreens)
            if (screen.DeviceName == snapshot.MonitorName) available = true;
        checkingSetup = true;
        checkSetupButton.Enabled = false;
        Thread worker = new Thread(delegate ()
        {
            try { engine.CheckSetup(snapshot, available); }
            catch (Exception ex) { RunOnUi(delegate { AppendLog("Setup check failed: " + ex.Message); }); }
            finally { RunOnUi(delegate { checkingSetup = false; checkSetupButton.Enabled = !isRunningNow; }); }
        });
        worker.IsBackground = true;
        worker.Start();
    }

    void OnStartClicked(object sender, EventArgs e)
    {
        TryStart(true);
    }

    async void OnStopClicked(object sender, EventArgs e)
    {
        bool closeTablet = closeTabletCheck.Checked;
        stopButton.Enabled = false;
        statusLabel.Text = "Status: Stopping...";
        try
        {
            await Task.Run(delegate
            {
                if (closeTablet) engine.SayGoodbye();
                engine.Stop();
                if (closeTablet) engine.CloseTabletApp();
            });
        }
        catch (Exception ex) { if (!IsDisposed) AppendLog("Stop failed: " + ex.Message); }
        finally { if (!IsDisposed) SetRunning(false); }
    }

    void OnOpenTabletClicked(object sender, EventArgs e)
    {
        Thread worker = new Thread(delegate () { engine.OpenTabletApp(); });
        worker.IsBackground = true;
        worker.Start();
    }

    void OnCloseTabletClicked(object sender, EventArgs e)
    {
        Thread worker = new Thread(delegate () { engine.CloseTabletApp(); });
        worker.IsBackground = true;
        worker.Start();
    }

    protected override void WndProc(ref Message message)
    {
        // Latch shutdown before WinForms raises closing events. This also works
        // while hidden in the tray and covers both shutdown and user logoff.
        ShutdownGuard.ObserveWindowMessage(message.Msg, message.WParam);
        base.WndProc(ref message);
    }

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        bool windowsIsEnding = e.CloseReason == CloseReason.WindowsShutDown || ShutdownGuard.IsShuttingDown;
        if (windowsIsEnding) engine.PrepareForShutdown();

        // The X button was pressed and "keep running in tray" is ticked: just hide the window
        if (!windowsIsEnding && e.CloseReason == CloseReason.UserClosing && closeToTrayCheck.Checked && !exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Really closing: remember the options and shut everything down
        if (monitorBox.SelectedIndex >= 0 && encoderBox.SelectedIndex >= 0)
        {
            ReadControlsIntoSettings();
            settings.Save();
        }
        // Windows shutting down / logging off: no adb is possible any more, so only say goodbye
        // through the video connection. (Normally this was already done by SessionEnding.)
        if (!windowsIsEnding && closeTabletCheck.Checked)
        {
            engine.SayGoodbye();
        }

        engine.Stop();

        if (!windowsIsEnding && closeTabletCheck.Checked)
        {
            engine.CloseTabletApp(); // do it now: the program is about to exit
        }

        Microsoft.Win32.SystemEvents.SessionEnding -= sessionEndingHandler;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= displayChangedHandler;
        showTimer.Stop();
        showTimer.Dispose();

        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    // ==================================================================
    // Log box helpers
    // ==================================================================
    void AppendLog(string message)
    {
        // Keep the log from growing forever
        if (logBox.TextLength > 30000)
        {
            logBox.Text = logBox.Text.Substring(15000);
        }
        logBox.AppendText(message + Environment.NewLine);
    }

    // Runs 'action' on the window's own thread (the only thread allowed to change controls)
    void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch
        {
            // The window is closing; nothing to update any more.
        }
    }
}
