param(
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'MetasysPointBridge-runtime'),
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$arguments = @('publish', (Join-Path $PSScriptRoot 'apiTool.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $Destination, '--self-contained', (!$FrameworkDependent.IsPresent).ToString().ToLowerInvariant())
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($file in @('OpenConsole.url','Configure-Https.ps1','Reset-Accounts.ps1','Install-Service.ps1','Uninstall-PointBridge.ps1','README.md','API-NOTES.md','RELEASE-NOTES.md','METASYS-LOGIN.md','ON-SITE-ACCEPTANCE.md','metasys-login.disabled.example.json','EXTERNAL-SOURCES.md','RECOVERY-AND-DIAGNOSTICS.md','WATCHDOG-OFFLINE.md','health-frame.schema.json','health-publishing.disabled.example.json','weather-source.disabled.example.json','INSTALL-AND-REMOVE.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $Destination }
Write-Host "Published to $Destination"
Write-Host 'The build is unsigned. Use your organization code-signing process before distribution.'
