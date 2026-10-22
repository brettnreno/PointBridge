param([switch]$ConfirmReset)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run account recovery in an elevated PowerShell window.' }
if (-not $ConfirmReset) { throw 'This resets application accounts while preserving point mappings and Metasys credentials. Run with -ConfirmReset to proceed.' }
$data = Join-Path $env:ProgramData 'MetasysPointBridge'
$usersFile = Join-Path $data 'users.json'
Stop-Service -Name MetasysPointBridge
try {
    if (Test-Path -LiteralPath $usersFile) { Move-Item -LiteralPath $usersFile -Destination ($usersFile + '.recovery-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.bak') }
    $setupFile = Join-Path $data 'setup-code.txt'
    # Replacing the consumed marker causes the service to issue a new random one-time code.
    Set-Content -LiteralPath $setupFile -Value 'Issue a new code.'
} finally { Start-Service -Name MetasysPointBridge }
Write-Host "Account recovery started. Open the console and read the new code from $data\setup-code.txt as an administrator."
