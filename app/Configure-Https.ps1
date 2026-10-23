param(
    [Parameter(Mandatory = $true)][string]$PublicHost,
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [string]$InstallationDirectory = (Join-Path $env:ProgramFiles 'Metasys Point Bridge'),
    [ValidateRange(1024,65535)][int]$Port = 5443,
    [switch]$OpenFirewall,
    [string[]]$AllowedRemoteAddress
)
$ErrorActionPreference = 'Stop'
if ($OpenFirewall) {
    if (!$AllowedRemoteAddress) { throw 'Specify -AllowedRemoteAddress with management IP addresses or VPN subnets before opening the firewall.' }
    foreach ($address in $AllowedRemoteAddress) {
        $parts = $address.Split('/')
        $ip = $null
        if ($parts.Count -gt 2 -or ![Net.IPAddress]::TryParse($parts[0],[ref]$ip)) { throw 'Use explicit management IP addresses or CIDR subnets; Any and named scopes are not accepted.' }
        $maximum = if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
        $prefix = 0
        if ($parts.Count -eq 2 -and (![int]::TryParse($parts[1],[ref]$prefix) -or $prefix -lt 1 -or $prefix -gt $maximum)) { throw 'Use a restricted CIDR prefix, not a catch-all subnet.' }
        if ($ip.Equals([Net.IPAddress]::Any) -or $ip.Equals([Net.IPAddress]::IPv6Any)) { throw 'Unspecified firewall addresses are not accepted.' }
    }
}
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
    $service = Get-CimInstance Win32_Service -Filter "Name='MetasysPointBridge'"
    if (!$service) { throw 'Install the MetasysPointBridge service first.' }
    & (Join-Path $env:SystemRoot 'System32\sc.exe') sidtype MetasysPointBridge unrestricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the service-specific identity.' }
    $serviceIdentity = ([Security.Principal.NTAccount]::new('NT SERVICE','MetasysPointBridge')).Translate([Security.Principal.SecurityIdentifier])
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
    if ($existing) { $existing | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $Port; $existing | Set-NetFirewallRule -Profile Domain,Private -RemoteAddress $AllowedRemoteAddress }
    else { New-NetFirewallRule -Name $ruleName -DisplayName 'Metasys Point Bridge HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Domain,Private -RemoteAddress $AllowedRemoteAddress | Out-Null }
}
Restart-Service -Name MetasysPointBridge
Write-Host "HTTPS configured at https://${PublicHost}:$Port/ . Verify certificate trust and DNS from a coworker's browser."
