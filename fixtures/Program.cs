using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

var passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception("Fixture failed: " + name); passed++; Console.WriteLine("PASS " + name); }
try {
var bytes = Encoding.UTF8.GetBytes("^fixture|fixture-only-password|1700000000000$");
var payload = JsonSerializer.SerializeToElement(MetasysUiClient.Payload("fixture", "fixture-only-password", 1700000000000, new byte[bytes.Length]));
Assert(payload.GetProperty("challenge").GetString() == Convert.ToBase64String(SHA256.HashData(bytes)), "independent challenge fixture");
Assert(payload.GetProperty("domain").GetString() == "", "local domain");
var defaultOptions = new MutableOptions();
var fake = new FakeClient();
var clock = new FakeClock();
var provider = new MetasysSessions(fake, defaultOptions, clock);
Assert(!provider.Available && await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null && fake.Logins == 0, "default disabled, no remote call");
defaultOptions.Value.Enabled = true;
Assert(!provider.Available, "enabled without grants still unavailable");
MetasysLoginGrant grant = new() { ServerOrigin = "https://metasys-fixture.invalid", Username = "fixture-user", StorageId = "6", ProfileId = "fixture-profile", Role = "Operator" };
defaultOptions.Value.Grants = [grant];
Assert(await provider.OpenAsync(new("fixture-unapproved-user", "fixture-only-password"), default) is null && fake.Logins == 0, "ungranted name denied before login");
var principal = await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default);
Assert(principal is not null && principal.Identity?.Name == "fixture-user" && principal.IsInRole("Admin") && !principal.IsInRole("Operator"), "canonical identity receives sole administrator permission");
Assert(!JsonSerializer.Serialize(principal!.Claims.Select(c => c.Value)).Contains(FakeClient.Secret), "cookie claims contain no vendor token");
Assert(await provider.ValidateAsync(principal, default), "refresh plus canonical profile validation");
fake.Outage = true;
Assert(!await provider.ValidateAsync(principal, default) && provider.Count == 0, "outage revokes session without grace");
fake.Outage = false;
Assert(!await provider.ValidateAsync(principal, default), "revoked session cannot recover");
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
fake.Status = ["Disabled"];
Assert(!await provider.ValidateAsync(principal, default) && provider.Count == 0, "disabled profile revokes session");
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "disabled account cannot open session");
fake.Status = ["Locked"];
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "locked account denied");
fake.Status = ["Active"];
fake.Username = "other";
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "identity substitution rejected");
fake.Username = "fixture-user"; fake.ProfileId = "substituted-id";
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "profile ID substitution rejected");
fake.ProfileId = "fixture-profile"; fake.StorageId = "999";
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "storage ID substitution rejected");
fake.StorageId = "6"; fake.Terms = false;
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "terms gate preserved");
fake.Terms = true; fake.Authenticated = false;
Assert(await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default) is null, "denied authentication rejected");
fake.Authenticated = true;
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
defaultOptions.Value.Grants = [grant with { Role = "Admin" }];
Assert(!await provider.ValidateAsync(principal, default), "role change revokes old cookie");
defaultOptions.Value.Grants = [grant];
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
var forged = new ClaimsPrincipal(new ClaimsIdentity(principal.Claims.Where(c => c.Type != ClaimTypes.Role).Append(new(ClaimTypes.Role, "Operator")), "Cookies"));
Assert(!await provider.ValidateAsync(forged, default), "forged role rejected");
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
clock.Now += TimeSpan.FromMinutes(31);
Assert(!await provider.ValidateAsync(principal, default), "absolute expiry independent of sliding cookie");
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
await provider.CloseAsync(principal);
Assert(provider.Count == 0 && !await provider.ValidateAsync(principal, default), "explicit logout invalidates local session");
principal = (await provider.OpenAsync(new("fixture-user", "fixture-only-password"), default))!;
defaultOptions.Value.Enabled = false;
Assert(!await provider.ValidateAsync(principal, default), "disabling provider revokes session");
var root = Path.Combine(AppContext.BaseDirectory, "fixture-local-users"); Directory.CreateDirectory(root);
var store = new UserStore(new(root));
var local = store.SetupRequired ? store.Setup(new(File.ReadAllText(Path.Combine(root, "setup-code.txt")).Trim(), "fixture-local-admin", "fixture-only-password")) : store.Authenticate(new("fixture-local-admin", "fixture-only-password"), "fixture")!;
Assert(store.Authenticate(new("fixture-local-admin", "fixture-only-password"), "fixture") is not null, "local fallback works with provider disabled");
Assert(store.Authenticate(new("fixture-user", "fixture-only-password"), "fixture") is null, "no implicit password fallback or duplicate Metasys local user");

