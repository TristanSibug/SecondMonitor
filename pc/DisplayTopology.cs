using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

// Windows CCD (Connecting and Configuring Displays) uses target identities and
// video timings even when a detached GDI display no longer enumerates DEVMODEs.
static class DisplayTopology
{
    internal const uint InvalidMode = 0xffffffff;
    [StructLayout(LayoutKind.Sequential)] internal struct Adapter { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source
    { public Adapter Adapter; public uint Id, ModeIndex, StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Target
    {
        public Adapter Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int Available;
        public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath
    { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Signal
    {
        public ulong PixelRate;
        public Rational HSync, VSync;
        public uint ActiveWidth, ActiveHeight, TotalWidth, TotalHeight, VideoStandard, ScanLineOrdering;
    }
    [StructLayout(LayoutKind.Explicit, Size = 48)] internal struct ModeData
    {
        [FieldOffset(0)] public Signal Signal;
        [FieldOffset(0)] public uint Width;
        [FieldOffset(4)] public uint Height;
        [FieldOffset(8)] public uint PixelFormat;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Mode
    { public uint Type, Id; public Adapter Adapter; public ModeData Data; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    { public uint Type, Size; public Adapter Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] struct PreferredMode
    { public Header Header; public uint Width, Height; public Signal Signal; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayPath[] paths, ref uint modeCount, [Out] Mode[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref PreferredMode mode);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint pathCount, DisplayPath[] paths,
        uint modeCount, Mode[] modes, uint flags);

    internal sealed class Snapshot
    {
        public DisplayPath[] Paths;
        public Mode[] Modes;
    }
    internal interface IApi
    {
        Snapshot Query(bool activeOnly);
        string SourceName(Source source);
        Mode PreferredSource(DisplayPath path);
        int Set(Snapshot snapshot, bool validate);
    }
    sealed class WindowsApi : IApi
    {
        public Snapshot Query(bool activeOnly)
        {
            uint flags = activeOnly ? 2u : 1u; // QDC_ONLY_ACTIVE_PATHS / QDC_ALL_PATHS
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int error = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (error != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed (code " + error + ").");
                var paths = new DisplayPath[pathCount];
                var modes = new Mode[modeCount];
                error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == 122) continue; // Topology grew between the two calls.
                if (error != 0) throw new InvalidOperationException("QueryDisplayConfig failed (code " + error + ").");
                return new Snapshot { Paths = paths.Take((int)pathCount).ToArray(), Modes = modes.Take((int)modeCount).ToArray() };
            }
            throw new InvalidOperationException("The display configuration keeps changing. Refresh and try again.");
        }
        public string SourceName(Source source)
        {
            var info = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = source.Adapter, Id = source.Id } };
            return DisplayConfigGetDeviceInfo(ref info) == 0 ? info.Name : null;
        }
        public Mode PreferredSource(DisplayPath path)
        {
            var info = new PreferredMode { Header = new Header { Type = 3, Size = (uint)Marshal.SizeOf<PreferredMode>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
            int error = DisplayConfigGetDeviceInfo(ref info);
            if (error != 0 || info.Width == 0 || info.Height == 0)
                throw new InvalidOperationException("Windows could not read the display's preferred mode (code " + error + ").");
            return new Mode { Type = 1, Id = path.Source.Id, Adapter = path.Source.Adapter,
                Data = new ModeData { Width = info.Width, Height = info.Height, PixelFormat = 4 } }; // 32 bpp
        }
        public int Set(Snapshot snapshot, bool validate)
        {
            // No SDC_ALLOW_CHANGES: Windows must honor supplied modes for the
            // other displays. Never use a global EXTEND/CLONE topology preset.
            uint flags = 0x20u | (validate ? 0x40u : 0x80u | 0x200u);
            return SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths, (uint)snapshot.Modes.Length, snapshot.Modes, flags);
        }
    }

    internal static string TargetKey(Target target) => target.Adapter.High + ":" + target.Adapter.Low + ":" + target.Id;

    static bool Same(Adapter a, Adapter b) => a.Low == b.Low && a.High == b.High;
    static bool SameSource(Source a, Source b) => Same(a.Adapter, b.Adapter) && a.Id == b.Id;
    static bool SameTarget(Target a, Target b) => Same(a.Adapter, b.Adapter) && a.Id == b.Id;

    public static DesktopMonitor[] ListMonitors(string preferredName, IDictionary<string, string> targetSources)
    {
        return ListMonitors(new WindowsApi(), DisplayModes.ListMonitors(), preferredName, targetSources);
    }

    internal static DesktopMonitor[] ListMonitors(IApi api, DesktopMonitor[] gdi, string preferredName, IDictionary<string, string> targetSources = null)
    {
        var current = api.Query(true);
        var result = new List<DesktopMonitor>();
        var usedSources = new HashSet<string>();
        foreach (var monitor in gdi.Where(m => m.Attached))
        {
            var activePaths = current.Paths.Where(p => api.SourceName(p.Source) == monitor.DeviceName).ToArray();
            if (activePaths.Length == 1)
            {
                monitor.TargetKey = TargetKey(activePaths[0].Target);
                if (targetSources != null) targetSources[monitor.TargetKey] = monitor.DeviceName;
            }
            result.Add(monitor);
            usedSources.Add(monitor.DeviceName);
        }
        // QDC_ALL_PATHS contains possible routes, not one row per monitor.
        // Group by physical target and exclude targets already on the desktop.
        var groups = api.Query(false).Paths.Where(p => p.Target.Available != 0 &&
            !current.Paths.Any(active => SameTarget(active.Target, p.Target)))
            .GroupBy(p => TargetKey(p.Target)).OrderBy(g => g.Key).ToArray();
        if (targetSources != null)
        {
            var liveTargets = new HashSet<string>(groups.Select(g => g.Key).Concat(current.Paths.Select(p => TargetKey(p.Target))));
            foreach (var stale in targetSources.Keys.Where(key => !liveTargets.Contains(key)).ToArray())
                targetSources.Remove(stale);
        }
        foreach (var group in groups)
        {
            string remembered = null;
            targetSources?.TryGetValue(group.Key, out remembered);
            var candidates = group.Select(p => (Path: p, Name: api.SourceName(p.Source)))
                .Where(p => !string.IsNullOrEmpty(p.Name) && !usedSources.Contains(p.Name) &&
                    (targetSources == null || !targetSources.Any(saved => saved.Key != group.Key && saved.Value == p.Name)))
                .OrderByDescending(p => p.Name == remembered).ThenByDescending(p => p.Name == preferredName)
                .ThenBy(p => p.Path.Source.Id).ToArray();
            if (candidates.Length == 0) continue;
            var candidate = candidates[0];
            if (targetSources != null) targetSources[group.Key] = candidate.Name;
            var bounds = System.Drawing.Rectangle.Empty;
            try
            {
                var mode = api.PreferredSource(candidate.Path);
                bounds = new System.Drawing.Rectangle(0, 0, (int)mode.Data.Width, (int)mode.Data.Height);
            }
            catch (InvalidOperationException) { } // Keep the target visible so failure can be diagnosed on Connect.
            result.Add(new DesktopMonitor { DeviceName = candidate.Name, TargetKey = group.Key, Bounds = bounds });
            usedSources.Add(candidate.Name);
        }
        return result.ToArray();
    }

    internal static Snapshot Plan(IApi api, Snapshot current, string name, bool connect, DesktopMonitor[] monitors)
    {
        var paths = new List<DisplayPath>(current.Paths);
        var modes = new List<Mode>(current.Modes);
        var selected = monitors.SingleOrDefault(m => m.DeviceName == name);
        if (selected == null || selected.Primary || selected.Attached == connect)
            throw new InvalidOperationException("Select a secondary display in the expected connection state.");
        if (!connect)
        {
            var matches = paths.Where(p => api.SourceName(p.Source) == name).ToArray();
            // Refuse clone groups: changing their shared source could affect another display.
            if (matches.Length != 1) throw new InvalidOperationException("Cannot disconnect a missing or shared display source.");
            paths.Remove(matches[0]);
        }
        else
        {
            var candidates = api.Query(false).Paths.Where(p => p.Target.Available != 0 &&
                (selected.TargetKey == null || TargetKey(p.Target) == selected.TargetKey) &&
                api.SourceName(p.Source) == name &&
                !paths.Any(active => SameSource(active.Source, p.Source) || SameTarget(active.Target, p.Target))).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException("Windows did not expose one available target for this display. Check Windows Display settings and refresh.");
            var path = candidates[0];
            Mode source = api.PreferredSource(path);
            var position = MonitorConnection.ConnectionPosition(monitors);
            source.Data.X = position.X;
            source.Data.Y = position.Y;
            int sourceIndex = modes.FindIndex(m => m.Type == 1 && m.Id == source.Id && Same(m.Adapter, source.Adapter));
            if (sourceIndex < 0) { sourceIndex = modes.Count; modes.Add(source); }
            else modes[sourceIndex] = source; // Reuse an unreferenced entry, never duplicate an identity.
            path.Source.ModeIndex = (uint)sourceIndex;
            path.Source.StatusFlags = 0;
            // Let Windows choose target timing for the selected display only.
            // Existing paths retain their exact source and target mode indices.
            path.Target.ModeIndex = InvalidMode;
            path.Target.RefreshRate = default;
            path.Target.ScanLineOrdering = 0;
            path.Target.StatusFlags = 0;
            path.Flags = 1; // DISPLAYCONFIG_PATH_ACTIVE
            paths.Add(path);
        }
        return new Snapshot { Paths = paths.ToArray(), Modes = modes.ToArray() };
    }

    public static bool SetConnected(string name, bool connect, Func<DesktopMonitor[]> list, out string message)
    {
        try { return Apply(new WindowsApi(), list, name, connect, out message); }
        catch (Exception ex) { message = ex.Message; return false; }
    }

    internal static bool Apply(IApi api, Func<DesktopMonitor[]> list, string name, bool connect, out string message)
    {
        var current = api.Query(true);
        var before = list();
        var selected = before.SingleOrDefault(m => m.DeviceName == name);
        var planned = Plan(api, current, name, connect, before);
        int error = api.Set(planned, true);
        if (error == 0)
        {
            // Never apply a plan based on a desktop that changed during validation.
            var fresh = api.Query(true);
            if (!current.Paths.SequenceEqual(fresh.Paths) || !current.Modes.SequenceEqual(fresh.Modes) ||
                !list().Any(m => m.DeviceName == name && m.TargetKey == selected.TargetKey && !m.Primary && m.Attached != connect))
            {
                message = "The display configuration changed. Refresh and try again.";
                return false;
            }
            error = api.Set(planned, false);
        }
        message = error == 0 ? (connect ? "Monitor connected." : "Monitor disconnected.") :
            "Windows DisplayConfig refused to " + (connect ? "connect" : "disconnect") + " this display (code " + error + ").";
        return error == 0;
    }
}
