using System.Diagnostics;
using System.Text;

if (args.Length > 0 && args[0] == "--child")
{
    if (args[1] == "fail") { Console.Error.Write("expected failure"); return 7; }
    if (args[1] == "wait") { Thread.Sleep(10000); return 0; }
    if (args[1] == "output")
    {
        Console.Out.Write(new string('o', 128 * 1024));
        Console.Error.Write(new string('e', 128 * 1024));
    }
    return 0;
}

int passed = 0, failed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

Test("touch protocol accepts old/new messages and clamps coordinates", () =>
{
    Check(TouchMessage.TryParse("T D 3 0.25 0.75", out var touch) && touch.Id == 3 && touch.Action == 'D' && touch.X == .25f, "new format");
    Check(TouchMessage.TryParse("M 0.5 0.2", out touch) && touch.Id == 0 && touch.Action == 'M', "legacy format");
    Check(TouchMessage.TryParse("T\tU   2  -0.1 1.1", out touch) && touch.X == 0 && touch.Y == 1, "whitespace/clamping");
});
Test("touch protocol rejects malformed and nonfinite values", () =>
{
    foreach (string text in new[] { "", "T  1 0.2 0.3", "T D -1 0 0", "T D 999999999999 0 0", "T D 65536 0 0", "T D 1 NaN 0", "T M 1 Infinity 0", "D 0 -Infinity", "T D 1 1e999 0", "T D 1 x 0", "T DOWN 1 0 0", "X 0 0", "T U 1 0", "T M 1 0 0 extra" })
        Check(!TouchMessage.TryParse(text, out _), "accepted " + text);
});
Test("oversized touch message does not consume the next valid message", () =>
{
    using var reader = new StringReader(new string('a', 100000) + "\nT D 1 0.1 0.2\r\nU 0.1 0.2");
    Check(!TouchMessage.TryParse(TouchMessage.ReadLine(reader), out _), "oversized accepted");
    Check(TouchMessage.TryParse(TouchMessage.ReadLine(reader), out var touch) && touch.Id == 1, "lost next message");
    Check(TouchMessage.TryParse(TouchMessage.ReadLine(reader), out touch) && touch.Action == 'U', "lost EOF message");
    Check(TouchMessage.ReadLine(reader) == null, "EOF");
});
Test("USB recovery retries the same serial with bounded backoff", () =>
{
    var recovery = new UsbRecovery();
    Check(recovery.ShouldAttempt("tablet", false, 0), "initial attempt");
    recovery.AttemptFinished(false, 0);
    Check(!recovery.ShouldAttempt("tablet", false, 2999), "premature retry");
    Check(recovery.ShouldAttempt("tablet", false, 3000), "same serial never retried");
    recovery.AttemptFinished(false, 3000);
    Check(!recovery.ShouldAttempt("tablet", false, 8999) && recovery.ShouldAttempt("tablet", false, 9000), "backoff not increased");
    recovery.AttemptFinished(true, 9000);
    Check(!recovery.ShouldAttempt("tablet", true, 100000), "reconfigured an active connection");
    Check(recovery.ShouldAttempt("tablet", false, 100000), "did not recover a dropped connection");
    Check(recovery.ShouldAttempt("other", false, 100001), "new serial delayed");
    Check(!recovery.ShouldAttempt(null, false, 100002), "attempted missing device");
    Check(recovery.ShouldAttempt("tablet", false, 100003), "replug not detected");
    for (int i = 0; i < 20; i++) recovery.AttemptFinished(false, 0);
    Check(!recovery.ShouldAttempt("tablet", false, 29999) && recovery.ShouldAttempt("tablet", false, 30000), "unbounded backoff");
});
Test("run settings are independent snapshots", () =>
{
    var settings = new AppSettings { Fps = 30 };
    var snapshot = settings.Copy(); settings.Fps = 60;
    Check(snapshot.Fps == 30, "running settings changed");
});

