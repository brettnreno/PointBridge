using System.Security.Claims;
public sealed class FixtureMetasysClient : IMetasysUiClient
{
    internal static string Mode = "normal";
    internal static int Calls, LogoutCalls;
    private const string Token = "fixture-token-not-real";
    public Task<UiLogin> LoginAsync(string origin, string username, string password, CancellationToken ct)
    {
        Calls++;
        if (Mode == "tls") throw new HttpRequestException(Token, new System.Security.Authentication.AuthenticationException(Token));
        if (Mode == "outage") throw new HttpRequestException(Token);
        var second = username == "fixture-second-user";
        return Task.FromResult(new UiLogin((username == "fixture-user" || second) && password == "fixture-only-password", true, second ? "102" : "101", second ? "fixture-second-token-not-real" : Token));
    }
    public Task<UiLogin> RefreshAsync(string origin, string token, CancellationToken ct)
    {
        Calls++;
        if (Mode == "tls") throw new HttpRequestException(Token, new System.Security.Authentication.AuthenticationException(Token));
        if (Mode == "outage") throw new HttpRequestException(Token);
        var second = token == "fixture-second-token-not-real";
        return Task.FromResult(new UiLogin(true, true, second ? "102" : "101", token));
    }
    public Task<UiIdentity> IdentityAsync(string origin, string token, CancellationToken ct)
    {
        Calls++;
        var second = token == "fixture-second-token-not-real";
        return Task.FromResult(new UiIdentity(Mode == "substitute" ? "wrong-user" : second ? "fixture-second-user" : "fixture-user", second ? "fixture-second-id" : "fixture-id", "metasys", Mode == "disabled" ? ["Disabled"] : ["Active"]));
    }
    public Task LogoutAsync(string origin, string token) { LogoutCalls++; return Task.CompletedTask; }
}
public static class FixtureAuthControl
{
    public static void MapFixtureAuthControl(this WebApplication app)
    {
        app.MapPost("/api/fixture/mode/{mode}", (string mode, HttpContext context) =>
        {
            if (!context.User.IsInRole("Admin")) return Results.StatusCode(403);
            if (mode is not ("normal" or "outage" or "tls" or "substitute" or "disabled")) return Results.BadRequest();
            FixtureMetasysClient.Mode = mode;
            return Results.Ok();
        });
        app.MapGet("/api/fixture/status", (HttpContext context) => context.User.IsInRole("Admin")
            ? Results.Ok(new { FixtureMetasysClient.Calls, FixtureMetasysClient.LogoutCalls, ApiLoginCalls = FixtureApiTransport.LoginCalls, PointWrites=FixtureApiTransport.PointWrites, JsonReads=FixtureJsonTransport.Calls, BridgeWorkerHosted = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Any(s => s is Bridge) }) : Results.StatusCode(403));
    }
}

