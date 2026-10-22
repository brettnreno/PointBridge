using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

public sealed class MetasysLoginOptions
{
    public bool Enabled { get; set; }
    public string ServerOrigin { get; set; } = "";
    public int SessionMinutes { get; set; } = 30;
    public MetasysLoginGrant[] Grants { get; set; } = [];
}
public sealed record MetasysLoginGrant
{
    public string ServerOrigin { get; set; } = "";
    public string Username { get; set; } = "";
    public string StorageId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Role { get; set; } = "Admin";
}
public sealed record UiLogin(bool Authenticated, bool TermsAccepted, string StorageId, string Token);
public sealed record UiIdentity(string Username, string ProfileId, string AuthenticationType, string[] Statuses);
public sealed class MetasysContractException(string code) : Exception { public string Code { get; } = code; }
public sealed record MetasysSignInAttempt(ClaimsPrincipal? Principal, string Code);
public static class MetasysOrigin
{
    public static string? Normalize(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && !string.IsNullOrWhiteSpace(uri.Host)
        ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant().TrimEnd('/') : null;
    public static string Required(string value) => Normalize(value) ?? throw new InvalidOperationException("Invalid Metasys HTTPS origin.");
}
public interface IMetasysUiClient
{
    Task<UiLogin> LoginAsync(string serverOrigin, string username, string password, CancellationToken ct);
    Task<UiLogin> RefreshAsync(string serverOrigin, string token, CancellationToken ct);
    Task<UiIdentity> IdentityAsync(string serverOrigin, string token, CancellationToken ct);
    Task LogoutAsync(string serverOrigin, string token);
}

// Private Metasys UI contract; no public REST API support is claimed.
public sealed class MetasysUiClient : IMetasysUiClient, IDisposable
{
    private const string Auth = "/API/private-api/Authentication/";
    private readonly HttpClient http;
    public MetasysUiClient() : this(new HttpClient(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })) { }
    internal MetasysUiClient(HttpClient http) { this.http = http; http.Timeout = TimeSpan.FromSeconds(20); }

