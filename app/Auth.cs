using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public sealed class BridgeUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string Role { get; set; } = "Admin";
    public bool Enabled { get; set; } = true;
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Stamp { get; set; } = Guid.NewGuid().ToString("N");
}

public record LoginRequest(string Username, string Password);
public record SetupRequest(string SetupCode, string Username, string Password);
public record CreateUserRequest(string Username, string Password, string Role);
public record UpdateUserRequest(string Role, bool Enabled, string? Password);

public sealed class UserStore
{
    private const int Iterations = 210_000;
    private readonly object gate = new();
    private readonly string file;
    private readonly string setupFile;
    private readonly List<BridgeUser> users;
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public UserStore(DataLocation location)
    {
        file = Path.Combine(location.Path, "users.json");
        setupFile = Path.Combine(location.Path, "setup-code.txt");
        users = File.Exists(file)
            ? JsonSerializer.Deserialize<List<BridgeUser>>(File.ReadAllText(file), Options) ?? []
            : [];
        foreach (var user in users) user.Role = "Admin"; // Single permission level, including legacy records.
        if (users.Count == 0 && (!File.Exists(setupFile) || !System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(setupFile).Trim(), "^[A-F0-9]{48}$")))
            File.WriteAllText(setupFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
    }

    public bool SetupRequired { get { lock (gate) return users.Count == 0; } }