public sealed class FixtureApiStore(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider, DataLocation location) : Store(provider, location)
{
    public override Api TemporaryClient(string side, Connection input) => new Api(input, "fixture-only", new HttpClient(new FixtureApiTransport()));
    public override Api Client(string side) => new Api(Get(side), "fixture-only", new HttpClient(new FixtureApiTransport()));
    public override ExternalJsonSession ExternalClient(string id)
    {
        CheckFeatures(new Mapping{SourceKind="json",ExternalSourceId=id});return ExternalTestClient(id);
    }
    public override ExternalJsonSession ExternalTestClient(string id)
    {
        if(!Config.ExtendedSourcesEnabled)throw new InvalidOperationException("Extension disabled.");
        var source=Config.ExternalSources[id];return new(source,Unprotect(source.Secret),new HttpClient(new FixtureJsonTransport()));
    }
}
public sealed class FixtureApiTransport : HttpMessageHandler
{
    public static int LoginCalls,PointWrites;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.Host is not ("fixture-api.invalid" or "fixture-api-two.invalid"))
            throw new InvalidOperationException("Unexpected fixture API request; no real network transport exists.");
        const string point="52cfddc0-411f-5b9e-9a6d-442cb62fecbb";
        var version=request.RequestUri.AbsolutePath.Split('/')[2];
        HttpResponseMessage Reply(string json)=>new(System.Net.HttpStatusCode.OK){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
        if(request.Method==HttpMethod.Post&&request.RequestUri.AbsolutePath.EndsWith("/objects/batch"))
        {
            var batch=System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if(batch?["method"]?.ToString()!="GET")throw new InvalidOperationException("Only read batches allowed by fixture.");
            var responses=new System.Text.Json.Nodes.JsonArray();
            foreach(var operation in batch!["requests"]!.AsArray())
            {
                if(operation?["relativeUrl"]?.ToString()!=point+"/attributes/presentValue")throw new InvalidOperationException("Unexpected batch target.");
                responses.Add(new System.Text.Json.Nodes.JsonObject{["id"]=operation["id"]!.ToString(),["status"]=200,
                    ["body"]=System.Text.Json.Nodes.JsonNode.Parse("{\"item\":{\"presentValue\":20},\"condition\":{\"presentValue\":{\"reliability\":\"reliabilityEnumSet.reliable\",\"priority\":\"writePriorityEnumSet.priorityDefault\"}},\"password\":\"fixture-sensitive-value-never-display\"}")});
            }
            return Reply(new System.Text.Json.Nodes.JsonObject{["responses"]=responses}.ToJsonString());
        }
        if(request.Method==HttpMethod.Get)
        {
            var path=request.RequestUri.AbsolutePath;
            if(path.EndsWith("/samples"))return Reply(version=="v4"?"{\"items\":[{\"timestamp\":\"2026-10-22T12:00:00Z\",\"value\":{\"value\":20,\"units\":\"unitEnumSet.degC\"},\"isReliable\":true}],\"next\":null}":"{\"items\":[{\"sampleTime\":\"2026-10-22T12:00:00Z\",\"value\":20,\"reliability\":\"reliabilityEnumSet.reliable\"}]}");
            if(path.EndsWith("/activities"))return Reply("{\"items\":[{\"id\":\"fixture-activity\",\"creationTime\":\"2026-10-22T12:00:00Z\",\"objectName\":\"Fixture temperature\",\"activityManagementStatus\":\"pending\",\"alarm\":{\"type\":\"alarmValueEnumSet.avHighLimit\",\"priority\":2},\"audit\":{\"actionType\":\"auditActionTypeEnumSet.writeAuditActionType\",\"user\":\"fixture-sensitive-value-never-display\",\"postData\":\"fixture-sensitive-value-never-display\"}}],\"next\":null}");
            if(path.EndsWith("/points"))return Reply("{\"items\":[{\"id\":\"32cfddc0-411f-5b9e-9a6d-442cb62fecbb\",\"label\":\"Fixture temperature\",\"objectUrl\":\"https://"+request.RequestUri.Host+"/api/"+version+"/objects/"+point+"\",\"attribute\":\"presentValue\"}],\"next\":null}");
            if(path.EndsWith("/equipment")||path.EndsWith("/spaces")||path.EndsWith("/networkDevices"))return Reply("{\"items\":[{\"id\":\""+point+"\",\"name\":\"Fixture "+path.Split('/').Last()+"\"}],\"next\":null}");
            var schema="{\"type\":\"object\",\"properties\":{\"presentValue\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}}}";
            var body=path.EndsWith("/commands")?"{\"items\":[]}":path.EndsWith("/objects")?"{\"items\":[{\"id\":\"52cfddc0-411f-5b9e-9a6d-442cb62fecbb\",\"name\":\"Fixture point\"}]}":"{\"item\":{\"name\":\"Fixture point\",\"units\":\"unitEnumSet.degC\",\"presentValue\":20},\"effectivePermissions\":{\"canModify\":true},\"schema\":"+schema+"}";
            return Reply(body);
        }
        if(request.Method!=HttpMethod.Post||!request.RequestUri.AbsolutePath.EndsWith("/login"))
        {Interlocked.Increment(ref PointWrites);throw new InvalidOperationException("Point writes forbidden by fixture.");}
        Interlocked.Increment(ref LoginCalls);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"fixture-api-token-not-real\",\"expires\":\"2099-01-01T00:00:00Z\"}", System.Text.Encoding.UTF8, "application/json") };
    }
}
public sealed class FixtureJsonTransport:HttpMessageHandler
{
    public static int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.Method!=HttpMethod.Get||request.RequestUri?.Host!="fixture-json.invalid")throw new InvalidOperationException("Only the in-memory JSON fixture is available.");
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"value\":20,\"unit\":\"unitEnumSet.degC\",\"observed\":\""+DateTimeOffset.UtcNow.ToString("O")+"\",\"quality\":\"good\"}",System.Text.Encoding.UTF8,"application/json")});
    }
}
