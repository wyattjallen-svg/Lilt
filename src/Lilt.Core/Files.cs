using System.Text;
using System.Text.Json;

namespace Lilt.Core;

public static class Files
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Write, flush to disk, then rename, so a crash or power cut mid-save never leaves a half-written file.
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    // A file that can't be read is treated as absent, and one that won't parse is kept beside it as .corrupt
    // so the next save doesn't quietly replace what was there.
    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            SetAside(path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteJson<T>(string path, T value) => WriteAtomic(path, JsonSerializer.Serialize(value, Json));

    static void SetAside(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