    internal static object Payload(string username, string password, long timestamp, byte[]? random = null)
    {
        var plain = Encoding.UTF8.GetBytes($"^{username}|{password}|{timestamp}$");
        var pad = random ?? RandomNumberGenerator.GetBytes(plain.Length);
        if (pad.Length != plain.Length) throw new InvalidOperationException("Invalid challenge input.");
        var mixed = new byte[plain.Length];
        try
        {
            for (var i = 0; i < plain.Length; i++) mixed[i] = (byte)(plain[i] ^ pad[i]);
            return new { username, password, domain = "", timestamp,
                challenge = Convert.ToBase64String(SHA256.HashData(mixed)), verifier = Convert.ToBase64String(pad) };
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(mixed); CryptographicOperations.ZeroMemory(pad); }
    }
    public async Task<UiLogin> LoginAsync(string serverOrigin, string username, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MetasysOrigin.Required(serverOrigin) + Auth + "LogIn")
        { Content = JsonContent.Create(Payload(username, password, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) };
        using var body = await SendAsync(request, ct);
        return LoginBody(body.RootElement);
    }
    public async Task<UiLogin> RefreshAsync(string serverOrigin, string token, CancellationToken ct)
    {
        using var request = Request(serverOrigin, Auth + "RefreshToken", token);
        using var body = await SendAsync(request, ct);
        return LoginBody(body.RootElement);
    }
    public async Task<UiIdentity> IdentityAsync(string serverOrigin, string token, CancellationToken ct)
    {
        using var request = Request(serverOrigin, "/API/private-api/v6/userinfo", token);
        using var body = await SendAsync(request, ct);
        return ParseIdentity(body.RootElement);
    }
    internal static UiIdentity ParseIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
            throw new MetasysContractException("profile-missing-or-invalid");
        // Installed UserProfile.AccountStatus is IList<string>, JsonProperty(Order=8),
        // without Required validation. Omitted/null is unreported, never an assertion of Active.
        string[] statuses = [];
        if (profile.TryGetProperty("accountStatus", out var status) && status.ValueKind != JsonValueKind.Null)
        {
            if (status.ValueKind == JsonValueKind.String) statuses = [status.GetString()!];
            else if (status.ValueKind == JsonValueKind.Array && status.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                statuses = status.EnumerateArray().Select(x => x.GetString()!).ToArray();
            else throw new MetasysContractException("account-status-shape");
            statuses = statuses.Select(s => s.Trim()).ToArray();
            if (statuses.Any(s => !new[] { "Active", "Dormant", "Disabled", "Locked", "Expired" }.Contains(s, StringComparer.OrdinalIgnoreCase)))
                throw new MetasysContractException("account-status-unknown");
        }
        var name = Text(profile, "userName"); var id = Text(profile, "id"); var type = Text(profile, "authenticationType");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
            throw new MetasysContractException("identity-fields-missing");
        return new(name, id, type, statuses);
    }
    public async Task LogoutAsync(string serverOrigin, string token)
    {
        using var request = Request(serverOrigin, Auth + "LogOut", token);
        using var body = await SendAsync(request, CancellationToken.None);
        if (!body.RootElement.TryGetProperty("IsAuthenticated", out var flag) || flag.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("Logout response was not confirmed.");
    }
    private static HttpRequestMessage Request(string serverOrigin, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, MetasysOrigin.Required(serverOrigin) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // No logging, automatic retries, cookies, default credentials, or redirect following.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new MetasysContractException("http-" + (int)response.StatusCode);
        // The verified PowerShell client accepts JSON independently of the media-type header.
        // Decode using the declared charset, then require valid JSON and the operation's exact shape.
        // No response text is logged or returned to the browser.
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
    }
    private static UiLogin LoginBody(JsonElement body) => new(
        body.TryGetProperty("IsAuthenticated", out var authenticated) && authenticated.ValueKind == JsonValueKind.True,
        body.TryGetProperty("IsTermsAndConditionsAccepted", out var terms) && terms.ValueKind == JsonValueKind.True,
        Text(body, "UserStorageId"), Text(body, "access_token"));
    private static string Text(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    }
    public void Dispose() => http.Dispose();
}

public sealed class MetasysSessions : BackgroundService
{
    private sealed class Session(string serverOrigin, UiLogin login, UiIdentity identity, MetasysLoginGrant grant, DateTimeOffset expires)
    {
        public readonly string ServerOrigin = serverOrigin;
        public string Token = login.Token;
        public readonly UiIdentity Identity = identity;
        public readonly MetasysLoginGrant Grant = grant;
        public readonly DateTimeOffset Expires = expires;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly IMetasysUiClient client;
    private readonly IOptionsMonitor<MetasysLoginOptions> options;
    private readonly TimeProvider clock;
    public MetasysSessions(IMetasysUiClient client, IOptionsMonitor<MetasysLoginOptions> options, TimeProvider clock)
    { this.client = client; this.options = options; this.clock = clock; }
    private string? CurrentOrigin => MetasysOrigin.Normalize(options.CurrentValue.ServerOrigin);
    public bool Available => options.CurrentValue.Enabled && CurrentOrigin is { } origin &&
        (options.CurrentValue.Grants ?? []).Any(g => ValidGrant(g) && MetasysOrigin.Normalize(g.ServerOrigin) == origin);
    internal int Count => sessions.Count;
    private static bool ValidGrant(MetasysLoginGrant? g) => g is not null &&
        MetasysOrigin.Normalize(g.ServerOrigin) is not null && !string.IsNullOrWhiteSpace(g.Username) && g.Username.Length <= 256 && !g.Username.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(g.StorageId) && !string.IsNullOrWhiteSpace(g.ProfileId) && g.Role is "Admin" or "Operator";
    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private MetasysLoginGrant? Allowed(string origin, UiLogin login, UiIdentity identity)
    {
        if (!options.CurrentValue.Enabled || CurrentOrigin != origin) return null;
        var matches = (options.CurrentValue.Grants ?? []).Where(g => ValidGrant(g) && MetasysOrigin.Normalize(g.ServerOrigin) == origin && SameName(g.Username, identity.Username) && g.StorageId == login.StorageId && g.ProfileId == identity.ProfileId).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string IdentityKey(string origin, MetasysLoginGrant grant) => "metasys:" + origin + ":" + grant.StorageId + ":" + grant.ProfileId;
    private static void Check(UiLogin login, UiIdentity identity, string expected)
    {
        if (!login.Authenticated || !login.TermsAccepted || string.IsNullOrWhiteSpace(login.Token) || string.IsNullOrWhiteSpace(login.StorageId) ||
            !SameName(identity.Username, expected) || string.IsNullOrWhiteSpace(identity.ProfileId) || identity.AuthenticationType != "metasys" ||
            identity.Statuses.Any(s => new[] { "Disabled", "Locked", "Expired" }.Contains(s, StringComparer.OrdinalIgnoreCase))) throw new InvalidOperationException("Metasys sign-in was not accepted.");
    }
    public async Task<ClaimsPrincipal?> OpenAsync(LoginRequest request, CancellationToken ct) => (await OpenResultAsync(request, ct)).Principal;
    public async Task<MetasysSignInAttempt> OpenResultAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username?.Trim() ?? "";
        var origin = CurrentOrigin;
        if (!Available || string.IsNullOrEmpty(request.Password) ||
            origin is null || !(options.CurrentValue.Grants ?? []).Any(g => ValidGrant(g) && MetasysOrigin.Normalize(g.ServerOrigin) == origin && SameName(g.Username, username))) return new(null, "account-not-granted");
        string? token = null;
        var stage = "login";
        try
        {
            var login = await client.LoginAsync(origin, username, request.Password, ct);
            token = login.Token;
            if (!login.Authenticated || string.IsNullOrWhiteSpace(token)) return new(null, "metasys-login-rejected");
            if (!login.TermsAccepted) return new(null, "metasys-terms-required");
            stage = "identity";
            var identity = await client.IdentityAsync(origin, token, ct);
            Check(login, identity, username);
            var grant = Allowed(origin, login, identity);
            if (grant is null || sessions.Count >= 100) return new(null, "identity-not-granted");
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[id] = new Session(origin, login, identity, grant with { }, clock.GetUtcNow().AddMinutes(Math.Clamp(options.CurrentValue.SessionMinutes, 1, 30)));
            token = null;
            return new(new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("auth_provider", "metasys"), new Claim("metasys_session", id),
                new Claim("metasys_origin", origin), new Claim(ClaimTypes.NameIdentifier, IdentityKey(origin, grant)),
                new Claim(ClaimTypes.Name, identity.Username), new Claim(ClaimTypes.Role, "Admin")
            }, CookieAuthenticationDefaults.AuthenticationScheme)), "accepted");
        }
        catch (Exception exception)
        {
            // Classify only controlled codes and exception types; never arbitrary message/body text.
            if (exception is MetasysContractException contract) return new(null, "metasys-" + stage + "-" + contract.Code);
            if (exception is JsonException) return new(null, "metasys-" + stage + "-json-invalid");
            for (Exception? error = exception; error is not null; error = error.InnerException)
                if (error is System.Security.Authentication.AuthenticationException) return new(null, "metasys-tls-failed");
            return new(null, stage == "identity" ? "metasys-identity-unavailable" : "metasys-unavailable");
        }
        finally { if (!string.IsNullOrEmpty(token)) await BestEffortLogout(origin, token); }
    }
    public async Task<bool> ValidateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.FindFirstValue("metasys_session");
        if (id is null || !sessions.TryGetValue(id, out var session)) return false;
        await session.Gate.WaitAsync(ct);
        try
        {
            if (!sessions.ContainsKey(id) || clock.GetUtcNow() >= session.Expires || !SameName(principal.Identity?.Name ?? "", session.Identity.Username) ||
                !options.CurrentValue.Enabled || CurrentOrigin != session.ServerOrigin || !(options.CurrentValue.Grants ?? []).Contains(session.Grant) ||
                principal.FindFirstValue("metasys_origin") != session.ServerOrigin || principal.FindFirstValue(ClaimTypes.NameIdentifier) != IdentityKey(session.ServerOrigin, session.Grant) ||
                principal.FindFirstValue(ClaimTypes.Role) != "Admin") throw new InvalidOperationException();
            var refreshed = await client.RefreshAsync(session.ServerOrigin, session.Token, ct);
            if (!string.IsNullOrWhiteSpace(refreshed.Token)) session.Token = refreshed.Token;
            var identity = await client.IdentityAsync(session.ServerOrigin, session.Token, ct);
            Check(refreshed, identity, session.Identity.Username);
            var grant = Allowed(session.ServerOrigin, refreshed, identity);
            if (grant != session.Grant) throw new InvalidOperationException();
            return true;
        }
        catch { await RemoveLocked(id, session); return false; }
        finally { session.Gate.Release(); }
    }
    public async Task CloseAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue("metasys_session");
        if (id is null || !sessions.TryGetValue(id, out var session)) return;
        await session.Gate.WaitAsync();
        try { await RemoveLocked(id, session); } finally { session.Gate.Release(); }
    }
    private async Task RemoveLocked(string id, Session session)
    {
        if (sessions.TryRemove(id, out _)) { var token = session.Token; session.Token = ""; await BestEffortLogout(session.ServerOrigin, token); }
    }
    private async Task BestEffortLogout(string serverOrigin, string token) { try { await client.LogoutAsync(serverOrigin, token); } catch { } }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var pair in sessions.Where(x => clock.GetUtcNow() >= x.Value.Expires).ToArray())
                { await pair.Value.Gate.WaitAsync(stoppingToken); try { await RemoveLocked(pair.Key, pair.Value); } finally { pair.Value.Gate.Release(); } }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var pair in sessions.ToArray())
            { await pair.Value.Gate.WaitAsync(); try { await RemoveLocked(pair.Key, pair.Value); } finally { pair.Value.Gate.Release(); } }
        }
    }
}
