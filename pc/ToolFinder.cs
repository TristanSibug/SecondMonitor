// ============================================================================
// ToolFinder
//
// Finds the helper programs we need (ffmpeg.exe and adb.exe).
//
// Why this exists: when ffmpeg/adb are installed by the installer (using winget), Windows
// may not have updated the PATH of programs that were already running, so just saying
// "ffmpeg" can fail even though it IS installed. So we look in all the usual places ourselves:
//   1. the folder of SecondMonitor.exe
//   2. every folder in PATH
//   3. the folders where winget puts its shortcuts
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

static class ToolFinder
{
    // Remember what we found, so we do not search the disk again and again
    static readonly Dictionary<string, string> found = new Dictionary<string, string>();

    // Returns the full path of the program, e.g. "C:\...\ffmpeg.exe".
    // If it cannot be found, the plain name is returned so Windows' normal search still gets a try
    // (and the usual "could not start ffmpeg" message appears if it is really missing).
    public static string Find(string name)
    {
        lock (found)
        {
            string path;
            if (found.TryGetValue(name, out path) && File.Exists(path)) return path;

            string fileName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

            // The list of folders to look in, in order
            List<string> folders = new List<string>();
            folders.Add(AppContext.BaseDirectory);

            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (pathVariable != null)
            {
                foreach (string folder in pathVariable.Split(';'))
                {
                    if (folder.Trim().Length > 0) folders.Add(folder.Trim().Trim('"'));
                }
            }

            // Where winget puts shortcuts to the programs it installs
            folders.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links"));
            folders.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WinGet", "Links"));

            foreach (string folder in folders)
            {
                try
                {
                    string candidate = Path.Combine(folder, fileName);
                    if (File.Exists(candidate))
                    {
                        found[name] = candidate;
                        return candidate;
                    }
                }
                catch
                {
                    // A strange folder name in PATH. Skip it.
                }
            }

            return name; // not found
        }
    }
}