Test("setup check reports missing tools and unauthorized tablets", () =>
{
    var messages = new List<string>();
    SetupDiagnostics.Run(new AppSettings(), false, messages.Add, (file, arguments, timeout) =>
        file == "ffmpeg" ? new CommandResult() : new CommandResult { Started = true, ExitCode = 0, Output = "List of devices attached\nserial\tunauthorized\r\n" });
    Check(messages.Any(x => x.Contains("Cannot run ffmpeg")), "missing ffmpeg not diagnosed");
    Check(messages.Any(x => x.Contains("tap Allow")), "authorization guidance missing");
    Check(messages.Any(x => x.Contains("Select an available monitor")), "missing monitor not diagnosed");
});
Test("setup check encodes sample and verifies the app on the selected tablet", () =>
{
    var messages = new List<string>();
    var calls = new List<string>();
    SetupDiagnostics.Run(new AppSettings { GpuCapture = true }, true, messages.Add, (file, arguments, timeout) =>
    {
        calls.Add(file + " " + arguments);
        string output = arguments.EndsWith("-encoders") ? " V....D libx264 test\n" : arguments.EndsWith("-filters") ? "ddagrab" :
            arguments == "devices" ? "List of devices attached\nother\toffline\nchosen\tdevice\r\n" :
            arguments.Contains("pm path") ? "package:/data/app/secondmonitor.apk" : "";
        return new CommandResult { Started = true, ExitCode = 0, Output = output };
    });
    Check(messages.Any(x => x.Contains("successfully encoded")), "no encoder smoke check");
    Check(messages.Any(x => x.Contains("Tablet app is installed")), "app not verified");
    Check(calls.Any(x => x.Contains("-s \"chosen\" shell pm path")), "wrong adb device");
    Check(!calls.Any(x => x.Contains("am start") || x.Contains("reverse tcp")), "diagnostics modified the connection");
});

Test("restart isolates callbacks and retains the stopped tablet for close", () =>
{
    var sessions = new List<FakeSession>();
    var engine = new StreamEngine(serial => { var session = new FakeSession(serial); sessions.Add(session); return session; });
    var messages = new List<string>();
    engine.Log += messages.Add;
    Check(engine.Start(new AppSettings(), default), "initial start failed");
    var old = sessions[0]; old.DeviceSerial = "tablet";
    old.Emit("first");
    Check(!engine.Start(new AppSettings(), default) && sessions.Count == 1, "double start allowed");
    engine.Stop();
    engine.CloseTabletApp();
    Check(sessions[1].DeviceSerial == "tablet" && sessions[1].ClosedTablet, "lost device targeting after Stop");
    Check(engine.Start(new AppSettings(), default), "restart failed");
    var current = sessions[2];
    old.Emit("stale cleanup"); current.Emit("current");
    Check(messages.SequenceEqual(new[] { "first", "current" }), "stale session callback leaked");
    engine.DisplayChanged();
    Check(current.DisplayChanges == 1 && old.DisplayChanges == 0, "display event reached stale session");
    engine.Stop(); engine.Stop();
    Check(old.Stops == 1 && current.Stops == 1, "Stop not idempotent");
});
Test("Start waits for an in-progress Stop and failed starts can recover", () =>
{
    var old = new FakeSession(null) { StopEntered = new ManualResetEventSlim(), StopRelease = new ManualResetEventSlim() };
    var next = new FakeSession(null);
    int creations = 0;
    var engine = new StreamEngine(_ => Interlocked.Increment(ref creations) == 1 ? old : next);
    Check(engine.Start(new AppSettings(), default), "first start");
    var stopping = Task.Run(engine.Stop);
    Check(old.StopEntered.Wait(2000), "Stop not entered");
    var starting = Task.Run(() => engine.Start(new AppSettings(), default));
    try { Check(!starting.Wait(100), "new session started before cleanup finished"); }
    finally { old.StopRelease.Set(); }
    Check(stopping.Wait(2000) && starting.Wait(2000) && starting.Result, "restart hung");
    engine.Stop();
    int attempts = 0;
    var retry = new StreamEngine(_ => new FakeSession(null) { StartResult = ++attempts > 1 });
    Check(!retry.Start(new AppSettings(), default) && retry.Start(new AppSettings(), default), "failed start blocked retry");
    retry.Stop();
});

