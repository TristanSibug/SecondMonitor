// ============================================================================
// Program.cs  --  the very start of the program.
//
// All it does is open the settings window (MainForm). The real work happens
// in StreamEngine.cs (video + touch) and MainForm.cs (the window).
// ============================================================================

using System;
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

        // Open the window and keep the program alive until the window is closed
        Application.Run(new MainForm());
    }
}