defaultOptions.Value.Enabled = true;
var operatorGrant = new MetasysLoginGrant { ServerOrigin = "https://metasys-fixture.invalid", Username = "fixture-operator", StorageId = "101", ProfileId = "fixture-operator-id", Role = "Operator" };
var adminGrant = new MetasysLoginGrant { ServerOrigin = "https://metasys-fixture.invalid", Username = "fixture-admin", StorageId = "102", ProfileId = "fixture-admin-id", Role = "Admin" };
defaultOptions.Value.Grants = [operatorGrant, adminGrant];
fake.Username = operatorGrant.Username; fake.StorageId = operatorGrant.StorageId; fake.ProfileId = operatorGrant.ProfileId;
var arbitraryOperator = await provider.OpenAsync(new(operatorGrant.Username, "fixture-only-password"), default);
Assert(arbitraryOperator is not null && arbitraryOperator.IsInRole("Admin") && !arbitraryOperator.IsInRole("Operator"), "arbitrary explicitly granted canonical user supported");
fake.Username = adminGrant.Username; fake.StorageId = adminGrant.StorageId; fake.ProfileId = adminGrant.ProfileId;
var arbitraryAdmin = await provider.OpenAsync(new(adminGrant.Username, "fixture-only-password"), default);
Assert(arbitraryAdmin is not null && arbitraryAdmin.IsInRole("Admin"), "multiple explicitly granted users receive administrator access");
var loginsBefore = fake.Logins;
Assert(await provider.OpenAsync(new("fixture-not-granted", "fixture-only-password"), default) is null && fake.Logins == loginsBefore, "successful-provider eligibility is never automatic");
defaultOptions.Value.Grants = [adminGrant];
var refreshesBefore = fake.Refreshes;
Assert(!await provider.ValidateAsync(arbitraryOperator!, default) && fake.Refreshes == refreshesBefore, "grant removal revokes before refresh");
defaultOptions.Value.ServerOrigin = "https://another-lab.invalid";
Assert(!provider.Available, "server change does not inherit old server grants");
Assert(!await provider.ValidateAsync(arbitraryAdmin!, default) && fake.Refreshes == refreshesBefore && fake.LastLogoutOrigin == "https://metasys-fixture.invalid", "server change revokes without forwarding tokens; logout pinned to original origin");
var scopedGrant = adminGrant with { ServerOrigin = "https://another-lab.invalid" };
defaultOptions.Value.Grants = [scopedGrant];
var scopedPrincipal = await provider.OpenAsync(new(adminGrant.Username, "fixture-only-password"), default);
Assert(scopedPrincipal?.FindFirstValue("metasys_origin") == "https://another-lab.invalid" && fake.LastLoginOrigin == "https://another-lab.invalid", "configured authentication origin scopes canonical identity");
await provider.CloseAsync(scopedPrincipal!);
defaultOptions.Value.Grants = [scopedGrant, scopedGrant with { Role = "Operator" }];
Assert(await provider.OpenAsync(new(adminGrant.Username, "fixture-only-password"), default) is null, "conflicting duplicate identity grants fail closed");
foreach (var badOrigin in new[] { "http://metasys-fixture.invalid", "https://name:password@metasys-fixture.invalid", "https://metasys-fixture.invalid/UI/", "https://metasys-fixture.invalid/?token=fixture", "https://metasys-fixture.invalid/#fragment" })
{
    defaultOptions.Value.ServerOrigin = badOrigin;
    loginsBefore = fake.Logins;
    Assert(!provider.Available && await provider.OpenAsync(new(adminGrant.Username, "fixture-only-password"), default) is null && fake.Logins == loginsBefore, "invalid configured origin rejected without remote call");
}
Assert(MetasysOrigin.Normalize("https://METASYS-FIXTURE.INVALID:443/") == "https://metasys-fixture.invalid", "origin normalization preserves server scope");

foreach (var mode in new[] { "success", "plain-json", "vendor-json", "utf16-json", "denied", "malformed", "redirect", "html", "outage" })
{
    var handler = new FixtureHandler(mode); using var client = new MetasysUiClient(new HttpClient(handler));
    var accepted = false;
    try { var response = await client.LoginAsync("https://metasys-fixture.invalid", "fixture", "fixture-only-password", default); accepted = response.Authenticated; }
    catch { }
    Assert(accepted == (mode is "success" or "plain-json" or "vendor-json" or "utf16-json"), "HTTP contract " + mode);
    Assert(handler.Calls == 1, "no HTTP login retry " + mode);
}
foreach(var fixture in new[]{ ("{}", "profile-missing-or-invalid"), ("{\"profile\":{}}", "identity-fields-missing"), ("{\"profile\":{\"accountStatus\":\"Active\"}}", "identity-fields-missing"), ("{\"profile\":{\"accountStatus\":[{}]}}", "account-status-shape"), ("{\"profile\":{\"accountStatus\":[]}}", "identity-fields-missing") })
{
 using var document=JsonDocument.Parse(fixture.Item1); string code="none";
 try { MetasysUiClient.ParseIdentity(document.RootElement); } catch(MetasysContractException error){code=error.Code;}
 Assert(code==fixture.Item2,"precise userinfo schema diagnosis "+fixture.Item2);
}
using(var document=JsonDocument.Parse("{\"profile\":{\"userName\":\"fixture-user\",\"id\":\"fixture-id\",\"authenticationType\":\"metasys\",\"accountStatus\":[\"Active\"]}}"))
 Assert(MetasysUiClient.ParseIdentity(document.RootElement).Username=="fixture-user","canonical profile parsing succeeds");
