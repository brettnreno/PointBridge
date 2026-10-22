public sealed class AuditLog(DataLocation location)
{
    private readonly object gate = new();
    public void Write(string actor, string method, string path, int status)
    {
        lock (gate)
        {
            var file = Path.Combine(location.Path, "audit.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length > 5_000_000)
                File.Move(file, file + ".previous", true);
            File.AppendAllText(file, System.Text.Json.JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, actor, method, path, status }) + Environment.NewLine);
        }
    }
}
