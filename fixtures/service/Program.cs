using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length != 4 || !args[0].StartsWith("PointBridgeSecurityFixture", StringComparison.Ordinal)) throw new ArgumentException("Use only the isolated service test harness.");
ServiceBase.Run(new UpgradeProbe(args[0], args[1], args[2], args[3]) { ServiceName = args[0] });

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class UpgradeProbe(string name, string mode, string directory, string resultFile) : ServiceBase
{
    const string ApiPassword = "synthetic-api-password-for-service-upgrade";
    const string LocalPassword = "synthetic-recovery-password-for-service-upgrade";
    const string SourceId = "10000000-0000-4000-8000-000000000001";
    const string SourceUrl = "https://synthetic-source.invalid/data?client_secret=synthetic-query-secret";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    readonly string fixtureName = name;

    protected override void OnStart(string[] arguments)
    {
        Task.Run(async () =>
        {
            try
            {
                await RunProbe();
                File.WriteAllText(resultFile, JsonSerializer.Serialize(new { passed = true, mode, processId = Environment.ProcessId, sessionId = Process.GetCurrentProcess().SessionId, keyFiles = mode == "attack" ? [] : Directory.GetFiles(Path.Combine(directory, "keys"), "*.xml").Select(Path.GetFileName).ToArray() }));
                if (mode == "hold")
                {
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (!File.Exists(Path.Combine(Path.GetDirectoryName(resultFile)!, "attack.json")) && DateTime.UtcNow < until) await Task.Delay(100);
                }
            }
            catch (Exception error) { File.WriteAllText(resultFile, JsonSerializer.Serialize(new { passed = false, mode, category = error.GetType().Name, message = SafeErrors.Message(error) })); }
            finally { Stop(); }
        });
    }

    void Check(bool value, string code) { if (!value) throw new UserInputException(code); }
    async Task RunProbe()
    {
        Check(WindowsIdentity.GetCurrent().User?.Value == "S-1-5-19" && Process.GetCurrentProcess().SessionId == 0, "fixture-not-an-unattended-LocalService-process");
        if (mode == "attack")
        {
            using var upgrade = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(resultFile)!, "upgrade.json")));
            var targets = upgrade.RootElement.GetProperty("keyFiles").EnumerateArray().Select(item => Path.Combine(directory, "keys", item.GetString()!)).Prepend(Path.Combine(directory, "config.json")).ToArray();
            Check(targets.Length > 1, "fixture-did-not-produce-persisted-DPAPI-key-files");
            using var held = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(resultFile)!, "hold.json")));
            var targetProcess = held.RootElement.GetProperty("processId").GetInt32();
            foreach (var access in new uint[] { 0x0010, 0x00040000 })
            {
                var handle = OpenProcess(access, false, targetProcess);
                if (handle != IntPtr.Zero) { CloseHandle(handle); throw new UserInputException("other-LocalService-process-can-read-memory-or-rewrite-process-DACL"); }
                Check(Marshal.GetLastWin32Error() == 5, "fixture-process-not-available-for-isolation-test");
            }
            foreach (var file in targets)
            {
                try { File.ReadAllText(file); throw new UserInputException("other-LocalService-process-read-private-data"); }
                catch (UnauthorizedAccessException) { }
                try
                {
                    var permissions = new FileInfo(file).GetAccessControl();
                    permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(file).SetAccessControl(permissions);
                    throw new UserInputException("other-LocalService-process-changed-owner-DACL");
                }
                catch (UnauthorizedAccessException) { }
            }
            return;
        }
        if (mode != "legacy") ServiceSecurity.SecureDirectory(directory, true, fixtureName);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")), options => options.SetApplicationName("MetasysPointBridge").ProtectKeysWithDpapi(true));
        using var store = new ProbeStore(provider, new(directory));
        if (mode == "legacy")
        {
            await store.Set("source", new() { BaseUrl = "https://synthetic-source.invalid", Username = "synthetic-api-user", Password = ApiPassword });
            await store.Set("destination", new() { BaseUrl = "https://synthetic-destination.invalid", ApiVersion = "v4", Username = "synthetic-api-user", Password = ApiPassword });
            await store.Update(settings =>
            {
                settings.SchemaVersion = 2;
                settings.ExtendedSourcesEnabled = true;
                settings.TransfersEnabled = true;
                settings.ExternalSources[SourceId] = new() { Url = SourceUrl, ValuePointer = "/value", TimestampPointer = "/at", UnitPointer = "/units", InputUnit = "degC", OutputUnit = "degC", MaxAgeSeconds = 60 };
                settings.Mappings.Add(new() { Id = "preserved-mapping", Name = "Synthetic preserved mapping", SourceKind = "json", ExternalSourceId = SourceId, SourceObject = "json:" + SourceId, DestinationObject = "destination-point", Enabled = true, CommandPriority = "16" });
            });
            var salt = RandomNumberGenerator.GetBytes(32);
            var users = new[] { new { id = "preserved-user", username = "synthetic-recovery", role = "Admin", enabled = true, salt = Convert.ToBase64String(salt), passwordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(LocalPassword, salt, 210000, HashAlgorithmName.SHA512, 32)), stamp = "preserved-stamp" } };
            File.WriteAllText(Path.Combine(directory, "users.json"), JsonSerializer.Serialize(users));
            File.WriteAllText(Path.Combine(directory, "websettings.json"), "{\"Web\":{\"LocalPort\":15194,\"HttpsPort\":15494,\"EnableRemote\":false}}");
            File.WriteAllText(Path.Combine(directory, "metasys-login.json"), "{\"Enabled\":true,\"AllowAnyAuthenticatedUser\":true,\"ServerOrigin\":\"https://synthetic-source.invalid\"}");
            using var journal = new RecoveryJournal(directory);
            var record = journal.Begin("preserved-mapping", RecoveryJournal.Binding(store, store.Config.Mappings[0], "source-point", "destination-point"), RecoveryJournal.ValueHash(System.Text.Json.Nodes.JsonValue.Create(20)!));
            record = journal.Transition("preserved-mapping", record, "submitted");
            journal.Transition("preserved-mapping", record, "confirmed");
            return;
        }
        Check(mode is "upgrade" or "restart" or "hold" or "pause" or "paused-restart", "unsupported-probe-mode");
        Check(store.Password() == ApiPassword, "existing-machine-DPAPI-password-did-not-survive-upgrade");
        Check(store.Config.TransfersEnabled == (mode != "paused-restart") && store.Config.Mappings[0].Id == "preserved-mapping" && store.Config.Mappings[0].CommandPriority == "16", "existing-settings-did-not-survive-upgrade");
        Check(store.ExternalUrl(store.Config.ExternalSources[SourceId]) == SourceUrl && !File.ReadAllText(Path.Combine(directory, "config.json")).Contains("synthetic-query-secret"), "legacy-query-migration-failed");
        Check(File.ReadAllText(Path.Combine(directory, "websettings.json")).Contains("15194") && File.ReadAllText(Path.Combine(directory, "metasys-login.json")).Contains("\"AllowAnyAuthenticatedUser\":true"), "ports-or-Metasys-access-changed");
        var usersStore = new UserStore(new(directory));
        Check(usersStore.Authenticate(new("synthetic-recovery", LocalPassword), "fixture")?.Id == "preserved-user", "legacy-recovery-login-failed");
        Check(usersStore.ValidPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "preserved-user"), new Claim(ClaimTypes.Role, "Admin"), new Claim("stamp", "preserved-stamp") }, "fixture"))), "existing-session-stamp-changed");
        using (var journal = new RecoveryJournal(directory)) Check(journal.Read("preserved-mapping", RecoveryJournal.Binding(store, store.Config.Mappings[0], "source-point", "destination-point"))?.Phase == "confirmed", "recovery-journal-binding-changed");
        if (mode == "pause") await store.Update(settings => settings.TransfersEnabled = false);
        using var bridge = new Bridge(store);
        Check(bridge.Armed == (mode is not ("pause" or "paused-restart")), "saved-start-or-pause-choice-not-restored-without-browser-login");
    }
    sealed class ProbeStore(IDataProtectionProvider provider, DataLocation location) : Store(provider, location)
    {
        public string Password() => Unprotect(Config.Source.Password);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
