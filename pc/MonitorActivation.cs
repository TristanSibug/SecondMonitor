using System;
using System.Threading;
using System.Threading.Tasks;

static class MonitorActivation
{
    public static DesktopMonitor StartupTarget(DesktopMonitor[] monitors, string identity, string name)
    {
        if (!string.IsNullOrEmpty(identity))
            return Array.Find(monitors, m => string.Equals(m.Identity, identity, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(name)) return Array.Find(monitors, m => m.DeviceName == name);
        return Array.Find(monitors, m => !m.Primary);
    }

    // Windows can announce a topology change before WinForms sees the new screen.
    // Poll readiness without blocking the UI; cancellation/timeout never starts a stream.
    public static async Task<bool> StartWhenReadyAsync(Func<bool> ready, Action start,
        CancellationToken cancellation, int timeoutMs = 3000)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested && !ShutdownGuard.IsShuttingDown)
        {
            if (ready())
            {
                if (cancellation.IsCancellationRequested || ShutdownGuard.IsShuttingDown) return false;
                start();
                return true;
            }
            if (timer.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(50, cancellation);
        }
        return false;
    }
}