byte[] Chunk(string tag, byte[] payload)
{
    using var bytes = new MemoryStream();
    using var writer = new BinaryWriter(bytes, Encoding.ASCII, true);
    writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write((uint)payload.Length); writer.Write(payload);
    if (payload.Length % 2 != 0) writer.Write((byte)0);
    return bytes.ToArray();
}
byte[] Join(params byte[][] pieces) { return pieces.SelectMany(x => x).ToArray(); }
byte[] Movie(params byte[][] chunks)
{
    return Join(Encoding.ASCII.GetBytes("RIFF"), BitConverter.GetBytes(uint.MaxValue), Encoding.ASCII.GetBytes("AVI "),
        Chunk("LIST", Encoding.ASCII.GetBytes("hdrl")),
        Encoding.ASCII.GetBytes("LIST"), BitConverter.GetBytes(uint.MaxValue), Encoding.ASCII.GetBytes("movi"), Join(chunks));
}
Test("AVI delivers complete final frame with fragmented reads and odd padding", () =>
{
    byte[] first = { 0, 0, 0, 1, 9, 16, 42 }, last = { 0, 0, 1, 9, 16, 12 };
    using var input = new FragmentedStream(Movie(Chunk("JUNK", new byte[3]), Chunk("00dc", first), Chunk("00dc", Array.Empty<byte>()), Chunk("00dc", last)));
    var frames = new AviFrameReader(input);
    Check(frames.ReadFrame().SequenceEqual(first), "first frame corrupted");
    Check(frames.ReadFrame().SequenceEqual(last), "last frame requires a later marker");
    Check(frames.ReadFrame() == null, "EOF");
});
Test("AVI supports record lists and extended segments", () =>
{
    byte[] frame = { 0, 0, 1, 9, 16 };
    byte[] extended = Join(Encoding.ASCII.GetBytes("RIFF"), BitConverter.GetBytes(uint.MaxValue), Encoding.ASCII.GetBytes("AVIX"),
        Chunk("LIST", Join(Encoding.ASCII.GetBytes("movi"), Chunk("00dc", frame))));
    var frames = new AviFrameReader(new MemoryStream(Join(Movie(Chunk("LIST", Join(Encoding.ASCII.GetBytes("rec "), Chunk("00dc", frame)))), extended)));
    Check(frames.ReadFrame().SequenceEqual(frame) && frames.ReadFrame().SequenceEqual(frame), "lost segmented frames");
    Check(frames.ReadFrame() == null, "EOF");
});
Test("AVI rejects truncated or oversized output", () =>
{
    Throws<InvalidDataException>(() => new AviFrameReader(new MemoryStream(new byte[12])));
    Throws<EndOfStreamException>(() => new AviFrameReader(new MemoryStream(Movie(Chunk("00dc", new byte[10]))[..^3])).ReadFrame());
    var oversized = Join(Movie(), Encoding.ASCII.GetBytes("00dc"), BitConverter.GetBytes(uint.MaxValue));
    Throws<InvalidDataException>(() => new AviFrameReader(new MemoryStream(oversized)).ReadFrame());
});

string self = Environment.ProcessPath;
// Support both 'dotnet tests.dll' and the default apphost created by dotnet run.
string childPrefix = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? "\"" + typeof(FragmentedStream).Assembly.Location + "\" " : "";
Test("command runner preserves exit failures and drains both output streams", () =>
{
    var failure = CommandRunner.Run(self, childPrefix + "--child fail", 5000);
    Check(failure.Started && !failure.Success && failure.ExitCode == 7 && failure.Output.Contains("expected failure"), "exit status lost");
    var output = CommandRunner.Run(self, childPrefix + "--child output", 5000);
    Check(output.Success && output.Output.Count(c => c == 'o') == 128 * 1024 && output.Output.Count(c => c == 'e') == 128 * 1024, "pipe deadlock or lost output");
    Check(!CommandRunner.Run(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "", 100).Started, "missing executable accepted");
});
Test("command runner bounds timeout and cancellation", () =>
{
    Stopwatch timer = Stopwatch.StartNew();
    var timeout = CommandRunner.Run(self, childPrefix + "--child wait", 200);
    Check(timeout.TimedOut && !timeout.Success && timer.ElapsedMilliseconds < 3000, "timeout ineffective");
    using var cancel = new CancellationTokenSource(200);
    var cancelled = CommandRunner.Run(self, childPrefix + "--child wait", 5000, cancel.Token);
    Check(cancelled.Cancelled && !cancelled.Success, "cancellation ineffective");
    Check(CommandRunner.Run(self, childPrefix + "--child fail", 5000, cancel.Token).Cancelled, "cancelled command launched");
});

