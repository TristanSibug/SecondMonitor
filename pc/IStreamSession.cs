using System;
using System.Drawing;

readonly struct MonitorTarget
{
    public readonly string DeviceName;
    public readonly Rectangle Bounds;
    public MonitorTarget(string deviceName, Rectangle bounds) { DeviceName = deviceName; Bounds = bounds; }
}

interface IStreamSession
{
    event Action<string> Log;
    event Action<string> Status;
    event Action<string> Stats;
    string DeviceSerial { get; }
    bool Start(AppSettings settings, MonitorTarget screen);
    void Stop();
    void SayGoodbye();
    void PrepareForShutdown();
    void DisplayChanged();
    bool OpenTabletApp();
    void CloseTabletApp();
}
