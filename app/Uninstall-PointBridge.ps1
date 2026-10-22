[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='High')]
param([switch]$RemoveData,[switch]$PlanOnly)
$ErrorActionPreference='Stop'
$serviceName='MetasysPointBridge'
$installRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Metasys Point Bridge')).TrimEnd('\')
$dataRoot=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MetasysPointBridge')).TrimEnd('\')
function Assert-SafeTree([string]$Path,[string]$Expected) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if(![string]::Equals($full,[IO.Path]::GetFullPath($Expected).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target does not match the fixed Point Bridge directory.'}
    $ancestor=$full
    while($ancestor){if(Test-Path -LiteralPath $ancestor){if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup refused: a target or ancestor is a junction/symbolic link.'}};$ancestor=[IO.Path]::GetDirectoryName($ancestor)}
    if(!(Test-Path -LiteralPath $full)){return}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($full)
    while($pending.Count){foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup refused: directory contains a junction/symbolic link.'}
        if($item.PSIsContainer){$pending.Push($item.FullName)}
    }}
}
function Product-Code([string]$Version){$hash=[Security.Cryptography.SHA256]::Create();try{$bytes=$hash.ComputeHash([Text.Encoding]::UTF8.GetBytes('MetasysPointBridge|'+('product|'+$Version).ToLowerInvariant()))[0..15]}finally{$hash.Dispose()};return '{'+([Guid]::new([byte[]]$bytes)).ToString().ToUpperInvariant()+'}'}
$plan=[pscustomobject]@{Service=$serviceName;InstallationDirectory=$installRoot;DataDirectory=$dataRoot;RemoveData=[bool]$RemoveData;FirewallRule='MetasysPointBridge-HTTPS';SharedCertificatesRemoved=$false;SharedDotNetRemoved=$false;WindowsEventHistoryCleared=$false}
if($PlanOnly){$plan;return}
if(![Environment]::Is64BitProcess){throw 'Run the 64-bit Windows PowerShell or PowerShell7 console.'}
if(!$PSCmdlet.ShouldProcess(($installRoot+$(if($RemoveData){'; '+$dataRoot}else{'; data retained'})),'Stop/remove Point Bridge service and installation; remove owned firewall rule; optionally permanently erase Point Bridge data')){return}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run the uninstall helper in an elevated PowerShell window.'}
Assert-SafeTree $installRoot (Join-Path $env:ProgramFiles 'Metasys Point Bridge')
if($RemoveData){Assert-SafeTree $dataRoot (Join-Path $env:ProgramData 'MetasysPointBridge')}
$installedExe=Join-Path $installRoot 'apiTool.exe'
if(Test-Path -LiteralPath $installedExe){
    $installedVersion=((Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion -split '[-+]')[0]
    if($installedVersion -notin @('1.2.0','1.1.1','1.1.0','1.0.0')){throw 'A different application version occupies the target directory. Use that release uninstall helper; nothing was changed.'}
}
$service=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if($service){
    $image=(Get-ItemProperty -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name ImagePath).ImagePath
    $image=[Environment]::ExpandEnvironmentVariables($image)
    $binary=if($image.StartsWith('"')){($image -split '"')[1]}else{($image -split '\s+')[0]}
    if(![string]::Equals([IO.Path]::GetFullPath($binary),(Join-Path $installRoot 'apiTool.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'Service executable is outside the expected installation. No service or files were changed.'}
    Stop-Service -Name $serviceName -ErrorAction Stop
    (Get-Service -Name $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
}
foreach($version in @('1.2.0','1.1.1','1.1.0','1.0.0')){
    $code=Product-Code $version
    $key='Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\'+$code
    if(Test-Path -LiteralPath $key){
        $product=Get-ItemProperty -LiteralPath $key
        if($product.DisplayName -ne 'Metasys Point Bridge' -or $product.Publisher -ne 'Point Bridge'){throw 'MSI identity mismatch. Cleanup stopped.'}
        $log=Join-Path ([IO.Path]::GetTempPath()) ('PointBridge-uninstall-'+[Guid]::NewGuid().ToString('N')+'.log')
        $process=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList @('/x',$code,'/passive','/norestart','/L*v',('"'+$log+'"')) -Wait -PassThru -WindowStyle Hidden
        if($process.ExitCode -notin @(0,1605,3010)){throw "Windows Installer removal failed (exit $($process.ExitCode)). See $log. Data was not deleted."}
        if($process.ExitCode -eq 3010){throw "Windows requires a restart to finish removal. Restart, then rerun this helper to finish cleanup. Data was not deleted. Log: $log"}
    }
}
# Also supports the supplied manual Install-Service.ps1 when installed at the standard path.
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){
    & (Join-Path $env:SystemRoot 'System32\sc.exe') delete $serviceName | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Service deletion failed; files and data were not deleted.'}
    if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'Service deletion is pending. Close Services consoles/restart, then rerun. Data was not deleted.'}
}
$rule=Get-NetFirewallRule -Name 'MetasysPointBridge-HTTPS' -ErrorAction SilentlyContinue
if($rule){$rule|Remove-NetFirewallRule -ErrorAction Stop}
if([Diagnostics.EventLog]::SourceExists('MetasysPointBridge')){[Diagnostics.EventLog]::DeleteEventSource('MetasysPointBridge')}
Assert-SafeTree $installRoot (Join-Path $env:ProgramFiles 'Metasys Point Bridge')
if(Test-Path -LiteralPath $installRoot){Remove-Item -LiteralPath $installRoot -Recurse -Force}
if($RemoveData){Assert-SafeTree $dataRoot (Join-Path $env:ProgramData 'MetasysPointBridge');if(Test-Path -LiteralPath $dataRoot){Remove-Item -LiteralPath $dataRoot -Recurse -Force}}
Write-Host 'Point Bridge service, installed files and its named firewall rule have been removed.'
Write-Host $(if($RemoveData){'Point Bridge default data directory, saved credentials, accounts, keys, mappings, journal and logs were erased.'}else{'Point Bridge configuration and credentials were retained. Use -RemoveData for complete application-data removal.'})
Write-Host 'Shared certificates, certificate permissions, .NET installations, Metasys points/trends and Windows event history were not changed.'