foreach(var media in new[]{"text/plain", "application/vnd.fixture+json", "application/json"})
{
 using var client = new MetasysUiClient(new HttpClient(new IdentityFixtureHandler(media)));
 var identity = await client.IdentityAsync("https://metasys-fixture.invalid", "fixture-only-token", default);
 Assert(identity.Username=="fixture-user" && identity.ProfileId=="101" && identity.AuthenticationType=="metasys", "userinfo canonical identity across media type "+media);
}
foreach(var statusField in new[]{"", ",\"accountStatus\":null", ",\"accountStatus\":[]", ",\"accountStatus\":\"Active\"", ",\"accountStatus\":[\"Active\"]"})
{
 using var document=JsonDocument.Parse("{\"profile\":{\"userName\":\"fixture-user\",\"id\":\"fixture-id\",\"authenticationType\":\"metasys\""+statusField+"}}");
 Assert(MetasysUiClient.ParseIdentity(document.RootElement).Username=="fixture-user","optional/scalar/array status retains canonical identity");
}
foreach(var deniedStatus in new[]{"Disabled","Locked","expired"})
{
 using var document=JsonDocument.Parse("{\"profile\":{\"userName\":\"fixture-user\",\"id\":\"fixture-profile\",\"authenticationType\":\"metasys\",\"accountStatus\":\""+deniedStatus+"\"}}");
 var parsed=MetasysUiClient.ParseIdentity(document.RootElement);
 var statusOptions=new MutableOptions();statusOptions.Value.Enabled=true;statusOptions.Value.Grants=[new MetasysLoginGrant{ServerOrigin="https://metasys-fixture.invalid",Username="fixture-user",StorageId="6",ProfileId="fixture-profile",Role="Operator"}];
 var statusProvider=new MetasysSessions(new FakeClient{Status=parsed.Statuses},statusOptions,new FakeClock());
 Assert(await statusProvider.OpenAsync(new("fixture-user","fixture-only-password"),default) is null,"scalar explicit denied status rejected "+deniedStatus);
}
foreach (var mode in new[] { "v6", "v5", "denied", "invalid-profile" })
{
    var transport = new IdentityRouteFixtureHandler(mode);
    using var routeClient = new MetasysUiClient(new HttpClient(transport));
    UiIdentity? routeIdentity = null;
    try { routeIdentity = await routeClient.IdentityAsync("https://metasys-fixture.invalid", "fixture-only-token", default); }
    catch (MetasysContractException) { }
    Assert(transport.Paths[0] == "/API/private-api/v6/userinfo", "profile route starts with v6: " + mode);
    Assert(transport.Paths.Count == (mode == "v5" ? 2 : 1), "v5 fallback occurs only for missing v6 route: " + mode);
    Assert((routeIdentity is not null) == (mode is "v6" or "v5"), "profile route preserves validation: " + mode);
    if (mode == "v5") Assert(transport.Paths[1] == "/API/private-api/v5/userinfo", "missing v6 route uses same-origin v5 userinfo");
}
var legacyTransport = new IdentityRouteFixtureHandler("v5");
using (var legacyClient = new MetasysUiClient(new HttpClient(legacyTransport)))
{
    var legacyOptions = new MutableOptions();
    legacyOptions.Value.Enabled = true;
    legacyOptions.Value.AllowAnyAuthenticatedUser = true;
    using var legacySessions = new MetasysSessions(legacyClient, legacyOptions, new FakeClock());
    var legacyPrincipal = await legacySessions.OpenAsync(new("fixture-user", "fixture-only-password"), default);
    Assert(legacyPrincipal is not null && legacyPrincipal.IsInRole("Admin"), "v5-only profile authenticates a validated Metasys user");
    Assert(await legacySessions.ValidateAsync(legacyPrincipal!, default), "v5-only profile preserves identity validation during refresh");
    await legacySessions.CloseAsync(legacyPrincipal!);
    Assert(legacyTransport.Paths.Count(path => path.EndsWith("/LogIn")) == 1, "v5 fallback never retries the password login");
    Assert(legacyTransport.Paths.Count(path => path == "/API/private-api/v5/userinfo") == 2, "v5 profile used for login and refreshed session");
    Assert(legacyTransport.Paths.Count(path => path.EndsWith("/LogOut")) == 1 && legacySessions.Count == 0, "v5 session logout removes access");
}
Console.WriteLine($"{passed} adapter assertions passed; fake transports only, no live authentication.");
} catch(Exception error) { Console.Error.WriteLine(error.Message.StartsWith("Fixture failed:") ? error.Message : "FAIL authentication fixture: "+error.GetType().Name); Environment.ExitCode=1; }

