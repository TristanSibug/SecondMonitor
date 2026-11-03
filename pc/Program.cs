// ============================================================================
// Program.cs  --  the very start of the program.
//
// All it does is open the settings window (MainForm). The real work happens
// in StreamEngine.cs (video + touch) and MainForm.cs (the window).
// ============================================================================

using System;
using System.Threading;               // Mutex (one copy only), EventWaitHandle, Thread
using System.Runtime.InteropServices; // lets us call Windows functions (DllImport)
using System.Windows.Forms;           // Application

static class Program
{
    // Tells Windows "don't lie to me about pixel sizes on high-DPI screens".
    // Without this, our screen positions and mouse positions can be wrong/scaled.
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // [STAThread] is needed by Windows Forms windows. Just leave it here.
    [STAThread]
    static void Main()
    {
        // Must be done before any window is created
        SetProcessDPIAware();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- Only allow ONE copy of the program ----
        // A "mutex" is a named flag that Windows keeps for us. The first copy creates it.
        // A second copy sees that it already exists (createdNew = false) and knows
        // another copy is running.
        bool createdNew;
        using (Mutex oneCopyFlag = new Mutex(false, "SecondMonitor_SingleInstance", out createdNew))
        {
            // This is a named "doorbell". A second copy rings it to say "please show your window".
            EventWaitHandle showBell = new EventWaitHandle(
                false, EventResetMode.AutoReset, "SecondMonitor_ShowWindow");

            if (!createdNew)
            {
                // Another copy is already running (maybe hidden in the tray).
                // Ring its doorbell so its window appears, then close this copy.
                showBell.Set();
                return;
            }

            // We are the first copy: open the window.
            MainForm form = new MainForm();

            // A helper thread waits for the doorbell. When a second copy rings it,
            // we ask the window to show itself.
            Thread bellThread = new Thread(delegate ()
            {
                while (true)
                {
                    showBell.WaitOne();
                    form.RequestShow();
                }
            });
            bellThread.IsBackground = true; // ends by itself when the program ends
            bellThread.Start();

            // Keep the program alive until the window is closed
            Application.Run(form);
        }
    }
}
