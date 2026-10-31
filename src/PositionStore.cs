using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ImageViewer;

/// <summary>
/// Remembers where each image was shown (window left/top in physical px and the zoom).
/// File: %LOCALAPPDATA%\imageviewer\positions.tsv, one line per image:
/// <c>lastUsedTicks \t x \t y \t scale \t fullPath</c> (paths cannot contain tabs).
/// Several viewers can run at once: saving re-reads the file and only overwrites the entries this process changed.
/// </summary>
internal static class PositionStore
{
    private const int MaxEntries = 2000;

    private sealed class Entry
    {
        public long Ticks;
        public int X, Y;
        public double Scale;
    }

    private static readonly Dictionary<string, Entry> Changed = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "imageviewer", "positions.tsv");

    public static bool TryGet(string path, out int x, out int y, out double scale)
    {
        if (!Changed.TryGetValue(path, out var e)) Load().TryGetValue(path, out e);
        x = e?.X ?? 0;
        y = e?.Y ?? 0;
        scale = e?.Scale ?? 0;
        return e is not null && scale > 0;
    }

    public static void Put(string path, int x, int y, double scale) =>
        Changed[path] = new Entry { Ticks = DateTime.UtcNow.Ticks, X = x, Y = y, Scale = scale };

    /// <summary>Write the changed entries; failures are ignored (remembering positions is not essential).</summary>
    public static void Save()
    {
        if (Changed.Count == 0) return;
        try
        {
            var all = Load();
            foreach (var kv in Changed) all[kv.Key] = kv.Value;
            var sb = new StringBuilder();
            foreach (var kv in all.OrderByDescending(kv => kv.Value.Ticks).Take(MaxEntries))
            {
                var e = kv.Value;
                sb.Append(e.Ticks).Append('\t').Append(e.X).Append('\t').Append(e.Y).Append('\t')
                  .Append(e.Scale.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(kv.Key).Append('\n');
            }
            string file = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
            Changed.Clear();
        }
        catch (Exception)
        {
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return map;
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string[] f = line.Split(new[] { '\t' }, 5);
                if (f.Length == 5
                    && long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                    && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    && double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
                {
                    map[f[4]] = new Entry { Ticks = ticks, X = x, Y = y, Scale = scale };
                }
            }
        }
        catch (Exception)
        {
        }
        return map;
    }
}
