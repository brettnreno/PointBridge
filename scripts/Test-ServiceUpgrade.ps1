param([string]$LogDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\service-upgrade'))
$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The isolated Windows SCM upgrade test requires an elevated Windows build worker.'}
$root=Split-Path $PSScriptRoot -Parent
$identifier=[guid]::NewGuid().ToString('N')
$name='PointBridgeSecurityFixture'+$identifier
$other=$name+'Other'
$directory=Join-Path $env:ProgramData ('PointBridgeSecurityFixture-'+$identifier)
$data=Join-Path $directory 'data'
$results=Join-Path $directory 'results'
$publish=Join-Path $directory 'bin'
New-Item -ItemType Directory -Path $directory,$data,$results,$publish,$LogDirectory -Force|Out-Null
function Set-FixtureAcl([string]$Path,[Security.AccessControl.FileSystemRights]$LocalServiceRights){
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-19'),$LocalServiceRights,'ContainerInherit,ObjectInherit','None','Allow'));Set-Acl -LiteralPath $Path -AclObject $acl
}
Set-FixtureAcl $directory ReadAndExecute
Set-FixtureAcl $data FullControl
Set-FixtureAcl $results Modify
$sc=Join-Path $env:SystemRoot 'System32\sc.exe'
function Run-Sc([string[]]$Arguments){& $sc @Arguments|Out-Null;if($LASTEXITCODE -ne 0){throw ('Isolated SCM command failed: '+$Arguments[0])}}
function Run-Probe([string]$Service,[string]$Mode,[switch]$KeepRunning){
    $result=Join-Path $results ($Mode+'.json')
    $command='"'+(Join-Path $publish 'ServiceFixture.exe')+'" '+$Service+' '+$Mode+' "'+$data+'" "'+$result+'"'
    Run-Sc @('config',$Service,'binPath=',$command)
    Run-Sc @('start',$Service)
    $until=[DateTime]::UtcNow.AddSeconds(60)
    while(!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 200}
    if(!(Test-Path -LiteralPath $result)){throw ('Isolated service probe timed out: '+$Mode)}
    if(!$KeepRunning){(Get-Service -Name $Service).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))}
    $outcome=Get-Content -LiteralPath $result -Raw|ConvertFrom-Json
    Copy-Item -LiteralPath $result -Destination (Join-Path $LogDirectory ($Mode+'.json'))
    if(!$outcome.passed -or $outcome.sessionId -ne 0){throw ('Isolated service probe failed: '+($outcome|ConvertTo-Json -Compress))}
    Write-Host ('PASS SCM '+$Mode+' probe: Session 0, LocalService, no browser or interactive credentials')
}
$created=@()
try{
    & dotnet publish (Join-Path $root 'fixtures\service\ServiceFixture.csproj') -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE -ne 0){throw 'Isolated service fixture publish failed'}
    foreach($service in @($name,$other)){Run-Sc @('create',$service,'binPath=',('"'+(Join-Path $publish 'ServiceFixture.exe')+'"'),'start=','auto','obj=','NT AUTHORITY\LocalService');$created+=,$service}
    Run-Probe $name legacy
    Run-Sc @('sidtype',$name,'unrestricted')
    Run-Probe $name upgrade
    Run-Probe $name restart
    Run-Probe $name hold -KeepRunning
    Run-Sc @('sidtype',$other,'unrestricted')
    Run-Probe $other attack
    (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    Run-Probe $name pause
    Run-Probe $name paused-restart
    foreach($service in @($name,$other)){if((Get-CimInstance Win32_Service -Filter ("Name='"+$service+"'")).StartMode -ne 'Auto'){throw 'Fixture is not configured for automatic Windows startup'}}
    Write-Host 'PASS legacy DPAPI/API credentials, recovery passwords/sessions, mappings, Start/Pause choice, journal bindings and ports survived the service SID upgrade. Another LocalService process could neither read config/keys/process memory nor rewrite data/process DACLs.'
}finally{
    foreach($service in $created){if($service -notlike 'PointBridgeSecurityFixture*'){throw 'Refusing to clean up a non-fixture service'};Stop-Service -Name $service -ErrorAction SilentlyContinue;& $sc delete $service|Out-Null}
}
