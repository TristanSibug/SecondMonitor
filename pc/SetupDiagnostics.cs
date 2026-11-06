using System;
using System.Text.RegularExpressions;

static class SetupDiagnostics
{
    public static void Run(AppSettings settings, bool monitorAvailable, Action<string> log,
        Func<string, string, int, CommandResult> execute = null)
    {
        execute ??= (file, arguments, timeout) => CommandRunner.Run(ToolFinder.Find(file), arguments, timeout);
        log("[setup] Checking prerequisites...");
        log(monitorAvailable ? "[setup] Selected monitor is available." : "[setup] Select an available monitor; press Refresh if needed.");
        CommandResult encoders = execute("ffmpeg", "-hide_banner -encoders", 8000);
        if (!encoders.Success) log("[setup] Cannot run ffmpeg. Install it or put ffmpeg.exe beside SecondMonitor.exe. " + encoders.Output.Trim());
        else
        {
            foreach (string encoder in new[] { "libx264", "h264_amf", "h264_qsv", "h264_nvenc" })
            {
                bool available = Regex.IsMatch(encoders.Output, @"(?m)^\s*V\S*\s+" + Regex.Escape(encoder) + @"\s");
                log("[setup] " + encoder + ": " + (available ? "included in ffmpeg (hardware support still required)." : "not included in this ffmpeg build."));
            }
            CommandResult sample = execute("ffmpeg",
                "-hide_banner -loglevel error -f lavfi -i color=size=128x96:rate=30 -frames:v 1 -an -c:v "
                + settings.Encoder + " -bf 0 -pix_fmt " + (settings.Encoder == "h264_amf" ? "nv12" : "yuv420p") + " -f null -", 8000);
            log(sample.Success ? "[setup] Selected encoder successfully encoded a sample frame."
                : "[setup] Selected encoder sample failed" + (sample.TimedOut ? " (timed out)" : "") + ": " + sample.Output.Trim() + " Try CPU/libx264.");
            if (settings.GpuCapture)
            {
                CommandResult filters = execute("ffmpeg", "-hide_banner -filters", 8000);
                log(filters.Success && Regex.IsMatch(filters.Output, @"\bddagrab\b")
                    ? "[setup] GPU capture filter is included; the selected display must also support it."
                    : "[setup] GPU capture filter is unavailable. Use Standard capture or update ffmpeg.");
            }
        }
        CommandResult devices = execute("adb", "devices", 8000);
        if (!devices.Success)
        {
            log("[setup] Cannot query adb devices" + (devices.TimedOut ? " (timed out)" : "") + ". Install Android platform-tools and check USB. " + devices.Output.Trim());
        }
        else
        {
            Match ready = Regex.Match(devices.Output, @"(?m)^([^\s]+)\s+device\s*$");
            if (ready.Success)
            {
                string serial = ready.Groups[1].Value;
                log("[setup] Authorized tablet: " + serial);
                CommandResult app = execute("adb", "-s \"" + serial.Replace("\"", "") + "\" shell pm path com.example.secondmonitor", 8000);
                log(app.Success && app.Output.Contains("package:")
                    ? "[setup] Tablet app is installed."
                    : "[setup] Could not verify the tablet app. Install SecondMonitor.apk on the tablet. " + app.Output.Trim());
            }
            else if (Regex.IsMatch(devices.Output, @"\sunauthorized\b")) log("[setup] Tablet connected: unlock it and tap Allow for USB debugging.");
            else if (Regex.IsMatch(devices.Output, @"\soffline\b")) log("[setup] Tablet is offline. Unlock it and reconnect the USB cable.");
            else log("[setup] No authorized tablet found. Connect a data-capable USB cable and enable USB debugging.");
        }
        log("[setup] Check complete. Driver operation, streaming and touch still require a connected tablet.");
    }
}
