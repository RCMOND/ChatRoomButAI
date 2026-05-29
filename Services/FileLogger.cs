using System.Text.Json;

namespace ChatRoom2.Services;

public class FileLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _options = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions _jsonOptions = new()
{
    WriteIndented = false,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
    public FileLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "chatlog.json");
    }

    public void Log(string type, string user, string content, DateTime time)
    {
        var record = new { type, user, content, time = time.ToString("O") };
        string line = JsonSerializer.Serialize(record, _options);
        lock (_lock) { File.AppendAllText(_logFilePath, line + Environment.NewLine); }
    }
}