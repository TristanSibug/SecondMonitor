// ============================================================================
// DisplayModes
//
// Helper code to ask Windows two things about a monitor:
//   1. Which resolutions / refresh rates can it be set to?   (List)
//   2. Please change it to this one.                         (Apply)
//
// These are the same modes you see in Windows' Display settings. If a mode
// is not in this list, the monitor's driver does not offer it, and this
// program cannot create it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices; // lets us call Windows functions (DllImport)

// ----------------------------------------------------------------------
// ModeInfo: one choice in the drop-down, like "1280x800 @ 60 Hz"
// ----------------------------------------------------------------------
class ModeInfo
{
    public int Width;
    public int Height;
    public int Hz;

    // A short text used to save the choice in settings.txt and to compare two modes
    public string Key
    {
        get { return Width + "x" + Height + "@" + Hz; }
    }

    // The text the drop-down shows. (A ComboBox calls ToString() on every item.)
    public override string ToString()
    {
        return Width + "x" + Height + " @ " + Hz + " Hz";
    }
}

sealed class WindowsMonitorConfiguration : IMonitorConfiguration
{
    public DesktopMonitor[] List() { return DisplayModes.ListMonitors(); }
    public bool SetConnected(string name, bool connected, out string message)
    { return DisplayModes.SetConnected(name, connected, out message); }
}

