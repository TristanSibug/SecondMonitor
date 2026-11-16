using System;
using System.Threading;
using System.Drawing;

sealed class DesktopMonitor
{
    public string DeviceName;
    public string TargetKey;
    public bool Primary;
    public bool Attached;
    public Rectangle Bounds;
}

interface IMonitorConfiguration
{
    DesktopMonitor[] List();
    bool SetConnected(string deviceName, bool connected, out string message);
}

// Re-read the device after stopping: Windows may have changed the topology while
// the encoder was being drained. Never substitute another display by list index.
sealed class MonitorConnection
{
    readonly IMonitorConfiguration configuration;
    public MonitorConnection(IMonitorConfiguration configuration) { this.configuration = configuration; }

    public bool Toggle(string deviceName, Action stopStreaming, out string message, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested || ShutdownGuard.IsShuttingDown)
        {
            message = "Display changes are blocked while closing or shutting down.";
            return false;
        }
        DesktopMonitor selected = Find(deviceName);
        if (selected == null || selected.Primary)
        {
            message = "Select an available secondary display. The primary display is protected.";
            return false;
        }
        string targetKey = selected.TargetKey;
        bool connect = !selected.Attached;
        if (!connect) stopStreaming(); // Any failure aborts the display change.
        selected = Find(deviceName);
        if (selected == null || selected.TargetKey != targetKey || selected.Primary || selected.Attached == connect)
        {
            message = "The display configuration changed. Refresh and try again.";
            return false;
        }
        bool result = false;
        string detail = "Display changes are blocked during shutdown.";
        // Share the existing shutdown gate so no queued display operation starts
        // after shutdown/logoff has begun.
        if (!ShutdownGuard.TryStart(() => result = configuration.SetConnected(deviceName, connect, out detail),
            cancellation, out _))
        {
            message = "Display changes are blocked during shutdown.";
            return false;
        }
        message = detail;
        return result;
    }

    public static Point ConnectionPosition(DesktopMonitor[] monitors)
    {
        DesktopMonitor rightmost = null;
        foreach (var monitor in monitors)
            if (monitor.Attached && (rightmost == null || monitor.Bounds.Right > rightmost.Bounds.Right))
                rightmost = monitor;
        if (rightmost == null) throw new InvalidOperationException("No active desktop to extend.");
        // Share an edge even when the rightmost display is above/below the primary.
        return new Point(rightmost.Bounds.Right, rightmost.Bounds.Top);
    }

    DesktopMonitor Find(string name)
    {
        foreach (DesktopMonitor monitor in configuration.List())
            if (monitor.DeviceName == name) return monitor;
        return null;
    }
}
