param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$ServiceName = 'MetasysPointBridge'
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installation script in an elevated PowerShell window.'
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetExtension($resolvedExecutable) -ne '.exe') { throw 'Select the published apiTool.exe.' }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "Service $ServiceName already exists. Stop and update the existing installation instead." }
if (-not [System.Diagnostics.EventLog]::SourceExists('MetasysPointBridge')) {
    [System.Diagnostics.EventLog]::CreateEventSource('MetasysPointBridge', 'Application')
}
New-Service -Name $ServiceName -DisplayName 'Metasys Point Bridge' -BinaryPathName ('"' + $resolvedExecutable + '"') -StartupType Automatic -Description 'Synchronizes configured Metasys points through the REST API; local administration at http://127.0.0.1:5088.'
& sc.exe config $ServiceName obj= "NT SERVICE\$ServiceName"
if ($LASTEXITCODE -ne 0) { throw 'Service installed, but virtual service account configuration failed. Do not start until its identity is configured.' }
$dataDirectory = Join-Path $env:ProgramData 'MetasysPointBridge'
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$acl = Get-Acl -LiteralPath $dataDirectory
$acl.SetAccessRuleProtection($true, $false)
foreach ($existingRule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($existingRule) }
foreach ($account in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', "NT SERVICE\$ServiceName")) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($account, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $dataDirectory -AclObject $acl
& sc.exe failure $ServiceName reset= 86400 actions= restart/15000/restart/30000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Service installed, but recovery configuration failed.' }
Write-Host 'Service installed under its virtual account, with a restricted data directory. It has not been started.'
Write-Host "Start-Service -Name '$ServiceName'"
