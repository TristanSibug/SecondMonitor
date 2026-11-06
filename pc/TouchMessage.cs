using System;
using System.Globalization;
using System.IO;
using System.Text;

readonly struct TouchMessage
{
    public readonly char Action;
    public readonly int Id;
    public readonly float X, Y;
    TouchMessage(char action, int id, float x, float y)
    {
        Action = action; Id = id; X = Math.Clamp(x, 0, 1); Y = Math.Clamp(y, 0, 1);
    }

    public static bool TryParse(string line, out TouchMessage message)
    {
        message = default;
        if (line == null || line.Length > 256) return false;
        string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        int offset, id = 0;
        if (parts.Length == 5 && parts[0] == "T")
        {
            offset = 1;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id > 65535) return false;
        }
        else if (parts.Length == 3) offset = 0;
        else return false;
        if (parts[offset].Length != 1) return false;
        char action = parts[offset][0];
        if (action != 'D' && action != 'M' && action != 'U') return false;
        int coordinate = offset == 1 ? 3 : 1;
        float x, y;
        if (!float.TryParse(parts[coordinate], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !float.TryParse(parts[coordinate + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        message = new TouchMessage(action, id, x, y);
        return true;
    }

    // Discard oversized messages through their newline, without unbounded allocations.
    public static string ReadLine(TextReader reader)
    {
        StringBuilder line = new StringBuilder();
        bool oversized = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') return oversized ? "" : line.ToString().TrimEnd('\r');
            if (line.Length < 256) line.Append((char)c);
            else oversized = true;
        }
        return line.Length == 0 && !oversized ? null : (oversized ? "" : line.ToString());
    }
}
