using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class SecurityFixtures
{
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bridge-legacy-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string password = "synthetic-legacy-password";
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 32));
        var legacy = new[] { new { id = "preserved-id", username = "legacy-recovery", role = "Admin", enabled = true, salt = Convert.ToBase64String(salt), passwordHash = hash, stamp = "preserved-stamp" } };
        var file = Path.Combine(directory, "users.json");
        File.WriteAllText(file, JsonSerializer.Serialize(legacy));
        var users = new UserStore(new(directory));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "preserved-id"), new Claim(ClaimTypes.Role, "Admin"), new Claim("stamp", "preserved-stamp") }, "fixture"));
        check(users.ValidPrincipal(principal), "legacy session valid before password upgrade");
        check(users.Authenticate(new("legacy-recovery", password), "fixture")?.Id == "preserved-id", "legacy recovery password still works");
        var upgraded = JsonSerializer.Deserialize<BridgeUser[]>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))![0];
        check(upgraded.PasswordVersion == 1 && upgraded.PasswordIterations >= 220000 && upgraded.PasswordHash != hash, "legacy password rehashed with versioned policy");
        check(users.ValidPrincipal(principal) && upgraded.Stamp == "preserved-stamp", "password upgrade preserves identity and active sessions");
        check(new UserStore(new(directory)).Authenticate(new("legacy-recovery", password), "fixture") != null, "upgraded password survives process restart");
        check(!File.ReadAllText(file).Contains(password), "recovery password never persisted in plaintext");
        using var client = new MetasysUiClient(new HttpClient(new OversizedHandler()));
        try { await client.LoginAsync("https://fixture.invalid", "synthetic", password, default); check(false, "oversized private response rejected"); }
        catch (MetasysContractException error) { check(error.Code == "response-too-large", "oversized private response rejected with controlled code"); }
    }
    sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1024 * 1024 + 1) + "{}", Encoding.UTF8, "application/json") });
    }
}
