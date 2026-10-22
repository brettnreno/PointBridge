param(
    [Parameter(Mandatory = $true)][string]$PublicHost,
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [string]$InstallationDirectory = (Join-Path $env:ProgramFiles 'Metasys Point Bridge'),
    [ValidateRange(1024,65535)][int]$Port = 5443,
    [switch]$OpenFirewall
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this configuration script in an elevated PowerShell window.' }
if ([Uri]::CheckHostName($PublicHost) -eq [UriHostNameType]::Unknown -or $PublicHost.Contains('*')) { throw 'Enter the server DNS name covered by the certificate, without a scheme or port.' }
$thumbprint = $CertificateThumbprint.Replace(' ', '')
$certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'Select a valid server certificate with its private key in LocalMachine\My.' }
if ($certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') { throw 'This helper configures RSA key permissions. For an ECDSA certificate, grant the service account private-key read access in Certificates MMC and configure Web settings manually.' }
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
try {
    if ($rsa -is [Security.Cryptography.RSACng]) { $keyPath = Join-Path $env:ProgramData ("Microsoft\Crypto\Keys\" + $rsa.Key.UniqueName) }
    elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $keyPath = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $rsa.CspKeyContainerInfo.UniqueKeyContainerName) }
    else { throw 'Private key provider is unsupported by this helper. Grant key read permission using Certificates MMC.' }
    $keyAcl = Get-Acl -LiteralPath $keyPath
    $serviceIdentity = (Get-CimInstance Win32_Service -Filter "Name='MetasysPointBridge'").StartName
    if ([string]::IsNullOrWhiteSpace($serviceIdentity)) { throw 'Install the MetasysPointBridge service first.' }
    $keyAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceIdentity,'Read','Allow'))
    Set-Acl -LiteralPath $keyPath -AclObject $keyAcl
} finally { $rsa.Dispose() }
$settingsPath = Join-Path (Join-Path $env:ProgramData 'MetasysPointBridge') 'websettings.json'
$baseSettingsPath = Join-Path $InstallationDirectory 'appsettings.json'
$settings = Get-Content -Raw -LiteralPath $(if (Test-Path -LiteralPath $settingsPath) { $settingsPath } else { $baseSettingsPath }) | ConvertFrom-Json
$settings.Web.EnableRemote = $true
$settings.Web.HttpsPort = $Port
$settings.Web.CertificateThumbprint = $thumbprint
$settings.AllowedHosts = '127.0.0.1;localhost;' + $PublicHost
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath ($settingsPath + '.tmp') -Encoding UTF8
Move-Item -LiteralPath ($settingsPath + '.tmp') -Destination $settingsPath -Force
if ($OpenFirewall) {
    $ruleName = 'MetasysPointBridge-HTTPS'
    $existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existing) { $existing | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $Port }
    else { New-NetFirewallRule -Name $ruleName -DisplayName 'Metasys Point Bridge HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Domain,Private | Out-Null }
}
Restart-Service -Name MetasysPointBridge
Write-Host "HTTPS configured at https://${PublicHost}:$Port/ . Verify certificate trust and DNS from a coworker's browser."
