using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilt.Core.Diagnostics;

public enum LogLevel { Info, Warning, Error, Crash }

// When, how bad, where from ("lilt", "engine", "theme", "windows"), what happened, and the details (a stack trace).
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message, string Detail = "");

// Lilt's own log, shown at about:logs: one JSON object per line in lilt.log, oldest first. It stays small: past MaxBytes the
// file becomes lilt.log.1 (the one before that is dropped), and long text is cut.
public sealed class LogBook(string folder)
{
    public const long MaxBytes = 512 * 1024;
    public const int MaxDetail = 16_000;
    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    readonly Lock gate = new();

    public string Folder => folder;
    public string Current => Path.Combine(folder, "lilt.log");
    string Previous => Current + ".1";

    public void Write(LogLevel level, string source, string message, string detail = "") =>
        Write(new LogEntry(DateTimeOffset.Now, level, source, Cut(message, 2000), Cut(detail, MaxDetail)));

    public void Write(LogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, json) + "\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(Current) && new FileInfo(Current).Length > MaxBytes) File.Move(Current, Previous, overwrite: true);
                File.AppendAllText(Current, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Newest first; lines that don't parse (a write cut short) are skipped.
    public IReadOnlyList<LogEntry> Read(int max = 1000)
    {
        var all = new List<LogEntry>();
        lock (gate)
            foreach (var file in new[] { Previous, Current })
                try
                {
                    if (!File.Exists(file)) continue;
                    foreach (var line in File.ReadLines(file))
                        try
                        {
                            if (line.Length > 0 && JsonSerializer.Deserialize<LogEntry>(line, json) is { Message: not null, Source: not null } e) all.Add(e with { Detail = e.Detail ?? "" });
                        }
                        catch (JsonException) { }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        all.Reverse();
        return all.Count > max ? all[..max] : all;
    }

    public void Clear()
    {
        lock (gate)
            foreach (var file in new[] { Previous, Current })
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
