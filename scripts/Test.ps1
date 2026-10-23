[CmdletBinding()]
param([string]$LogDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\logs'),[switch]$NoRestore)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $LogDirectory -Force|Out-Null
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$node=(Get-Command node -ErrorAction Stop).Source
function Run-Dotnet([string[]]$Arguments,[string]$Log){& $dotnet @Arguments 2>&1|Tee-Object -FilePath (Join-Path $LogDirectory $Log);if($LASTEXITCODE -ne 0){throw "dotnet failed: $Log"}}
Push-Location $root
try{
    if(!$NoRestore){foreach($project in @('validation/IntegrationTests.csproj','fixtures/AdapterFixtures.csproj','fixtures/http/EnabledAuthFixtures.csproj')){Run-Dotnet @('restore',$project) ('restore-'+([IO.Path]::GetFileNameWithoutExtension($project))+'.txt')}}
    Run-Dotnet @('run','--project','validation/IntegrationTests.csproj','--no-restore') 'backend.txt'
    Run-Dotnet @('run','--project','fixtures/AdapterFixtures.csproj','--no-restore') 'authentication-adapter.txt'
    Run-Dotnet @('build','fixtures/http/EnabledAuthFixtures.csproj','--no-restore') 'http-build.txt'
    foreach($suite in @('strict','extensions','setup')){
        $data=Join-Path ([IO.Path]::GetTempPath()) ('pointbridge-ci-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $data|Out-Null
        @{Web=@{EnableRemote=$false;LocalPort=15089};Acceptance=@{LoginOnly=$true;AllowConnectionTests=$true;AllowExtendedConfiguration=($suite -eq 'extensions')};MetasysLogin=@{Enabled=($suite -ne 'setup');ServerOrigin='https://fixture.invalid';Grants=@(@{Username='fixture-user';StorageId='101';ProfileId='fixture-id';ServerOrigin='https://fixture.invalid';Role='Admin'})}}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $data 'websettings.json')
        $oldData=$env:METASYS_BRIDGE_DATA;$oldTestData=$env:BRIDGE_TEST_DATA;$process=$null
        try{
            $env:METASYS_BRIDGE_DATA=$data;$env:BRIDGE_TEST_DATA=$data
            $dll=Join-Path $root 'fixtures\http\bin\Debug\net10.0\EnabledAuthFixtures.dll'
            $process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$dll+'"'),'--contentRoot',('"'+(Join-Path $root 'app')+'"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $LogDirectory ($suite+'-server.txt')) -RedirectStandardError (Join-Path $LogDirectory ($suite+'-server-errors.txt'))
            $ready=$false;$until=[DateTime]::UtcNow.AddSeconds(30)
            while([DateTime]::UtcNow -lt $until){if($process.HasExited){throw 'Fake HTTP test server exited before startup.'};try{$response=Invoke-WebRequest -Uri 'http://127.0.0.1:15089/api/session' -TimeoutSec 2;if($response.StatusCode -eq 200){$ready=$true;break}}catch{};Start-Sleep -Milliseconds 200}
            if(!$ready){throw 'Fake HTTP server did not start within30 seconds; check port15089.'}
            $scripts=if($suite -eq 'strict'){@('enabled-provider-http.mjs')}elseif($suite -eq 'setup'){@('metasys-setup-http.mjs')}else{@('extensions-http.mjs','recovery-http.mjs','explorer-http.mjs','api-sessions-http.mjs','security-http.mjs')}
            foreach($script in $scripts){& $node (Join-Path $root ('validation\'+$script)) 2>&1|Tee-Object -FilePath (Join-Path $LogDirectory ($script+'.txt'));if($LASTEXITCODE -ne 0){throw "HTTP fixture failed: $script"}}
        }finally{
            if($process -and !$process.HasExited){$process.Kill($true);$process.WaitForExit(10000)|Out-Null}
            $env:METASYS_BRIDGE_DATA=$oldData;$env:BRIDGE_TEST_DATA=$oldTestData
            if(([string](Get-Content -LiteralPath (Join-Path $LogDirectory ($suite+'-server.txt')) -Raw -ErrorAction SilentlyContinue)).Contains('fixture-unexpected-secret-canary')){throw 'Unexpected exception canary leaked into server logs.'}
        }
    }
    & $node (Join-Path $root 'validation/ui-theme.mjs');if($LASTEXITCODE -ne 0){throw 'Theme tests failed.'}
    foreach($file in @('app/wwwroot/app.js','app/wwwroot/extensions.js','app/wwwroot/metasys-setup.js','app/wwwroot/explorer.js','app/wwwroot/theme.js')){& $node --check $file;if($LASTEXITCODE -ne 0){throw 'JavaScript syntax check failed.'}}
    & (Join-Path $PSScriptRoot 'Test-HttpsGuard.ps1')
    & (Join-Path $PSScriptRoot 'Test-ServiceUpgrade.ps1') -LogDirectory (Join-Path $LogDirectory 'service-upgrade')
}finally{Pop-Location}
Write-Host 'All tests passed. Only in-memory transports/local fake endpoints were used. Temporary synthetic data stays outside artifacts.'
