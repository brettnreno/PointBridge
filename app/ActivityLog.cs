using System.Collections.Concurrent;
using System.Text.Json;

public sealed class ActivityLog
{
    readonly object gate = new();
    readonly string file;
    static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();

    public ActivityLog(string directory)
    {
        file = Path.Combine(directory, "activity.jsonl");
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadLines(file).TakeLast(200))
        {
            try { if (JsonSerializer.Deserialize<AuditEntry>(line, options) is { } entry) Entries.Enqueue(entry); }
            catch (JsonException) { }
        }
    }

    public void Add(string level, string message)
    {
        var entry = new AuditEntry(DateTimeOffset.Now, level, message.Length > 4096 ? message[..4096] : message);
        lock (gate)
        {
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length >= 2 * 1024 * 1024)
                {
                    if (File.Exists(file + ".2")) File.Delete(file + ".2");
                    if (File.Exists(file + ".1")) File.Move(file + ".1", file + ".2");
                    File.Move(file, file + ".1");
                }
                File.AppendAllText(file, JsonSerializer.Serialize(entry, options) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Entries.Enqueue(new AuditEntry(DateTimeOffset.Now, "error", "Persistent activity log unavailable. Check data-directory permissions and free space."));
                Console.Error.WriteLine("Persistent activity log unavailable; category=" + error.GetType().Name);
            }
            Entries.Enqueue(entry);
            while (Entries.Count > 200) Entries.TryDequeue(out _);
        }
    }
}