public record DataLocation(string Path);
sealed class IdentityRouteFixtureHandler(string mode) : HttpMessageHandler
{
    public List<string> Paths { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);
        if (path.EndsWith("/LogIn") || path.EndsWith("/RefreshToken") || path.EndsWith("/LogOut"))
        {
            var authenticationBody = path.EndsWith("/LogOut") ? "{\"IsAuthenticated\":false}" : "{\"IsAuthenticated\":true,\"IsTermsAndConditionsAccepted\":true,\"UserStorageId\":6,\"access_token\":\"fixture-only-token\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(authenticationBody, Encoding.UTF8, "application/json") });
        }
        var status = mode == "denied" ? HttpStatusCode.Unauthorized : mode == "v5" && path == "/API/private-api/v6/userinfo" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        var body = mode == "invalid-profile" ? "{}" : "{\"profile\":{\"userName\":\"fixture-user\",\"id\":\"fixture-id\",\"authenticationType\":\"metasys\"}}";
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
sealed class MutableOptions : IOptionsMonitor<MetasysLoginOptions>
{
    public MetasysLoginOptions Value = new() { ServerOrigin = "https://metasys-fixture.invalid" };
    public MetasysLoginOptions CurrentValue => Value;
    public MetasysLoginOptions Get(string? name) => Value;
    public IDisposable? OnChange(Action<MetasysLoginOptions, string?> listener) => null;
}
sealed class FakeClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
sealed class FakeClient : IMetasysUiClient
{
    public const string Secret = "fixture-token-never-output";
    public int Logins, Logouts, Refreshes;
    public string LastLoginOrigin = "", LastLogoutOrigin = "";
    public bool Outage, Terms = true, Authenticated = true;
    public string Username = "fixture-user", ProfileId = "fixture-profile", StorageId = "6";
    public string[] Status = ["Active"];
    public Task<UiLogin> LoginAsync(string serverOrigin, string username, string password, CancellationToken ct) { Logins++; LastLoginOrigin = serverOrigin; return Task.FromResult(new UiLogin(Authenticated, Terms, StorageId, Secret)); }
    public Task<UiLogin> RefreshAsync(string serverOrigin, string token, CancellationToken ct) { Refreshes++; return Outage ? throw new Exception(Secret) : Task.FromResult(new UiLogin(Authenticated, Terms, StorageId, Secret)); }
    public Task<UiIdentity> IdentityAsync(string serverOrigin, string token, CancellationToken ct) => Task.FromResult(new UiIdentity(Username, ProfileId, "metasys", Status));
    public Task LogoutAsync(string serverOrigin, string token) { Logouts++; LastLogoutOrigin = serverOrigin; return Task.CompletedTask; }
}
sealed class FixtureHandler(string mode) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.RequestUri?.AbsoluteUri != "https://metasys-fixture.invalid/API/private-api/Authentication/LogIn" || request.Method != HttpMethod.Post || request.Headers.Authorization is not null)
            throw new Exception("Unexpected fixture request.");
        if (mode == "outage") throw new HttpRequestException(FakeClient.Secret);
        var status = mode == "denied" ? HttpStatusCode.Unauthorized : mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK;
        var content = mode == "malformed" ? "{" : mode == "html" ? "<html>fixture</html>" : "{\"IsAuthenticated\":true,\"IsTermsAndConditionsAccepted\":true,\"UserStorageId\":6,\"access_token\":\"fixture-token-never-output\",\"UserName\":\"fix***\"}";
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, mode == "utf16-json" ? Encoding.Unicode : Encoding.UTF8, mode == "html" ? "text/html" : mode == "plain-json" ? "text/plain" : mode == "vendor-json" ? "application/vnd.fixture+json" : "application/json") });
    }
}

sealed class IdentityFixtureHandler(string media) : HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
 {
  if(request.RequestUri?.AbsoluteUri!="https://metasys-fixture.invalid/API/private-api/v6/userinfo" || request.Method!=HttpMethod.Get || request.Headers.Authorization?.Scheme!="Bearer") throw new Exception("Unexpected identity fixture request");
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"profile\":{\"userName\":\"fixture-user\",\"id\":101,\"authenticationType\":\"metasys\",\"accountStatus\":[\"Active\"]},\"permissions\":{}}", Encoding.UTF8, media)});
 }
}
