using System.Text.Json;

public static class Endpoints
{
    public static void MapBridgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/state", (Store store, Bridge bridge) => new
        {
            source = store.Public("source"),
            destination = store.Public("destination"),
            extensionsEnabled = store.Config.ExtendedSourcesEnabled,
            destinations = store.Config.Destinations.Keys.ToDictionary(id=>id,id=>store.Public("destination:"+id)),
            externalSources = store.Config.ExternalSources.Keys.ToDictionary(id=>id,id=>store.PublicExternal(id)),
            mappings = store.Snapshot().Mappings,
            health = bridge.Health,
            armed = bridge.Armed,
            mode = "COV",
            status = bridge.Status
        });
        // Authenticated by the existing /api middleware. No values, credentials, or tokens.
        app.MapGet("/api/health/mappings", (Bridge bridge) => new
        {
            armed = bridge.Armed,
            checkedAt = DateTimeOffset.UtcNow,
            scope = "communication-and-readback; field-observation-age-not-established",
            reconciliationSeconds = bridge.HealthCheckInterval.TotalSeconds,
            operationDeadlineSeconds = bridge.OperationDeadline.TotalSeconds,
            maxConcurrentChecks = bridge.MaxConcurrentChecks,
            mappings = bridge.Health
        });
        // Diagnostic snapshot only. A GET sequence is not an independently scheduled heartbeat.
        app.MapGet("/api/health/watchdog-frame", (Bridge bridge, HealthFrameProducer producer) =>
            producer.Capture(bridge.Armed, bridge.Health, bridge.HealthCheckInterval, bridge.OperationDeadline));
        // User-triggered bounded download; no persisted export or automatic upload.
        app.MapGet("/api/diagnostics/export",(Store store,Bridge bridge)=>
            Results.File(Troubleshooting.Capture(store,bridge),"application/json","PointBridge-diagnostics.json"));

        app.MapPut("/api/connections/{side}", async (string side, Connection input, Store store, Bridge bridge) =>
        {
            store.Side(side);
            ValidateConnection(input);
            await bridge.Configure(() => store.Set(side, input));
            return store.Public(side);
        });

        app.MapDelete("/api/connections/{side}/credentials", async (string side, Store store, Bridge bridge) =>
        {
            store.Side(side);
            await bridge.Configure(() => store.ClearCredentials(side));
            return new { success = true };
        });

        app.MapPost("/api/connections/{side}/test", async (string side, HttpRequest request, Store store, CancellationToken ct) =>
        {
            store.Side(side);
            Connection? input = null;
            if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
                input = await request.ReadFromJsonAsync<Connection>(ct);
            if (input != null) ValidateConnection(input);
            using var client = input == null ? store.Client(side) : store.TemporaryClient(side, input);
            await client.Login(ct);
            return new { success = true, message = "Authentication successful. Validate a point mapping to check types, units, write permissions and supported commands." };
        });

        app.MapGet("/api/objects/{side}", async (string side, string? parentId, Store store, CancellationToken ct) =>
        {
            if (parentId != null && !Guid.TryParse(parentId, out _)) throw new ArgumentException("Parent must be a Metasys object GUID.");
            using var client = store.Client(side);
            return await client.Get(parentId == null ? "objects?depth=1&flatten=true" : $"objects/{parentId}/objects?depth=1&flatten=true", ct);
        });

        app.MapGet("/api/commands/{side}", async (string side, string @object, Store store, CancellationToken ct) =>
        {
            using var client = store.Client(side);
            return new { items = await Preflight.Commands(client, @object, ct) };
        });

        app.MapPost("/api/mappings/validate", async (Mapping mapping, Store store, CancellationToken ct) =>
        {
            return await store.ValidateMapping(mapping,ct);
        });
        app.MapPut("/api/connections/extensions",async(ExtensionRequest request,Store store,Bridge bridge)=>
        {
            await bridge.Configure(()=>store.Update(next=>{next.SchemaVersion=2;next.ExtendedSourcesEnabled=request.Enabled;}));
            return new{enabled=store.Config.ExtendedSourcesEnabled};
        });
        app.MapDelete("/api/connections/recipients/{id}",async(string id,Store store,Bridge bridge)=>
        {
            if(store.Config.Mappings.Any(m=>m.DestinationConnectionId==id))throw new ArgumentException("Remove mappings using this recipient first.");
            await bridge.Configure(()=>store.Update(next=>next.Destinations.Remove(id)));return new{success=true};
        });
        app.MapPut("/api/connections/json/{id}",async(string id,ExternalJsonSource input,Store store,Bridge bridge)=>
        {
            store.ValidateExternalInput(id,input);
            await bridge.Configure(()=>store.SetExternal(id,input));return store.PublicExternal(id);
        });
        app.MapPost("/api/connections/json/{id}/test",async(string id,Store store,CancellationToken ct)=>
        {
            using var session=store.ExternalTestClient(id);var sample=await session.Read(ct);
            return new{success=true,sample.Value,sample.Units,sample.ObservedAt,sample.FetchedAt,sample.QualityReported};
        });
        app.MapDelete("/api/connections/json/{id}/credentials",async(string id,Store store,Bridge bridge)=>
        {
            await bridge.Configure(()=>store.Update(next=>next.ExternalSources[id].Secret=""));return new{success=true};
        });
        app.MapDelete("/api/connections/json/{id}",async(string id,Store store,Bridge bridge)=>
        {
            if(store.Config.Mappings.Any(m=>m.SourceKind=="json"&&m.ExternalSourceId==id))throw new ArgumentException("Remove mappings using this source first.");
            await bridge.Configure(()=>store.Update(next=>next.ExternalSources.Remove(id)));return new{success=true};
        });

        app.MapPost("/api/mappings", async (Mapping mapping, Store store, Bridge bridge, CancellationToken ct) =>
        {
            Preflight.CheckInput(mapping);
            await bridge.Configure(async () =>
            {
                await ValidateLive(mapping, store, ct);
                mapping.Id = Guid.NewGuid().ToString();
                await store.Update(next => next.Mappings.Add(mapping));
            });
            return mapping;
        });

        app.MapPut("/api/mappings/{id}", async (string id, Mapping mapping, Store store, Bridge bridge, CancellationToken ct) =>
        {
            Preflight.CheckInput(mapping);
            await bridge.Configure(async () =>
            {
                if (!store.Config.Mappings.Any(x => x.Id == id)) throw new ArgumentException("Mapping not found.");
                mapping.Id = id;
                await ValidateLive(mapping, store, ct);
                await store.Update(next => next.Mappings[next.Mappings.FindIndex(x => x.Id == id)] = mapping);
            });
            return mapping;
        });

        app.MapDelete("/api/mappings/{id}", async (string id, Store store, Bridge bridge) =>
        {
            await bridge.Configure(() => store.Update(next => next.Mappings.RemoveAll(x => x.Id == id)));
            return new { success = true };
        });

        app.MapPost("/api/runtime", async (RuntimeRequest request, Bridge bridge) =>
        {
            await bridge.SetArmed(request.Armed);
            return new { armed = bridge.Armed };
        });

        app.MapGet("/api/logs", (Bridge bridge) => bridge.Logs.ToArray().Reverse());
    }

    static async Task ValidateLive(Mapping mapping, Store store, CancellationToken ct)
    {
        using var destination=store.Client(Store.DestinationSide(mapping));
        var validation = await store.ValidateMapping(mapping,ct);
        if (!validation.Valid) throw new ArgumentException(string.Join(" ", validation.Errors));
        if (mapping.Enabled)
        {
            foreach (var existing in store.Config.Mappings.Where(x => x.Enabled && x.Id != mapping.Id))
            {
                if(!string.Equals(store.Get(Store.DestinationSide(existing)).BaseUrl.TrimEnd('/'),store.Get(Store.DestinationSide(mapping)).BaseUrl.TrimEnd('/'),StringComparison.OrdinalIgnoreCase))continue;
                var existingId = await destination.Resolve(existing.DestinationObject, ct);
                if (existingId == validation.Destination!.Id) throw new ArgumentException("Another enabled mapping already targets this destination point.");
            }
        }
        mapping.SourceValue = null;
        mapping.DestinationValue = null;
        mapping.LastSuccess = null;
        mapping.Status = "Validated; transfers paused";
    }

    static void ValidateConnection(Connection input)
    {
        if (!Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/"
            || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new ArgumentException("Enter an HTTPS server origin, e.g. https://adx.example.com");
        if (input.ApiVersion is not ("v5" or "v6")) throw new ArgumentException("Choose API v5 or v6.");
        if (string.IsNullOrWhiteSpace(input.Username)) throw new ArgumentException("Enter the Metasys account username.");
    }
}
