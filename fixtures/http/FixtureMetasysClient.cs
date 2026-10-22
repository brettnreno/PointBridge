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
        return Task.FromResult(new UiLogin(username == "fixture-user" && password == "fixture-only-password", true, "101", Token));
    }
    public Task<UiLogin> RefreshAsync(string origin, string token, CancellationToken ct)
    {
        Calls++;
        if (Mode == "tls") throw new HttpRequestException(Token, new System.Security.Authentication.AuthenticationException(Token));
        if (Mode == "outage") throw new HttpRequestException(Token);
        return Task.FromResult(new UiLogin(true, true, "101", Token));
    }
    public Task<UiIdentity> IdentityAsync(string origin, string token, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new UiIdentity(Mode == "substitute" ? "wrong-user" : "fixture-user", "fixture-id", "metasys", Mode == "disabled" ? ["Disabled"] : ["Active"]));
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
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.Host is not ("fixture-api.invalid" or "fixture-api-two.invalid"))
            throw new InvalidOperationException("Unexpected fixture API request; no real network transport exists.");
        if(request.Method==HttpMethod.Get)
        {
            var path=request.RequestUri.AbsolutePath;
            var schema="{\"type\":\"object\",\"properties\":{\"presentValue\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}}}";
            var body=path.EndsWith("/commands")?"{\"items\":[]}":path.EndsWith("/objects")?"{\"items\":[{\"id\":\"52cfddc0-411f-5b9e-9a6d-442cb62fecbb\",\"name\":\"Fixture point\"}]}":"{\"item\":{\"name\":\"Fixture point\",\"units\":\"unitEnumSet.degC\",\"presentValue\":20},\"effectivePermissions\":{\"canModify\":true},\"schema\":"+schema+"}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")});
        }
        if(request.Method!=HttpMethod.Post||!request.RequestUri.AbsolutePath.EndsWith("/login"))
        {Interlocked.Increment(ref PointWrites);throw new InvalidOperationException("Point writes forbidden by fixture.");}
        Interlocked.Increment(ref LoginCalls);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"fixture-api-token-not-real\",\"expires\":\"2099-01-01T00:00:00Z\"}", System.Text.Encoding.UTF8, "application/json") });
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
