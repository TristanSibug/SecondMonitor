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

    // Numbers Windows understands
    const int ENUM_CURRENT_SETTINGS = -1;     // "tell me the mode it is using right now"
    const uint DM_POSITION = 0x00000020;      // the position fields are filled in
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
