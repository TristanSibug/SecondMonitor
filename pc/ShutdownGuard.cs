using System;
using System.Runtime.InteropServices;
using System.Threading;

// Serialize the decision to launch with the shutdown notification. A caller
// that checked earlier must still pass this gate at the actual Process.Start.
sealed class ProcessLaunchGate
{
    readonly object sync = new object();
    CancellationTokenSource shutdown = new CancellationTokenSource();
    bool ending;

    public bool IsShuttingDown { get { lock (sync) return ending; } }

    public bool TryStart(Action start, CancellationToken cancellation, out CancellationToken shutdownToken)
    {
        lock (sync)
        {
            shutdownToken = shutdown.Token;
            if (ending || cancellation.IsCancellationRequested) return false;
            start();
            return true;
        }
    }

    public void BeginShutdown()
    {
        CancellationTokenSource pending;
        lock (sync)
        {
            if (ending) return;
            ending = true;
            pending = shutdown;
        }
        // Cancel outside the launch lock; cancellation callbacks may take locks.
        pending.Cancel();
    }

    public void CancelShutdown()
    {
        lock (sync)
        {
            if (!ending) return;
            // Old commands retain the cancelled token, even if shutdown is aborted.
            // Do not dispose it while those commands may still be using it.
            shutdown = new CancellationTokenSource();
            ending = false;
        }
    }
}

static class ShutdownGuard
{
    const int WM_QUERYENDSESSION = 0x0011;
    const int WM_ENDSESSION = 0x0016;
    const int SM_SHUTTINGDOWN = 0x2000;
    static readonly ProcessLaunchGate launches = new ProcessLaunchGate();

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    static void ObserveSystemShutdown()
    {
        // Also protect the interval before our window receives its notification.
        if (OperatingSystem.IsWindows() && GetSystemMetrics(SM_SHUTTINGDOWN) != 0)
            BeginShutdown();
    }

    public static bool IsShuttingDown
    {
        get { ObserveSystemShutdown(); return launches.IsShuttingDown; }
    }

    public static bool TryStart(Action start, CancellationToken cancellation, out CancellationToken shutdownToken)
    {
        ObserveSystemShutdown();
        return launches.TryStart(start, cancellation, out shutdownToken);
    }

    public static void BeginShutdown() { launches.BeginShutdown(); }

    public static void ObserveWindowMessage(int message, IntPtr wParam)
    {
        if (message == WM_QUERYENDSESSION || (message == WM_ENDSESSION && wParam != IntPtr.Zero))
            BeginShutdown();
        else if (message == WM_ENDSESSION)
            launches.CancelShutdown(); // Windows cancelled shutdown/logoff.
    }
}
