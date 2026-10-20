using System.Text.Json;

namespace Lilt.Core;

public static class Files
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Write-then-rename so a crash mid-save never leaves a half-written file.
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteJson<T>(string path, T value) => WriteAtomic(path, JsonSerializer.Serialize(value, Json));
}
