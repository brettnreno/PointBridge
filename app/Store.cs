using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public class Store : IDisposable
{
    readonly object clientsGate = new();
    readonly Dictionary<string, (Connection Settings, Api Client)> clients = new();
    bool disposed;
    readonly IDataProtector protector;
    readonly string file;
    readonly SemaphoreSlim gate = new(1);
    static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    Settings config;
    public Settings Config => Volatile.Read(ref config);
    public string DirectoryPath { get; }

    public Store(IDataProtectionProvider provider, DataLocation location)
    {
        protector = provider.CreateProtector("MetasysBridge.v1");
        DirectoryPath = location.Path;
        Directory.CreateDirectory(DirectoryPath);
        file = Path.Combine(DirectoryPath, "config.json");
        config = File.Exists(file) ? ReadSettings(file) : new();
        if (config.SchemaVersion is < 1 or > 3) throw new InvalidDataException("Unsupported configuration schema version.");
        MigrateQueries();
        foreach (var mapping in config.Mappings)
        {
            mapping.SourceValue = null;
            mapping.DestinationValue = null;
            mapping.LastSuccess = null;
            mapping.Status = "Not synchronized since startup";
        }
    }

    public Settings Snapshot() => Clone(Config);
    static Settings Clone(Settings value) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(value, options), options)!;

    public void Side(string side)
    {
        if (side.StartsWith("destination:", StringComparison.Ordinal) && Guid.TryParse(side[12..], out _)) return;
        if (side is not ("source" or "destination")) throw new UserInputException("Unknown connection.");
    }

    public Connection Get(string side)
    {
        Side(side);
        if (side.StartsWith("destination:")) return Config.Destinations.GetValueOrDefault(side[12..]) ?? throw new UserInputException("Unknown destination connection.");
        return side == "source" ? Config.Source : Config.Destination;
    }

    public object Public(string side)
    {
        var connection = Get(side);
        return new { connection.BaseUrl, connection.ApiVersion, connection.Username, hasPassword = connection.Password.Length > 0 };
    }

    public Task Set(string side, Connection input)
    {
        Side(side);
        return Update(next =>
        {
            var previous = side.StartsWith("destination:") ? next.Destinations.GetValueOrDefault(side[12..]) ?? new() : side == "source" ? next.Source : next.Destination;
            if (input.Password.Length == 0 && previous.Password.Length > 0 && !SameAccount(previous, input))
                throw new UserInputException("Enter the password when changing the server address or account username.");
            var saved = new Connection { BaseUrl = input.BaseUrl, ApiVersion = input.ApiVersion, Username = input.Username };
            try { saved.Password = input.Password.Length == 0 ? previous.Password : protector.Protect(input.Password); }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Windows DPAPI could not protect credentials under this account. Use the configured Windows service identity with its user profile available. No credentials were saved.");
            }
            if (side.StartsWith("destination:")) { next.SchemaVersion=Math.Max(next.SchemaVersion,2); next.Destinations[side[12..]]=saved; }
            else if (side == "source") next.Source = saved; else next.Destination = saved;
        });
    }

    public Task ClearCredentials(string side)
    {
        Side(side);
        return Update(next => (side.StartsWith("destination:") ? next.Destinations[side[12..]] : side == "source" ? next.Source : next.Destination).Password = "");
    }

    public virtual Api Client(string side)
    {
        lock (clientsGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var connection = Get(side);
            if (connection.Password.Length == 0 || connection.BaseUrl.Length == 0)
                throw new InvalidOperationException($"Configure {side} credentials first.");
            if (clients.TryGetValue(side, out var existing) && !SameConnection(existing.Settings, connection))
            {
                clients.Remove(side); existing.Client.Dispose();
            }
            if (!clients.TryGetValue(side, out existing))
            {
                var snapshot = new Connection { BaseUrl = connection.BaseUrl, ApiVersion = connection.ApiVersion,
                    Username = connection.Username, Password = connection.Password };
                existing = (snapshot, CreateClient(snapshot, Unprotect(snapshot.Password)));
                clients.Add(side, existing);
            }
            return existing.Client.Share(side);
        }
    }
    protected virtual Api CreateClient(Connection connection, string password) => new(connection, password);
    static bool SameConnection(Connection first, Connection second) => SameAccount(first, second)
        && first.ApiVersion == second.ApiVersion && first.Password == second.Password;
    void RetireChangedClients()
    {
        lock (clientsGate)
            foreach (var entry in clients.ToArray())
            {
                Connection? current = entry.Key.StartsWith("destination:") ? Config.Destinations.GetValueOrDefault(entry.Key[12..])
                    : entry.Key == "source" ? Config.Source : Config.Destination;
                if (current != null && SameConnection(entry.Value.Settings, current)) continue;
                clients.Remove(entry.Key); entry.Value.Client.Dispose();
            }
    }
    public void Dispose()
    {
        lock (clientsGate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in clients.Values) entry.Client.Dispose();
            clients.Clear();
        }
    }
    public static string DestinationSide(Mapping mapping) => mapping.DestinationConnectionId == "destination" ? "destination" : "destination:" + mapping.DestinationConnectionId;
    public void CheckFeatures(Mapping mapping)
    {
        if (mapping.SourceKind is not ("metasys" or "json")) throw new UserInputException("Unknown source kind.");
        if ((mapping.SourceKind == "json" || mapping.DestinationConnectionId != "destination") && !Config.ExtendedSourcesEnabled)
            throw new InvalidOperationException("External sources and additional recipients are disabled. Enable the extension explicitly after configuration review.");
        if (mapping.SourceKind == "json" && (!Config.ExternalSources.TryGetValue(mapping.ExternalSourceId,out var source) || !source.Enabled))
            throw new InvalidOperationException("External source is missing or disabled.");
    }
    public virtual ExternalJsonSession ExternalClient(string id)
    {
        if (!Config.ExtendedSourcesEnabled || !Config.ExternalSources.TryGetValue(id,out var source) || !source.Enabled)
            throw new InvalidOperationException("External source is missing or disabled.");
        return new(EffectiveExternal(source),Unprotect(source.Secret),new HttpClient(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false }));
    }
    public virtual ExternalJsonSession ExternalTestClient(string id)
    {
        if(!Config.ExtendedSourcesEnabled||!Config.ExternalSources.TryGetValue(id,out var source))throw new InvalidOperationException("External source is missing or the extension is disabled.");
        return new(EffectiveExternal(source),Unprotect(source.Secret),new HttpClient(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false }));
    }
    public object PublicExternal(string id)
    {
        var source=Config.ExternalSources[id];
        var node=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(source,options))!;
        node.AsObject().Remove("secret");node.AsObject().Remove("protectedQuery");
        node["url"]=source.ProtectedQuery.Length>0?source.Url+SavedQueryMarker:source.Url;
        node["hasSecret"]=source.Secret.Length>0;node["hasProtectedQuery"]=source.ProtectedQuery.Length>0;return node;
    }
    public Task SetExternal(string id, ExternalJsonSource input)
    {
        ValidateExternalInput(id,input);
        return Update(next =>
        {
            var previous=next.ExternalSources.GetValueOrDefault(id);
            var saved=JsonSerializer.Deserialize<ExternalJsonSource>(JsonSerializer.Serialize(input,options),options)!;
            saved.ProtectedQuery = "";
            if (input.Url.EndsWith(SavedQueryMarker, StringComparison.Ordinal))
            {
                saved.Url = previous!.Url;
                saved.ProtectedQuery = previous.ProtectedQuery;
            }
            else ProtectQuery(saved);
            saved.Secret=input.Secret.Length>0?protector.Protect(input.Secret):previous?.Secret??"";
            next.SchemaVersion=Math.Max(next.SchemaVersion,saved.ProtectedQuery.Length>0?3:2);next.ExternalSources[id]=saved;
        });
    }
    public void ValidateExternalInput(string id,ExternalJsonSource input)
    {
        if (!Guid.TryParse(id,out _)) throw new UserInputException("Invalid external source ID.");
        var validation = input;
        if (input.Url.EndsWith(SavedQueryMarker, StringComparison.Ordinal))
        {
            if (!Config.ExternalSources.TryGetValue(id, out var existing) || existing.ProtectedQuery.Length == 0 || input.Url != existing.Url + SavedQueryMarker)
                throw new UserInputException("Re-enter the external source URL; saved query parameters cannot be moved to another address.");
            validation = JsonSerializer.Deserialize<ExternalJsonSource>(JsonSerializer.Serialize(input, options), options)!;
            validation.Url = ExternalUrl(existing);
        }
        ExternalJsonSession.Validate(validation);
        if (input.Secret.Length>8192 || input.Secret.Any(char.IsControl)) throw new UserInputException("Invalid API secret.");
            var previous=Config.ExternalSources.GetValueOrDefault(id);
            if (input.Secret.Length==0 && previous?.Secret.Length>0 &&
                (new Uri(input.Url).GetLeftPart(UriPartial.Authority)!=new Uri(previous.Url).GetLeftPart(UriPartial.Authority)||input.AuthMode!=previous.AuthMode||input.AuthHeader!=previous.AuthHeader))
                throw new UserInputException("Re-enter the API secret when changing its origin or authentication settings.");
    }
    public async Task<ValidationResult> ValidateMapping(Mapping mapping,CancellationToken ct,ExternalJsonPool? pool=null)
    {
        CheckFeatures(mapping);using var destination=Client(DestinationSide(mapping));
        if(mapping.SourceKind=="metasys") { using var source=Client("source");return await Preflight.Validate(mapping,source,destination,ct); }
        if(pool!=null)return await Preflight.ValidatePoint(mapping,ExternalPoint(mapping,await pool.Get(mapping.ExternalSourceId).Read(ct)),destination,ct);
        using var external=ExternalClient(mapping.ExternalSourceId);var sample=await external.Read(ct);
        return await Preflight.ValidatePoint(mapping,ExternalPoint(mapping,sample),destination,ct);
    }
    public static PointInfo ExternalPoint(Mapping m,ExternalSample sample)=>new(){Id="json:"+m.ExternalSourceId,Name=m.ExternalSourceId,Type=sample.Type,Units=sample.Units,
        Value=sample.Value.DeepClone(),Schema=new System.Text.Json.Nodes.JsonObject{["type"]=sample.Type},Reliability=sample.QualityReported?"reliabilityEnumSet.reliable":null};

    public virtual Api TemporaryClient(string side, Connection input)
    {
        Side(side);
        var saved = Get(side);
        if (input.Password.Length == 0 && SameAccount(saved, input) && saved.ApiVersion == input.ApiVersion)
            return Client(side);
        if (input.Password.Length == 0 && !SameAccount(Get(side), input))
            throw new UserInputException("Enter a password when testing a different server or account username.");
        var password = input.Password.Length > 0 ? input.Password : Unprotect(Get(side).Password);
        if (password.Length == 0) throw new InvalidOperationException("Enter a password first.");
        var client = CreateClient(input, password);
        client.ConnectionName = side;
        return client;
    }

    static bool SameAccount(Connection first, Connection second) =>
        string.Equals(first.BaseUrl.TrimEnd('/'), second.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && first.Username == second.Username;

    protected string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        try { return protector.Unprotect(value); }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Stored credentials cannot be decrypted by this Windows identity. Re-enter them using the configured service identity.");
        }
    }

    public const string SavedQueryMarker = "?__POINTBRIDGE_SAVED_QUERY__";
    public string ExternalUrl(ExternalJsonSource source) => source.Url + Unprotect(source.ProtectedQuery);
    ExternalJsonSource EffectiveExternal(ExternalJsonSource source)
    {
        var effective = JsonSerializer.Deserialize<ExternalJsonSource>(JsonSerializer.Serialize(source, options), options)!;
        effective.Url = ExternalUrl(source);
        return effective;
    }
    bool ProtectQuery(ExternalJsonSource source)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Query.Length == 0) return false;
        source.ProtectedQuery = protector.Protect(source.Url[source.Url.IndexOf('?')..]);
        source.Url = source.Url[..source.Url.IndexOf('?')];
        return true;
    }
    Settings ReadSettings(string path)
    {
        var text = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(text);
        if (node?["protectedLegacyBackup"] is { } protectedBackup) text = Unprotect(protectedBackup.GetValue<string>());
        return JsonSerializer.Deserialize<Settings>(text, options) ?? throw new InvalidDataException("The configuration is empty.");
    }
    void WriteMigration(string path, string text)
    {
        var temporary = path + ".security.tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    void MigrateQueries()
    {
        foreach (var suffix in new[] { ".bak", ".v1-before-extension", ".tmp" })
        {
            var backup = file + suffix;
            if (!File.Exists(backup)) continue;
            try
            {
                var settings = ReadSettings(backup);
                var changed = false;
                foreach (var source in settings.ExternalSources.Values) changed |= ProtectQuery(source);
                if (changed) { settings.SchemaVersion = Math.Max(settings.SchemaVersion, 3); WriteMigration(backup, JsonSerializer.Serialize(settings, options)); }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
            {
                WriteMigration(backup, JsonSerializer.Serialize(new { protectedLegacyBackup = protector.Protect(File.ReadAllText(backup)) }));
            }
        }
        var migrated = config.SchemaVersion < 3 && config.ExternalSources.Values.Any(source => source.ProtectedQuery.Length > 0);
        foreach (var source in config.ExternalSources.Values) migrated |= ProtectQuery(source);
        if (migrated) { config.SchemaVersion = Math.Max(config.SchemaVersion, 3); WriteMigration(file, JsonSerializer.Serialize(config, options)); }
    }

    public async Task Update(Action<Settings> change)
    {
        await gate.WaitAsync();
        try
        {
            var next = Clone(Config);
            change(next);
            await Write(next);
            Volatile.Write(ref config, next);
            RetireChangedClients();
        }
        finally { gate.Release(); }
    }

    // Retained for callers that intentionally persist the current in-memory state.
    public async Task Save()
    {
        await gate.WaitAsync();
        try { await Write(Config); }
        finally { gate.Release(); }
    }

    async Task Write(Settings value)
    {
        if (value.ExternalSources.Values.Any(source => source.ProtectedQuery.Length > 0)) value.SchemaVersion = Math.Max(value.SchemaVersion, 3);
        if(Config.SchemaVersion<2 && value.SchemaVersion>=2 && File.Exists(file) && !File.Exists(file+".v1-before-extension"))
            File.Copy(file,file+".v1-before-extension");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes);
            stream.Flush(true);
        }
        if (File.Exists(file)) File.Replace(temporary, file, file + ".bak", true);
        else File.Move(temporary, file);
    }
}
