using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public static class SecurityScenarios
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task QueryUpgrade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bridge-security-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var provider = new EphemeralDataProtectionProvider();
        var file = Path.Combine(directory, "config.json");
        const string marker = "synthetic-query-canary-never-public";
        const string address = "https://source.invalid/data?subscription-key=" + marker + "&client_secret=" + marker + "&city=New%20York";
        const string sourceId = "10000000-0000-4000-8000-000000000001";
        using var initial = new Store(provider, new(directory));
        await initial.Set("source", new() { BaseUrl = "https://source.invalid", Username = "synthetic-account", Password = "synthetic-api-password" });
        await initial.SetExternal(sourceId, new() { Url = address, ValuePointer = "/value", TimestampPointer = "/observed", UnitPointer = "/units", InputUnit = "degC", OutputUnit = "degC", MaxAgeSeconds = 60, Secret = "synthetic-header-secret" });
        await initial.Update(settings => { settings.TransfersEnabled = true; settings.Mappings.Add(new() { Id = "mapping", SourceKind = "json", ExternalSourceId = sourceId, SourceObject = "json:" + sourceId, DestinationObject = "destination-point", CommandPriority = "16" }); });
        var binding = RecoveryJournal.Binding(initial, initial.Config.Mappings[0], "source-point", "destination-point");
        var legacy = initial.Snapshot();
        legacy.SchemaVersion = 2;
        legacy.ExternalSources[sourceId].Url = address;
        legacy.ExternalSources[sourceId].ProtectedQuery = "";
        var passwordCiphertext = legacy.Source.Password;
        var secretCiphertext = legacy.ExternalSources[sourceId].Secret;
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var suffix in new[] { "", ".bak", ".v1-before-extension", ".tmp" }) File.WriteAllText(file + suffix, json);
        using var upgraded = new Store(provider, new(directory));
        Check(upgraded.Config.SchemaVersion == 3, "older binaries must refuse protected-query configuration rather than drop query parameters");
        foreach (var suffix in new[] { "", ".bak", ".v1-before-extension", ".tmp" }) Check(!File.ReadAllText(file + suffix).Contains(marker), "query canary remains in legacy file " + suffix);
        Check(upgraded.Config.TransfersEnabled && upgraded.Config.Mappings[0].CommandPriority == "16", "operator state and mapping priority changed");
        Check(upgraded.Config.Source.Password == passwordCiphertext && upgraded.Config.ExternalSources[sourceId].Secret == secretCiphertext, "existing encrypted credentials changed");
        Check(upgraded.ExternalUrl(upgraded.Config.ExternalSources[sourceId]) == address, "effective source URL changed");
        Check(RecoveryJournal.Binding(upgraded, upgraded.Config.Mappings[0], "source-point", "destination-point") == binding, "migration changed recovery binding");
        var publicJson = JsonSerializer.Serialize(upgraded.PublicExternal(sourceId));
        Check(!publicJson.Contains(marker) && !publicJson.Contains("protectedQuery") && !publicJson.Contains(secretCiphertext), "public response leaked query or ciphertext");
        var roundTrip = JsonSerializer.Deserialize<ExternalJsonSource>(publicJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await upgraded.SetExternal(sourceId, roundTrip);
        Check(upgraded.ExternalUrl(upgraded.Config.ExternalSources[sourceId]) == address, "redacted form round-trip lost query");
        roundTrip.Url = "https://other.invalid/data" + Store.SavedQueryMarker;
        try { await upgraded.SetExternal(sourceId, roundTrip); throw new Exception("query moved to a new origin"); } catch (UserInputException) { }
        roundTrip.Url = "https://source.invalid/data";
        await upgraded.SetExternal(sourceId, roundTrip);
        Check(upgraded.Config.ExternalSources[sourceId].ProtectedQuery.Length == 0, "explicit query removal failed");
        Check(SafeErrors.Message(new IOException(marker)).Contains(marker) == false && SafeErrors.Message(new UserInputException("Safe input guidance")) == "Safe input guidance", "unexpected errors not sanitized");
    }

    public static async Task StreamLimits()
    {
        foreach (var size in new[] { 0, 10, BoundedLineReader.MaximumLineCharacters, BoundedLineReader.MaximumLineCharacters + 1 })
        {
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', size) + "\r\nlast")));
            var lines = new BoundedLineReader(reader);
            try
            {
                var line = await lines.ReadLineAsync(default);
                Check(size <= BoundedLineReader.MaximumLineCharacters && line!.Length == size, "oversized line accepted");
                Check(await lines.ReadLineAsync(default) == "last" && await lines.ReadLineAsync(default) == null, "line/EOF behavior changed");
            }
            catch (UpstreamLimitException) { Check(size > BoundedLineReader.MaximumLineCharacters, "valid line rejected"); }
        }
        foreach (var body in new[] { string.Concat(Enumerable.Repeat("data:" + new string('x', 32768) + "\n", 9)), string.Concat(Enumerable.Repeat("data:x\n", 1025)) })
        {
            using var client = new Api(new() { BaseUrl = "https://fixture.invalid", ApiVersion = "v5", Username = "synthetic" }, "synthetic", new HttpClient(new StreamHandler(body)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await client.Stream((_, _) => Task.CompletedTask, timeout.Token); throw new Exception("oversized event accepted"); } catch (UpstreamLimitException) { }
        }
    }

    sealed class StreamHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var login = request.RequestUri!.AbsolutePath.EndsWith("/login");
            var content = login ? "{\"accessToken\":\"synthetic-token\",\"expires\":\"2099-01-01T00:00:00Z\"}" : "event:hello\ndata:{}\n\n" + body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, login ? "application/json" : "text/event-stream") });
        }
    }
}
