param([Parameter(Mandatory=$true)][string]$MsiPath)
$ErrorActionPreference='Stop'
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$null
try{
    $database=$installer.OpenDatabase((Resolve-Path -LiteralPath $MsiPath).Path,0)
    function Query-One([string]$Sql,[int]$Columns=1){$view=$database.OpenView($Sql);[void]$view.Execute();$record=$view.Fetch();if(!$record){throw 'Required MSI security metadata missing'};$values=@{First=$record.StringData(1);Second=if($Columns -gt 1){$record.StringData(2)}else{$null}};[void]$view.Close();return $values}
    $service=Query-One 'SELECT `StartType`,`StartName` FROM `ServiceInstall` WHERE `Name`=''MetasysPointBridge''' 2
    if([int]$service.First -ne 2 -or $service.Second -ne 'NT AUTHORITY\LocalService'){throw 'Automatic startup or existing Windows identity changed'}
    $component=Query-One 'SELECT `Attributes` FROM `Component` WHERE `Component`=''DataDirectory'''
    if(([int]$component.First -band 16) -eq 0){throw 'ProgramData component must remain permanent during upgrade/uninstall'}
    $upgrade=Query-One 'SELECT `Value` FROM `Property` WHERE `Property`=''UpgradeCode'''
    if($upgrade.First -ne '{2FD32BF3-90A5-4722-B5AB-12AC6109A81D}'){throw 'Existing MSI upgrade identity changed'}
    $sid=Query-One 'SELECT `Type`,`Target` FROM `CustomAction` WHERE `Action`=''ConfigureBridgeSid''' 2
    if([int]$sid.First -ne 3106 -or !$sid.Second.Contains('sidtype MetasysPointBridge unrestricted')){throw 'Missing elevated deferred service SID setup'}
    $sidSequence=[int](Query-One 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action`=''ConfigureBridgeSid''').First
    $installSequence=[int](Query-One 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action`=''InstallServices''').First
    $startSequence=[int](Query-One 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action`=''StartServices''').First
    if($sidSequence -le $installSequence -or $sidSequence -ge $startSequence){throw 'Service SID must be enabled before automatic service start'}
    $acl=(Query-One 'SELECT `SDDLText` FROM `MsiLockPermissionsEx` WHERE `MsiLockPermissionsEx`=''DataAcl''').First
    if(!$acl.Contains('S-1-5-80-') -or !$acl.Contains(';;;OW)') -or $acl.Contains(';;;LS)')){throw 'Data ACL is not isolated to the service SID with owner-rights protection'}
    $recovery=(Query-One 'SELECT `Target` FROM `CustomAction` WHERE `Action`=''ConfigureBridgeRecovery''').First
    if(!$recovery.Contains('restart/5000/restart/15000/restart/60000')){throw 'Service recovery configuration missing'}
    $remove=$database.OpenView('SELECT `DirProperty` FROM `RemoveFile`');[void]$remove.Execute()
    while($row=$remove.Fetch()){if($row.StringData(1) -eq 'DATADIR'){throw 'Installer must not remove retained application data'}};[void]$remove.Close()
    Write-Host 'PASS MSI security metadata: stable upgrade identity, permanent data, automatic LocalService startup, service SID before start, owner-rights ACL and recovery actions.'
}finally{
    if($database){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)}
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
