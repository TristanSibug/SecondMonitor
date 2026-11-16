using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

sealed record MonitorLayout(uint Width, uint Height, uint PixelFormat, int X, int Y,
    uint Rotation, uint Scaling, uint RefreshNumerator, uint RefreshDenominator, uint ScanLineOrdering)
{
    public bool Valid => Width > 0 && Height > 0 && Width <= 65535 && Height <= 65535;
}

// Store portable mode values by Windows' monitor device path, never by the
// source number or adapter LUID (both can change between boots).
sealed class MonitorLayoutStore
{
    readonly object sync = new object();
    readonly string path;
    public string LastError { get; private set; }
    Dictionary<string, MonitorLayout> layouts = new(StringComparer.OrdinalIgnoreCase);
    public MonitorLayoutStore(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
                layouts = JsonSerializer.Deserialize<Dictionary<string, MonitorLayout>>(File.ReadAllText(path))
                    ?? new();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        layouts = new(layouts, StringComparer.OrdinalIgnoreCase);
    }
    public static MonitorLayoutStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecondMonitor", "monitor-layouts.json"));
    public MonitorLayout Get(string identity)
    {
        lock (sync) return identity != null && layouts.TryGetValue(identity, out var saved) && saved != null && saved.Valid ? saved : null;
    }
    public void Remember(string identity, MonitorLayout layout)
    {
        if (string.IsNullOrEmpty(identity) || !layout.Valid) return;
        lock (sync)
        {
            if (layouts.TryGetValue(identity, out var old) && old == layout && LastError == null) return;
            layouts[identity] = layout;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(layouts));
                File.Move(temporary, path, true);
                LastError = null;
            }
            catch (IOException ex) { LastError = ex.Message; }
            catch (UnauthorizedAccessException ex) { LastError = ex.Message; }
        }
    }
}
