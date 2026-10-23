[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'),[switch]$SkipTests,[switch]$NoRestore)
$ErrorActionPreference='Stop'
if(![Environment]::Is64BitProcess -or ![OperatingSystem]::IsWindows()){throw 'Use PowerShell7 on64-bit Windows to build the MSI.'}
$root=Split-Path $PSScriptRoot -Parent
$logs=Join-Path $OutputDirectory 'logs';$packages=Join-Path $OutputDirectory 'packages';$runtime=Join-Path $OutputDirectory 'runtime'
New-Item -ItemType Directory -Path $logs,$packages -Force|Out-Null
$project=Join-Path $root 'app\apiTool.csproj';$version=([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$version=[string]($version|Where-Object {$_}|Select-Object -First 1)
if(!$SkipTests){& (Join-Path $PSScriptRoot 'Test.ps1') -LogDirectory $logs -NoRestore:$NoRestore}
$publishArguments=@('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:DebugSymbols=false','-o',$runtime)
if($NoRestore){$publishArguments+='--no-restore'}
& dotnet @publishArguments 2>&1|Tee-Object -FilePath (Join-Path $logs 'publish.txt');if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
foreach($file in @('OpenConsole.url','Configure-Https.ps1','Reset-Accounts.ps1','Install-Service.ps1','Uninstall-PointBridge.ps1','README.md','API-NOTES.md','RELEASE-NOTES.md','METASYS-LOGIN.md','ON-SITE-ACCEPTANCE.md','metasys-login.disabled.example.json','EXTERNAL-SOURCES.md','RECOVERY-AND-DIAGNOSTICS.md','WATCHDOG-OFFLINE.md','health-frame.schema.json','health-publishing.disabled.example.json','weather-source.disabled.example.json','INSTALL-AND-REMOVE.md')){Copy-Item -LiteralPath (Join-Path (Join-Path $root 'app') $file) -Destination $runtime}
Copy-Item -LiteralPath (Join-Path $root 'app\METASYS-EXPLORER.md') -Destination $runtime
Copy-Item -LiteralPath (Join-Path $root 'app\SECURITY-UPGRADE.txt') -Destination $runtime
$msi=Join-Path $packages ('MetasysPointBridge-'+$version+'-x64.msi')
& (Join-Path $root 'app\Build-Msi.ps1') -PublishDirectory $runtime -OutputPath $msi -Version $version|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $logs 'msi.json')
& (Join-Path $root 'scripts\Test-InstallerSecurity.ps1') -MsiPath $msi
Copy-Item -LiteralPath (Join-Path $root 'app\Uninstall-PointBridge.ps1'),(Join-Path $root 'app\INSTALL-AND-REMOVE.md') -Destination $packages
$items=@(Get-ChildItem -LiteralPath $packages -File|Where-Object {$_.Extension -ne '.zip' -and $_.Name -ne 'SHA256SUMS.txt'})
$items|ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash+'  '+$_.Name}|Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt')
$zip=Join-Path $packages ('MetasysPointBridge-'+$version+'-bundle.zip')
Compress-Archive -LiteralPath @((Get-ChildItem -LiteralPath $packages -File|Where-Object {$_.Extension -ne '.zip'}).FullName) -DestinationPath $zip -Force
Write-Host "Unsigned self-contained MSI/bundle: $packages. The real product was not installed or deployed; tests used isolated synthetic services only."
