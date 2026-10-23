$ErrorActionPreference='Stop'
$thumbprint='0000000000000000000000000000000000000000'
if(Test-Path -LiteralPath ('Cert:\LocalMachine\My\'+$thumbprint)){throw 'Refusing to use an existing certificate in a guard test'}
$helper=Join-Path (Split-Path $PSScriptRoot -Parent) 'app\Configure-Https.ps1'
foreach($address in @('Any','0.0.0.0/0','::/0','10.1.2.3/33','not-an-address')){
    $rejected=$false
    try{& $helper -PublicHost 'fixture.invalid' -CertificateThumbprint $thumbprint -OpenFirewall -AllowedRemoteAddress $address}
    catch{if($_.Exception.Message -match 'explicit management|restricted CIDR|Unspecified firewall'){$rejected=$true}else{throw}}
    if(!$rejected){throw 'Unsafe firewall scope was not rejected before any certificate/service changes'}
}
try{& $helper -PublicHost 'fixture.invalid' -CertificateThumbprint $thumbprint -OpenFirewall;throw 'Missing firewall scope accepted'}
catch{if($_.Exception.Message -notmatch 'Specify -AllowedRemoteAddress'){throw}}
foreach($address in @('10.1.2.3','10.1.2.0/24','2001:db8::/64')){
    try{& $helper -PublicHost 'fixture.invalid' -CertificateThumbprint $thumbprint -OpenFirewall -AllowedRemoteAddress $address;throw 'Missing certificate was unexpectedly accepted'}
    catch{if($_.FullyQualifiedErrorId -notmatch 'PathNotFound'){throw}}
}
Write-Host 'PASS HTTPS guard: explicit management addresses required; catch-all/invalid scopes rejected before changes. Valid scopes reach only a deliberately missing certificate; no firewall, certificate or production-service changes performed.'
