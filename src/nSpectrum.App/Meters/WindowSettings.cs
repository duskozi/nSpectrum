using System.Globalization;
using System.IO;
using System.Text.Json;

namespace nSpectrum.App.Meters;

/// <summary>
/// Remembers each floating meter window's place, size and options in
/// %APPDATA%\nSpectrum\windows.json, as plain string values per window key.
/// </summary>
internal static class WindowSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nSpectrum", "windows.json");

    private static Dictionary<string, Dictionary<string, string>>? _all;

    private static Dictionary<string, Dictionary<string, string>> All
    {
        get
        {
            if (_all is not null) return _all;
            try
            {
                _all = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(FilePath))
                    : null;
            }
            catch
            {
                _all = null; // a broken file must never stop the app
            }
            return _all ??= [];
        }
    }

    public static Dictionary<string, string> For(string key)
    {
        if (!All.TryGetValue(key, out var d)) All[key] = d = [];
        return d;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience.
        }
    }

    public static string Get(this Dictionary<string, string> d, string name, string fallback) =>
        d.TryGetValue(name, out var v) ? v : fallback;

    public static bool Get(this Dictionary<string, string> d, string name, bool fallback) =>
        d.TryGetValue(name, out var v) && bool.TryParse(v, out bool b) ? b : fallback;

    public static double Get(this Dictionary<string, string> d, string name, double fallback) =>
        d.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : fallback;

    public static int Get(this Dictionary<string, string> d, string name, int fallback) =>
        d.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : fallback;

    public static void Set(this Dictionary<string, string> d, string name, object value) =>
        d[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