static class DisplayModes
{
    // ------------------------------------------------------------------
    // DEVMODE: the Windows structure that describes one display mode.
    // The order and types of these fields must match Windows' own layout EXACTLY,
    // so do not rearrange them.
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    // Windows functions from user32.dll
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE display, uint flags);

    static readonly Dictionary<string, DEVMODE> disconnectedModes = new Dictionary<string, DEVMODE>();

    public static DesktopMonitor[] ListMonitors()
    {
        var monitors = new List<DesktopMonitor>();
        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref device, 0)) break;
            if ((device.StateFlags & 8) != 0) continue; // Mirroring pseudo-driver.
            bool attached = (device.StateFlags & 1) != 0;
            DEVMODE mode = NewDevMode();
            if (!EnumDisplaySettings(device.DeviceName, attached ? -1 : -2, ref mode))
                EnumDisplaySettings(device.DeviceName, 0, ref mode);
            monitors.Add(new DesktopMonitor {
                DeviceName = device.DeviceName, Attached = attached,
                Primary = (device.StateFlags & 4) != 0,
                Bounds = new System.Drawing.Rectangle(mode.dmPositionX, mode.dmPositionY,
                    (int)mode.dmPelsWidth, (int)mode.dmPelsHeight)
            });
        }
        return monitors.ToArray();
    }

    public static bool SetConnected(string name, bool connect, out string message)
    {
        DesktopMonitor selected = null;
        DesktopMonitor[] monitors = ListMonitors();
        foreach (var monitor in monitors) if (monitor.DeviceName == name) selected = monitor;
        if (selected == null || selected.Primary || selected.Attached == connect)
        {
            message = "The selected secondary display is no longer available in the expected state.";
            return false;
        }
        DEVMODE mode = NewDevMode();
        if (connect)
        {
            if (!disconnectedModes.TryGetValue(name, out mode))
            {
                mode = NewDevMode();
                if (!EnumDisplaySettings(name, -2, ref mode) || mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
                {
                    mode = NewDevMode();
                    if (!EnumDisplaySettings(name, 0, ref mode))
                    {
                        message = "Windows does not offer a mode for this disconnected display.";
                        return false;
                    }
                }
            }
            // Extend beside the existing desktop; never move or reset its displays.
            var position = MonitorConnection.ConnectionPosition(monitors);
            mode.dmPositionX = position.X;
            mode.dmPositionY = position.Y;
            mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        }
        else
        {
            if (!EnumDisplaySettings(name, ENUM_CURRENT_SETTINGS, ref mode))
            {
                message = "Could not read the selected display's current mode.";
                return false;
            }
            disconnectedModes[name] = mode;
            mode.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            mode.dmPelsWidth = mode.dmPelsHeight = 0;
        }
        int result = ChangeDisplaySettingsEx(name, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (result == 0)
        {
            // A mode test may take time; do not change a display promoted to
            // primary (or removed) while Windows was checking the request.
            bool stillSecondary = false;
            foreach (var monitor in ListMonitors())
                if (monitor.DeviceName == name && !monitor.Primary && monitor.Attached != connect)
                    stillSecondary = true;
            if (!stillSecondary)
            {
                message = "The display configuration changed. Refresh and try again.";
                return false;
            }
            result = ChangeDisplaySettingsEx(name, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
        message = result == 0 ? (connect ? "Monitor connected." : "Monitor disconnected.") :
            "Windows refused to " + (connect ? "connect" : "disconnect") + " this display (code " + result + ").";
        return result == 0;
    }

    // Numbers Windows understands
    const int ENUM_CURRENT_SETTINGS = -1;     // "tell me the mode it is using right now"
    const uint DM_POSITION = 0x00000020;      // the position fields are filled in
    const uint DM_BITSPERPEL = 0x00040000;    // colour depth
    const uint DM_PELSWIDTH = 0x00080000;     // the width field is filled in
    const uint DM_PELSHEIGHT = 0x00100000;    // the height field is filled in
    const uint DM_DISPLAYFREQUENCY = 0x00400000; // the refresh rate field is filled in
    const uint CDS_UPDATEREGISTRY = 0x00000001;  // change it and remember it
    const uint CDS_TEST = 0x00000002;            // only CHECK if it would work, change nothing

    // Makes an empty DEVMODE with its size filled in (Windows requires that)
    static DEVMODE NewDevMode()
    {
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }

    // ------------------------------------------------------------------
    // List: every mode this monitor offers, up to 60 Hz (our video never
    // goes above 60 frames per second, so higher rates are not useful).
    // ------------------------------------------------------------------
    public static List<ModeInfo> List(string deviceName)
    {
        List<ModeInfo> modes = new List<ModeInfo>();
        HashSet<string> seen = new HashSet<string>(); // to skip duplicates

        int i = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettings(deviceName, i, ref dm))
        {
            i++;

            // Only normal 32-bit colour modes, and only 24..60 Hz
            if (dm.dmBitsPerPel != 32) continue;
            if (dm.dmDisplayFrequency < 24 || dm.dmDisplayFrequency > 60) continue;

            ModeInfo mode = new ModeInfo();
            mode.Width = (int)dm.dmPelsWidth;
            mode.Height = (int)dm.dmPelsHeight;
            mode.Hz = (int)dm.dmDisplayFrequency;

            if (seen.Add(mode.Key)) modes.Add(mode);

            dm = NewDevMode(); // fresh one for the next question
        }

        // Sort: smallest width first, then height, then refresh rate
        modes.Sort(delegate (ModeInfo a, ModeInfo b)
        {
            if (a.Width != b.Width) return a.Width.CompareTo(b.Width);
            if (a.Height != b.Height) return a.Height.CompareTo(b.Height);
            return a.Hz.CompareTo(b.Hz);
        });

        return modes;
    }

    // ------------------------------------------------------------------
    // GetCurrent: the mode the monitor is using right now (or null if unknown)
    // ------------------------------------------------------------------
    public static ModeInfo GetCurrent(string deviceName)
    {
        DEVMODE dm = NewDevMode();
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return null;

        ModeInfo mode = new ModeInfo();
        mode.Width = (int)dm.dmPelsWidth;
        mode.Height = (int)dm.dmPelsHeight;
        mode.Hz = (int)dm.dmDisplayFrequency;
        return mode;
    }

    // ------------------------------------------------------------------
    // Apply: asks Windows to switch the monitor to 'mode'.
    // Returns true if it worked. 'message' says what happened (for the log).
    // ------------------------------------------------------------------
    public static bool Apply(string deviceName, ModeInfo mode, out string message)
    {
        // Start from the monitor's current settings so everything else stays the same
        // (especially its position on your desktop).
        DEVMODE dm = NewDevMode();
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
        {
            message = "Could not read the monitor's current settings.";
            return false;
        }

        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.Hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;

        // First only CHECK that Windows accepts it (changes nothing)
        int test = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != 0)
        {
            message = "Windows refused " + mode + " for this monitor (code " + test + ").";
            return false;
        }

        // Now really change it (and remember it)
        int result = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != 0)
        {
            message = "Could not change the monitor to " + mode + " (code " + result + ").";
            return false;
        }

        message = "Monitor set to " + mode + ".";
        return true;
    }
}
