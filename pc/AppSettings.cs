// ============================================================================
// AppSettings
//
// This class just HOLDS every option you can choose in the window, and it
// remembers them between runs by saving them into a small text file called
// "settings.txt" (it is saved next to the program).
//
// The file looks like this, one option per line:
//     Encoder=libx264
//     Fps=60
// ============================================================================

using System;
using System.IO;

class AppSettings
{
    // ------------------------------------------------------------------
    // The options, with the values they start with the very first time.
    // ------------------------------------------------------------------
    public int MonitorIndex = 1;          // which monitor in the list is the virtual one
    public string Encoder = "libx264";    // libx264 (CPU), h264_amf (AMD), h264_qsv (Intel), h264_nvenc (NVIDIA)
    public bool GpuCapture = false;       // false = standard capture, true = GPU capture (ffmpeg ddagrab)
    public int Fps = 60;                  // pictures per second
    public int BitrateKbps = 8000;        // video quality budget (8000 = 8 Mbit/s)
    public bool LowerPriority = true;     // run below the game's priority so the game wins
    public int EncoderThreads = 2;        // CPU threads libx264 may use (0 = let it decide)
    public bool ShowFps = false;          // show the FPS number in the window
    public int GpuOutputIndex = -1;       // which screen ffmpeg's GPU capture uses (-1 = find it automatically)
    public string Resolution = "";        // monitor resolution to set on Start, like "1280x800@60" ("" = leave as is)
    public string MonitorName = "";       // Windows' own name for the chosen monitor, like \\.\DISPLAY8 (more reliable than its list number)
    public bool AutoStartStreaming = false; // start streaming as soon as the program opens
    public bool StartInTray = false;      // when the program opens, hide the window in the system tray
    public bool CloseToTray = false;      // closing the window keeps the program running in the tray
    public bool AutoOpenTabletApp = true; // open the app on the tablet by itself when the tablet is plugged in
    public bool TouchScreenInput = true;  // true = real touchscreen input (swipe to scroll, pinch...), false = mouse only
    public bool CloseTabletOnStop = false; // close the tablet app when you press Stop or exit the program

    // ------------------------------------------------------------------
    // Where the settings file lives
    // ------------------------------------------------------------------
    static string FilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "settings.txt");
    }

    // ------------------------------------------------------------------
    // Load: read settings.txt (if it exists) and return the settings in it.
    // If anything is missing or broken, we simply keep the starting values.
    // ------------------------------------------------------------------
    public static AppSettings Load()
    {
        AppSettings s = new AppSettings();

        try
        {
            if (!File.Exists(FilePath())) return s;

            foreach (string line in File.ReadAllLines(FilePath()))
            {
                // Each line is "name=value". Find the "=" sign and cut the line in two.
                int equalsAt = line.IndexOf('=');
                if (equalsAt <= 0) continue; // not a valid line, skip it

                string name = line.Substring(0, equalsAt).Trim();
                string value = line.Substring(equalsAt + 1).Trim();

                int number;
                bool flag;

                if (name == "MonitorIndex" && int.TryParse(value, out number)) s.MonitorIndex = number;
                else if (name == "Encoder" && value.Length > 0) s.Encoder = value;
                else if (name == "GpuCapture" && bool.TryParse(value, out flag)) s.GpuCapture = flag;
                else if (name == "Fps" && int.TryParse(value, out number)) s.Fps = number;
                else if (name == "BitrateKbps" && int.TryParse(value, out number)) s.BitrateKbps = number;
                else if (name == "LowerPriority" && bool.TryParse(value, out flag)) s.LowerPriority = flag;
                else if (name == "EncoderThreads" && int.TryParse(value, out number)) s.EncoderThreads = number;
                else if (name == "ShowFps" && bool.TryParse(value, out flag)) s.ShowFps = flag;
                else if (name == "GpuOutputIndex" && int.TryParse(value, out number)) s.GpuOutputIndex = number;
                else if (name == "Resolution") s.Resolution = value;
                else if (name == "MonitorName") s.MonitorName = value;
                else if (name == "AutoStartStreaming" && bool.TryParse(value, out flag)) s.AutoStartStreaming = flag;
                else if (name == "StartInTray" && bool.TryParse(value, out flag)) s.StartInTray = flag;
                else if (name == "CloseToTray" && bool.TryParse(value, out flag)) s.CloseToTray = flag;
                else if (name == "AutoOpenTabletApp" && bool.TryParse(value, out flag)) s.AutoOpenTabletApp = flag;
                else if (name == "TouchScreenInput" && bool.TryParse(value, out flag)) s.TouchScreenInput = flag;
                else if (name == "CloseTabletOnStop" && bool.TryParse(value, out flag)) s.CloseTabletOnStop = flag;
            }
        }
        catch
        {
            // The file could not be read. That is fine, we just use the starting values.
        }

        return s;
    }

    // ------------------------------------------------------------------
    // Save: write every option into settings.txt
    // ------------------------------------------------------------------
    public void Save()
    {
        try
        {
            string[] lines = new string[]
            {
                "MonitorIndex=" + MonitorIndex,
                "Encoder=" + Encoder,
                "GpuCapture=" + GpuCapture,
                "Fps=" + Fps,
                "BitrateKbps=" + BitrateKbps,
                "LowerPriority=" + LowerPriority,
                "EncoderThreads=" + EncoderThreads,
                "ShowFps=" + ShowFps,
                "GpuOutputIndex=" + GpuOutputIndex,
                "Resolution=" + Resolution,
                "MonitorName=" + MonitorName,
                "AutoStartStreaming=" + AutoStartStreaming,
                "StartInTray=" + StartInTray,
                "CloseToTray=" + CloseToTray,
                "AutoOpenTabletApp=" + AutoOpenTabletApp,
                "TouchScreenInput=" + TouchScreenInput,
                "CloseTabletOnStop=" + CloseTabletOnStop
            };
            File.WriteAllLines(FilePath(), lines);
        }
        catch
        {
            // Could not save (for example a read-only folder). Not a big problem.
        }
    }
}
