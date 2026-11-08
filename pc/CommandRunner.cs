using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

sealed class CommandResult
{
    public bool Started;
    public bool TimedOut;
    public bool Cancelled;
    public int ExitCode = -1;
    public string Output = "";
    public bool Success { get { return Started && !TimedOut && !Cancelled && ExitCode == 0; } }
}

static class CommandRunner
{
    public static CommandResult Run(string file, string arguments, int timeoutMs, CancellationToken cancellation = default)
    {
        CommandResult result = new CommandResult();
        if (cancellation.IsCancellationRequested || ShutdownGuard.IsShuttingDown)
        { result.Cancelled = true; return result; }
        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            CancellationToken shutdown;
            try
            {
                if (!ShutdownGuard.TryStart(() => process.Start(), cancellation, out shutdown))
                { result.Cancelled = true; return result; }
                result.Started = true;
            }
            catch (Win32Exception ex) { result.Output = ex.Message; return result; }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Stopwatch timer = Stopwatch.StartNew();
            while (!process.WaitForExit(50))
            {
                result.Cancelled = cancellation.IsCancellationRequested || shutdown.IsCancellationRequested;
                result.TimedOut = timer.ElapsedMilliseconds >= timeoutMs;
                if (!result.Cancelled && !result.TimedOut) continue;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                process.WaitForExit(1000);
                break;
            }
            result.Cancelled |= cancellation.IsCancellationRequested || shutdown.IsCancellationRequested;
            if (process.HasExited) result.ExitCode = process.ExitCode;
            // Descendants may inherit the pipes. Never wait indefinitely for those handles.
            try { Task.WaitAll(new Task[] { stdout, stderr }, 1000); } catch (AggregateException) { }
            if (stdout.IsCompletedSuccessfully) result.Output += stdout.Result;
            if (stderr.IsCompletedSuccessfully) result.Output += stderr.Result;
            return result;
        }
    }
}
