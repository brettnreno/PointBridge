using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Metasys Point Bridge requires Windows.");
var contentRoot = FindContentRoot(args);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
builder.Host.UseWindowsService(o => o.ServiceName = "MetasysPointBridge");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddEventLog(o => { if (OperatingSystem.IsWindows()) o.SourceName = "MetasysPointBridge"; });

var folder = Environment.GetEnvironmentVariable("METASYS_BRIDGE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MetasysPointBridge");
SecureDirectory(folder);
builder.Configuration.AddJsonFile(Path.Combine(folder, "websettings.json"), optional: true, reloadOnChange: false);
builder.Services.AddDataProtection().SetApplicationName("MetasysPointBridge")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(folder, "keys"))).ProtectKeysWithDpapi(true);
builder.Services.AddSingleton(new DataLocation(folder));
#if POINT_BRIDGE_AUTH_FIXTURES
builder.Services.AddSingleton<Store, FixtureApiStore>();
#else
builder.Services.AddSingleton<Store>();
#endif
builder.Services.AddSingleton<UserStore>();
builder.Services.Configure<MetasysLoginOptions>(builder.Configuration.GetSection("MetasysLogin"));
builder.Services.AddSingleton<MetasysLoginSettings>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<MetasysLoginOptions>>(sp => sp.GetRequiredService<MetasysLoginSettings>());
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
#if POINT_BRIDGE_AUTH_FIXTURES
builder.Services.AddSingleton<IMetasysUiClient, FixtureMetasysClient>();
#else
builder.Services.AddSingleton<IMetasysUiClient, MetasysUiClient>();
#endif
builder.Services.AddSingleton<MetasysSessions>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetasysSessions>());
builder.Services.AddSingleton<AuditLog>();
builder.Services.Configure<HealthPublishingOptions>(builder.Configuration.GetSection("HealthPublishing"));
builder.Services.AddSingleton<HealthFrameProducer>();
if (builder.Configuration.GetValue<bool>("HealthPublishing:Enabled"))
{
    Console.Error.WriteLine("Live Metasys health publishing is not commissioned. Only the separate offline simulator is implemented; leave HealthPublishing disabled.");
    Environment.ExitCode = 2;
    return;
}
var reconciliationSeconds = builder.Configuration.GetValue("Reliability:ReconciliationSeconds", 300);
var operationDeadlineSeconds = builder.Configuration.GetValue("Reliability:OperationDeadlineSeconds", 45);
var concurrentChecks = builder.Configuration.GetValue("Reliability:MaxConcurrentChecks", 8);
if (reconciliationSeconds is < 1 or > 3600 || operationDeadlineSeconds is < 1 or > 300 || concurrentChecks is < 2 or > 32)
    throw new InvalidOperationException("Invalid reliability settings: reconciliation 1-3600 seconds, operation deadline 1-300 seconds, concurrent checks 2-32.");
builder.Services.AddSingleton(sp => new Bridge(sp.GetRequiredService<Store>())
{
    HealthCheckInterval = TimeSpan.FromSeconds(reconciliationSeconds),
    OperationDeadline = TimeSpan.FromSeconds(operationDeadlineSeconds),
    MaxConcurrentChecks = concurrentChecks
});
var loginOnly = builder.Configuration.GetValue<bool>("Acceptance:LoginOnly");
var allowConnectionTests = loginOnly && builder.Configuration.GetValue<bool>("Acceptance:AllowConnectionTests");
if (!loginOnly) builder.Services.AddHostedService(sp => sp.GetRequiredService<Bridge>());
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "MetasysPointBridge.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return context.Response.WriteAsJsonAsync(new { error = "Sign in to continue." }); };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return context.Response.WriteAsJsonAsync(new { error = "Administrator permission is required." }); };
    options.Events.OnValidatePrincipal = async context =>
    {
        var isMetasys = context.Principal?.FindFirst("auth_provider")?.Value == "metasys";
        var valid = isMetasys
            ? await context.HttpContext.RequestServices.GetRequiredService<MetasysSessions>().ValidateAsync(context.Principal!, context.HttpContext.RequestAborted)
            : context.HttpContext.RequestServices.GetRequiredService<UserStore>().ValidPrincipal(context.Principal!);
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path == "/api/auth/login" || context.Request.Path == "/api/auth/metasys/login" || context.Request.Path == "/api/auth/setup" || context.Request.Path == "/api/auth/metasys/bootstrap" || context.Request.Path == "/api/auth/metasys/settings/users"
            ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("unlimited"));
    options.OnRejected = (context, _) => new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many sign-in attempts. Please wait a minute." }));
});