if (args.Contains("--ffmpeg"))
{
    Test("real ffmpeg packets decode and carry SPS/PPS with both encoder header modes", () =>
    {
        foreach (bool global in new[] { false, true })
        {
            var info = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -f lavfi -i testsrc2=size=128x96:rate=30 -frames:v 30 -c:v libx264 -preset ultrafast -tune zerolatency -profile:v main -pix_fmt yuv420p -bf 0 "
                + (global ? "-flags +global_header -bsf:v dump_extra=freq=keyframe,h264_metadata=aud=insert" : "-bsf:v h264_metadata=aud=insert")
                + " -flush_packets 1 -f avi pipe:1") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var encoder = Process.Start(info);
            var errors = encoder.StandardError.ReadToEndAsync();
            var reader = new AviFrameReader(encoder.StandardOutput.BaseStream);
            using var raw = new MemoryStream();
            int count = 0;
            byte[] packet;
            while ((packet = reader.ReadFrame()) != null) { count++; raw.Write(packet); }
            encoder.WaitForExit();
            Check(encoder.ExitCode == 0 && count == 30, "encoder lost frames: " + count + " " + errors.Result);
            string path = Path.Combine(Path.GetTempPath(), "secondmonitor-" + Guid.NewGuid() + ".h264");
            try
            {
                File.WriteAllBytes(path, raw.ToArray());
                var decoded = CommandRunner.Run("ffmpeg", "-hide_banner -loglevel error -xerror -i \"" + path + "\" -f null -", 10000);
                Check(decoded.Success && decoded.Output.Length == 0, "tablet payload not decodable: " + decoded.Output);
                var probe = CommandRunner.Run("ffprobe", "-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"" + path + "\"", 10000);
                Check(probe.Success && probe.Output.Trim() == "30", "decoded frame count: " + probe.Output);
            }
            finally { File.Delete(path); }
        }
    });
    Test("real ffmpeg delivers a single frame before a second input frame or EOF", () =>
    {
        var info = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -video_size 128x96 -framerate 30 -i - -c:v libx264 -preset ultrafast -tune zerolatency -profile:v main -pix_fmt yuv420p -bf 0 -bsf:v h264_metadata=aud=insert -flush_packets 1 -f avi pipe:1")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var encoder = Process.Start(info);
        var error = encoder.StandardError.ReadToEndAsync();
        try
        {
            encoder.StandardInput.BaseStream.Write(new byte[128 * 96 * 4]);
            encoder.StandardInput.BaseStream.Flush();
            var frame = Task.Run(() => new AviFrameReader(encoder.StandardOutput.BaseStream).ReadFrame());
            Check(frame.Wait(3000) && frame.Result.Length > 0, "first frame buffered until future input");
        }
        finally { encoder.Kill(true); encoder.WaitForExit(); }
    });
}
Console.WriteLine($"{passed} passed, {failed} failed" + (args.Contains("--ffmpeg") ? "" : "; ffmpeg integration unrun (pass --ffmpeg to include)"));
return failed == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes) : Stream
{
    readonly MemoryStream inner = new(bytes);
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(3, count));
    public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(3, buffer.Length)]);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class FakeSession(string serial) : IStreamSession
{
    public event Action<string> Log;
    public event Action<string> Status { add { } remove { } }
    public event Action<string> Stats { add { } remove { } }
    public string DeviceSerial { get; set; } = serial;
    public bool StartResult = true, ClosedTablet;
    public int Stops, DisplayChanges;
    public ManualResetEventSlim StopEntered, StopRelease;
    public bool Start(AppSettings settings, MonitorTarget screen) => StartResult;
    public void Stop() { Stops++; StopEntered?.Set(); StopRelease?.Wait(3000); }
    public void Emit(string message) => Log?.Invoke(message);
    public void SayGoodbye() { }
    public void PrepareForShutdown() { }
    public void DisplayChanged() => DisplayChanges++;
    public bool RunAdbReverse() => true;
    public bool OpenTabletApp() => true;
    public void CloseTabletApp() => ClosedTablet = true;
}