    public BridgeUser Setup(SetupRequest request)
    {
        lock (gate)
        {
            if (users.Count != 0) throw new InvalidOperationException("Initial setup is already complete.");
            var expected = File.ReadAllText(setupFile).Trim();
            var supplied = request.SetupCode?.Trim() ?? "";
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied)))
                throw new UnauthorizedAccessException("Setup code is incorrect. Read setup-code.txt in the service data folder as a Windows administrator.");
            var user = BuildUser(request.Username, request.Password, "Admin");
            users.Add(user);
            try { Save(); } catch { users.Clear(); throw; }
            // Keep the consumed token file empty so backup/ACL handling does not need a delete exception.
            File.WriteAllText(setupFile, "Setup completed. This code is no longer valid.");
            return user;
        }
    }

    public BridgeUser? Authenticate(LoginRequest request, string remote)
    {
        lock (gate)
        {
            var key = remote + ":" + (request.Username ?? "").ToUpperInvariant();
            if (failures.TryGetValue(key, out var failure) && failure.Count >= 5 && failure.Until > DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("Too many sign-in attempts. Try again in 15 minutes.");
            var user = users.Find(u => string.Equals(u.Username, request.Username, StringComparison.OrdinalIgnoreCase));
            // A dummy KDF prevents a cheap username-existence timing test.
            var salt = user == null ? new byte[32] : Convert.FromBase64String(user.Salt);
            var hash = Derive(request.Password ?? "", salt);
            var valid = user is { Enabled: true } && CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(user.PasswordHash));
            if (valid) { failures.Remove(key); return user; }
            var count = failure.Until > DateTimeOffset.UtcNow ? failure.Count + 1 : 1;
            failures[key] = (count, DateTimeOffset.UtcNow.AddMinutes(15));
            // Bound attacker-controlled username entries.
            if (failures.Count > 2000)
                foreach (var expired in failures.Where(x => x.Value.Until <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) failures.Remove(expired);
            return null;
        }
    }

    public bool ValidPrincipal(ClaimsPrincipal principal)
    {
        lock (gate)
        {
            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = principal.FindFirstValue("stamp");
            return users.Any(u => u.Id == id && u.Enabled && u.Stamp == stamp && principal.IsInRole(u.Role));
        }
    }

    public object[] PublicUsers()
    {
        lock (gate) return users.Select(u => (object)new { u.Id, u.Username, u.Role, u.Enabled }).ToArray();
    }

    public object Create(CreateUserRequest request)
    {
        lock (gate)
        {
            if (users.Any(u => string.Equals(u.Username, request.Username?.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That username already exists.");
            var user = BuildUser(request.Username, request.Password, request.Role);
            users.Add(user);
            try { Save(); } catch { users.Remove(user); throw; }
            return new { user.Id, user.Username, user.Role, user.Enabled };
        }
    }

    public object Update(string id, UpdateUserRequest request)
    {
        lock (gate)
        {
            ValidateRole(request.Role);
            var user = users.Find(u => u.Id == id) ?? throw new ArgumentException("User not found.");
            if (user.Enabled && user.Role == "Admin" && !request.Enabled && users.Count(u => u.Enabled && u.Role == "Admin") == 1)
                throw new ArgumentException("Keep at least one enabled administrator.");
            var copy = JsonSerializer.Deserialize<BridgeUser>(JsonSerializer.Serialize(user))!;
            user.Role = "Admin";
            user.Enabled = request.Enabled;
            if (!string.IsNullOrEmpty(request.Password)) SetPassword(user, request.Password);
            user.Stamp = Guid.NewGuid().ToString("N");
            try { Save(); } catch { users[users.IndexOf(user)] = copy; throw; }
            return new { user.Id, user.Username, user.Role, user.Enabled };
        }
    }

    private void Save()
    {
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(users, Options));
        File.Move(file + ".tmp", file, true);
    }

    private static BridgeUser BuildUser(string username, string password, string role)
    {
        ValidateRole(role);
        username = username?.Trim() ?? "";
        if (username.Length is < 3 or > 80 || username.Any(c => char.IsControl(c))) throw new ArgumentException("Use a username with 3–80 characters.");
        var user = new BridgeUser { Username = username, Role = "Admin" };
        SetPassword(user, password);
        return user;
    }

    private static void ValidateRole(string role)
    {
        if (role is not ("Admin" or "Operator")) throw new ArgumentException("Role must be Admin or Operator.");
    }

    private static void SetPassword(BridgeUser user, string password)
    {
        if (password == null || password.Length is < 12 or > 256) throw new ArgumentException("Use a password with 12–256 characters.");
        var salt = RandomNumberGenerator.GetBytes(32);
        user.Salt = Convert.ToBase64String(salt);
        user.PasswordHash = Convert.ToBase64String(Derive(password, salt));
    }

    private static byte[] Derive(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/metasys/login", async (LoginRequest request, MetasysSessions sessions, HttpContext context) =>
        {
            if (!sessions.Available) return Results.NotFound();
            var attempt = await sessions.OpenResultAsync(request, context.RequestAborted);
            var principal = attempt.Principal;
            if (principal is null)
            {
                var message = attempt.Code switch
                {
                    "account-not-granted" or "identity-not-granted" => "Metasys sign-in was not accepted for this application.",
                    "metasys-tls-failed" => "The Point Bridge process cannot establish a verified HTTPS connection to Metasys.",
                    "metasys-terms-required" => "Accept the required terms through the normal Metasys UI, then sign in here.",
                    "metasys-login-rejected" => "Metasys sign-in was not accepted for this application.",
                    _ => attempt.Code.StartsWith("metasys-identity-", StringComparison.Ordinal) ? "Metasys accepted the login, but Point Bridge could not validate the user profile. Diagnostic: " + attempt.Code : "The Metasys authentication service is unavailable. Diagnostic: " + attempt.Code
                };
                return Results.Json(new { error = message, code = attempt.Code is "account-not-granted" or "identity-not-granted" or "metasys-login-rejected" ? "metasys-sign-in-rejected" : attempt.Code }, statusCode: 401);
            }
            try
            {
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                    new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30), AllowRefresh = false });
                return Results.Ok(new { Username = principal.Identity?.Name, Role = principal.FindFirstValue(ClaimTypes.Role) });
            }
            catch { await sessions.CloseAsync(principal); return Results.StatusCode(503); }
        });
        app.MapPost("/api/auth/setup", async (SetupRequest request, UserStore users, HttpContext context) =>
        {
            if (!System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress!)) return Results.StatusCode(403);
            var user = users.Setup(request);
            await SignIn(context, user);
            return Results.Ok(new { user.Username, user.Role });
        });
        app.MapPost("/api/auth/login", async (LoginRequest request, UserStore users, HttpContext context) =>
        {
            var user = users.Authenticate(request, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (user == null) return Results.Json(new { error = "Username or password is incorrect." }, statusCode: 401);
            await SignIn(context, user);
            return Results.Ok(new { user.Username, user.Role });
        });
        app.MapPost("/api/auth/logout", async (HttpContext context, MetasysSessions sessions) =>
        {
            if (context.User.FindFirstValue("auth_provider") == "metasys") await sessions.CloseAsync(context.User);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { success = true });
        });
        app.MapGet("/api/auth/users", (UserStore users) => users.PublicUsers());
        app.MapPost("/api/auth/users", (CreateUserRequest request, UserStore users) => users.Create(request));
        app.MapPut("/api/auth/users/{id}", (string id, UpdateUserRequest request, UserStore users) => users.Update(id, request));
    }

    private static Task SignIn(HttpContext context, BridgeUser user)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role), new Claim("stamp", user.Stamp)
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
    }
}
