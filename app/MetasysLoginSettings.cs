using System.Text.Json;
using Microsoft.Extensions.Options;

public record MetasysServerRequest(string ServerOrigin, bool Enabled, bool AllowAnyAuthenticatedUser = true);
public record MetasysEnrollmentRequest(string ServerOrigin, string Username, string Password);
public record MetasysRemoveRequest(string Username);
public record MetasysBootstrapRequest(string SetupCode, string ServerOrigin, string Username, string Password);

public static class LocalMetasysServer
{
    public static string Required(string value)
    {
        var origin = MetasysOrigin.Required(value);
#if POINT_BRIDGE_AUTH_FIXTURES
        if (new Uri(origin).Host == "fixture.invalid") return origin;
#endif
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(new Uri(origin).Host);
            var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses.Select(address => address.Address)).ToArray();
            if (addresses.Length > 0 && addresses.All(address => System.Net.IPAddress.IsLoopback(address) || local.Contains(address))) return origin;
        }
        catch { }
        throw new ArgumentException("Use the trusted HTTPS address of Metasys installed on this computer.");
    }
}

public sealed class MetasysLoginSettings : IOptionsMonitor<MetasysLoginOptions>
{
    private readonly object gate = new();
    private readonly string file;
    private MetasysLoginOptions current;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MetasysLoginSettings(DataLocation location, IOptions<MetasysLoginOptions> initial)
    {
        file = Path.Combine(location.Path, "metasys-login.json");
        current = File.Exists(file)
            ? JsonSerializer.Deserialize<MetasysLoginOptions>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid Metasys login settings.")
            : initial.Value;
    }

    public MetasysLoginOptions CurrentValue => Volatile.Read(ref current);
    public MetasysLoginOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<MetasysLoginOptions, string?> listener) => null;
    public object Public() => new { CurrentValue.Enabled, CurrentValue.ServerOrigin, CurrentValue.AllowAnyAuthenticatedUser, users = CurrentValue.Grants.Select(grant => new { grant.Username, grant.Role }) };

    public void SaveServer(MetasysServerRequest request)
    {
        var origin = LocalMetasysServer.Required(request.ServerOrigin);
        lock (gate)
        {
            var grants = MetasysOrigin.Normalize(current.ServerOrigin) == origin ? current.Grants : [];
            if (request.Enabled && grants.Length == 0) throw new ArgumentException("Verify a Metasys login on this server before enabling sign-in.");
            Save(new() { ServerOrigin = origin, Enabled = request.Enabled, AllowAnyAuthenticatedUser = request.AllowAnyAuthenticatedUser, Grants = grants, SessionMinutes = current.SessionMinutes });
        }
    }

    public async Task Enroll(MetasysEnrollmentRequest request, IMetasysUiClient client, CancellationToken cancellationToken)
    {
        var origin = LocalMetasysServer.Required(request.ServerOrigin);
        var expected = CurrentValue;
        if (origin != MetasysOrigin.Normalize(expected.ServerOrigin)) throw new ArgumentException("Save this server before approving users.");
        var username = request.Username?.Trim() ?? "";
        if (username.Length is < 1 or > 256 || username.Any(char.IsControl) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
            throw new ArgumentException("Enter the Metasys username and password to verify this user.");
        string? token = null;
        try
        {
            var login = await client.LoginAsync(origin, username, request.Password, cancellationToken);
            token = login.Token;
            if (!login.Authenticated || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException();
            if (!login.TermsAccepted) throw new ArgumentException("Accept the terms in Metasys first, then verify this user again.");
            var identity = await client.IdentityAsync(origin, token, cancellationToken);
            MetasysSessions.Check(login, identity, username);
            lock (gate)
            {
                if (!ReferenceEquals(expected, current)) throw new ArgumentException("Settings changed during verification. Verify this user again.");
                var grant = new MetasysLoginGrant { ServerOrigin = origin, Username = identity.Username, StorageId = login.StorageId, ProfileId = identity.ProfileId, Role = "Admin" };
                var grants = current.Grants.Where(item => !string.Equals(item.Username, identity.Username, StringComparison.OrdinalIgnoreCase)).Append(grant).ToArray();
                if (grants.Length > 100) throw new ArgumentException("At most 100 Metasys users can be approved.");
                Save(new() { ServerOrigin = origin, Enabled = current.Enabled, AllowAnyAuthenticatedUser = current.AllowAnyAuthenticatedUser, Grants = grants, SessionMinutes = current.SessionMinutes });
            }
        }
        catch (ArgumentException) { throw; }
        catch { throw new ArgumentException("Metasys user verification failed. Check the credentials, account status, HTTPS certificate trust and server availability. No user was approved."); }
        finally { if (!string.IsNullOrEmpty(token)) { try { await client.LogoutAsync(origin, token); } catch { } } }
    }

    public void Remove(string username)
    {
        lock (gate)
        {
            var grants = current.Grants.Where(grant => !string.Equals(grant.Username, username, StringComparison.OrdinalIgnoreCase)).ToArray();
            Save(new() { ServerOrigin = current.ServerOrigin, Enabled = current.Enabled && (current.AllowAnyAuthenticatedUser || grants.Length > 0), AllowAnyAuthenticatedUser = current.AllowAnyAuthenticatedUser, Grants = grants, SessionMinutes = current.SessionMinutes });
        }
    }

    private void Save(MetasysLoginOptions next)
    {
        var temporary = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, next, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, file, true);
            Volatile.Write(ref current, next);
        }
        catch { throw new ArgumentException("Could not save Metasys login settings. The previous settings remain active."); }
    }
}

public static class MetasysLoginSetupEndpoints
{
    public static void MapMetasysLoginSetup(this WebApplication app)
    {
        app.MapPost("/api/auth/metasys/bootstrap", async (MetasysBootstrapRequest request, UserStore users, MetasysLoginSettings settings, IMetasysUiClient client, HttpContext context) =>
        {
            if (context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address)) return Results.StatusCode(403);
            users.ValidateSetupCode(request.SetupCode);
            settings.SaveServer(new(request.ServerOrigin, false, true));
            await settings.Enroll(new(request.ServerOrigin, request.Username, request.Password), client, context.RequestAborted);
            settings.SaveServer(new(request.ServerOrigin, true, true));
            users.CompleteMetasysSetup(request.SetupCode);
            return Results.Ok(new { success = true });
        });
        var group = app.MapGroup("/api/auth/metasys/settings");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.User.IsInRole("Admin") || context.HttpContext.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
                return Results.StatusCode(403);
            return await next(context);
        });
        group.MapGet("", (MetasysLoginSettings settings) => settings.Public());
        group.MapPut("", (MetasysServerRequest request, MetasysLoginSettings settings, UserStore users) =>
        {
            if (settings.CurrentValue.Enabled && !request.Enabled && !users.HasEnabledAdministrator)
                throw new ArgumentException("Create a local recovery administrator under User accounts before disabling Metasys sign-in or changing servers.");
            settings.SaveServer(request);
            return settings.Public();
        });
        group.MapPost("/users", async (MetasysEnrollmentRequest request, MetasysLoginSettings settings, IMetasysUiClient client, HttpContext context) =>
        {
            await settings.Enroll(request, client, context.RequestAborted);
            return settings.Public();
        });
        group.MapPost("/remove-user", (MetasysRemoveRequest request, MetasysLoginSettings settings) => { settings.Remove(request.Username); return settings.Public(); });
    }
}
