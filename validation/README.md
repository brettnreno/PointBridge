# Test harnesses

Run PowerShell7 from the repository root:

```powershell
./scripts/Test.ps1
```

Requires Windows x64, .NET10 SDK and Node.js. The script restores test projects, runs the real-code backend and authentication adapter harnesses, builds the injected HTTP fixture application, and runs strict-provider plus extension/export HTTP suites sequentially on loopback port15089. It stops only its own fixture process. Temporary synthetic credentials/data are outside uploaded artifacts. No live BAS, paid API, real credential or customer network is used.

Standalone commands:

```powershell
dotnet run --project validation/IntegrationTests.csproj
dotnet run --project fixtures/AdapterFixtures.csproj
```

HTTP .mjs files expect a configured fake server and synthetic data; use Test.ps1 rather than pointing them at a real application. Backend tests cover60 scenario groups; authentication-adapter tests74 assertions. Prior HTTP totals were83 strict-provider,52 extension and20 export assertions. Read the current run logs for actual totals; fresh fixture bootstrap can change assertion labels. Simulations do not certify target-server installation, TLS/service identity, point priority or notification delivery.