var remote = builder.Configuration.GetValue<bool>("Web:EnableRemote");
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 1024 * 1024;
    server.Listen(IPAddress.Loopback, builder.Configuration.GetValue("Web:LocalPort", 5088));
    if (!remote) return;
    var thumbprint = builder.Configuration["Web:CertificateThumbprint"]?.Replace(" ", "");
    if (string.IsNullOrWhiteSpace(thumbprint)) throw new InvalidOperationException("Remote access requires a trusted HTTPS certificate thumbprint.");
    using var certificates = new X509Store(StoreName.My, StoreLocation.LocalMachine);
    certificates.Open(OpenFlags.ReadOnly);
    var certificate = certificates.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, true).OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
        ?? throw new InvalidOperationException("The configured HTTPS certificate is missing, expired, untrusted, or has no private key. Grant the service identity private-key read access.");
    server.ListenAnyIP(builder.Configuration.GetValue("Web:HttpsPort", 5443), listen => listen.UseHttps(certificate));
});

var app = builder.Build();
var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
_ = app.Services.GetRequiredService<UserStore>();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps && !IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.IsHttps) context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (UnauthorizedAccessException exception) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = exception.Message }); }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = exception.Message }); }
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")) { await next(); return; }
    var publicEndpoint = path == "/api/session" || path == "/api/auth/login" || path == "/api/auth/metasys/login" || path == "/api/auth/setup" || path == "/api/auth/metasys/bootstrap";
    if (!publicEndpoint && context.User.Identity?.IsAuthenticated != true)
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Sign in to continue." }); return; }
    var requiresAdmin = path.StartsWithSegments("/api/connections") || path.StartsWithSegments("/api/auth/users");
    if (requiresAdmin && !context.User.IsInRole("Admin"))
    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Administrator permission is required." }); return; }
    if (context.Request.Method is not ("GET" or "HEAD"))
    {
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!context.Request.Headers["X-CSRF-Token"].Equals(csrf) || context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != expectedOrigin)
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Invalid session; reload the application." }); return; }
    }
    var connectionAction = context.Request.Method switch
    {
        "PUT" => path == "/api/connections/source" || path == "/api/connections/destination",
        "POST" => path == "/api/connections/source/test" || path == "/api/connections/destination/test",
        "DELETE" => path == "/api/connections/source/credentials" || path == "/api/connections/destination/credentials",
        _ => false
    };
    if(loginOnly&&builder.Configuration.GetValue<bool>("Acceptance:AllowExtendedConfiguration")&&
        (path.StartsWithSegments("/api/connections")||path.StartsWithSegments("/api/mappings")))
        connectionAction=true;
    if (loginOnly && !(allowConnectionTests && connectionAction) && context.Request.Method is not ("GET" or "HEAD") && !path.StartsWithSegments("/api/auth")
#if POINT_BRIDGE_AUTH_FIXTURES
        && !path.StartsWithSegments("/api/fixture")
#endif
        )
    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Transfers and mapping changes are locked for this acceptance instance. Connection tests require the connection-test acceptance mode." }); return; }
    await next();
    if (context.Request.Method is not ("GET" or "HEAD"))
        context.RequestServices.GetRequiredService<AuditLog>().Write(context.User.Identity?.Name ?? "anonymous", context.Request.Method, path, context.Response.StatusCode);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/session", (HttpContext context, UserStore users, MetasysSessions metasysSessions) => new
{
    metasysLoginAvailable = metasysSessions.Available,
    loginOnlyAcceptance = loginOnly,
    connectionTestsAllowed = !loginOnly || allowConnectionTests,
    transfersLocked = loginOnly,
    csrfToken = csrf,
    authenticated = context.User.Identity?.IsAuthenticated == true,
    setupRequired = users.SetupRequired && !metasysSessions.Available,
    username = context.User.Identity?.Name,
    role = context.User.IsInRole("Admin") ? "Admin" : context.User.IsInRole("Operator") ? "Operator" : null
});
app.MapAuthEndpoints();
app.MapMetasysLoginSetup();
app.MapBridgeEndpoints();
#if POINT_BRIDGE_AUTH_FIXTURES
app.MapFixtureAuthControl();
#endif
app.Run();

static void SecureDirectory(string path)
{
    var directory = Directory.CreateDirectory(path);
    var permissions = new DirectorySecurity();
    permissions.SetAccessRuleProtection(true, false);
    foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
        permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    directory.SetAccessControl(permissions);
}

static string FindContentRoot(string[] arguments)
{
    var position = Array.FindIndex(arguments, value => value.Equals("--contentRoot", StringComparison.OrdinalIgnoreCase));
    if (position >= 0 && position + 1 < arguments.Length) return Path.GetFullPath(arguments[position + 1]);
    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) return AppContext.BaseDirectory;
    for (var candidate = new DirectoryInfo(AppContext.BaseDirectory); candidate != null; candidate = candidate.Parent)
        if (File.Exists(Path.Combine(candidate.FullName, "apiTool.csproj")) && Directory.Exists(Path.Combine(candidate.FullName, "wwwroot"))) return candidate.FullName;
    return Directory.GetCurrentDirectory();
}
