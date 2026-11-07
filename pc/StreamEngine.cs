using System;

// A fresh session owns every connection, encoder and worker for one Start/Stop run.
// Late callbacks and cleanup from a stopped session cannot affect the next one.
sealed class StreamEngine
{
    readonly object lifecycle = new object();
    readonly Func<string, IStreamSession> createSession;
    volatile IStreamSession session;
    string lastSerial;
    public event Action<string> Log;
    public event Action<string> Status;
    public event Action<string> Stats;

    public StreamEngine(Func<string, IStreamSession> createSession) { this.createSession = createSession; }

    public bool Start(AppSettings settings, MonitorTarget screen)
    {
        lock (lifecycle)
        {
            if (session != null) return false;
            IStreamSession next = createSession(null);
            next.Log += message => { if (ReferenceEquals(session, next)) Log?.Invoke(message); };
            next.Status += message => { if (ReferenceEquals(session, next)) Status?.Invoke(message); };
            next.Stats += message => { if (ReferenceEquals(session, next)) Stats?.Invoke(message); };
            session = next;
            if (next.Start(settings, screen)) return true;
            next.Stop(); session = null;
            return false;
        }
    }
    public void Stop()
    {
        lock (lifecycle)
        {
            IStreamSession old = session;
            if (old == null) return;
            lastSerial = old.DeviceSerial;
            old.Stop();
            session = null;
        }
    }
    public void SayGoodbye() { session?.SayGoodbye(); }
    public void PrepareForShutdown() { (session ?? createSession(null)).PrepareForShutdown(); }
    public void DisplayChanged() { session?.DisplayChanged(); }

    IStreamSession ManualSession()
    {
        IStreamSession manual = createSession(session?.DeviceSerial ?? lastSerial);
        manual.Log += message => Log?.Invoke(message);
        return manual;
    }
    public void OpenTabletApp() { ManualSession().OpenTabletApp(); }
    public void CloseTabletApp() { ManualSession().CloseTabletApp(); }
    public void CheckSetup(AppSettings settings, bool monitorAvailable)
    {
        SetupDiagnostics.Run(settings, monitorAvailable, message => Log?.Invoke(message));
    }
}
