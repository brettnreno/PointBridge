[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'MetasysPointBridge-runtime'),
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'MetasysPointBridge-1.2.0-x64.msi'),
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.2.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Builds an MSI database using Windows Installer's native authoring API.
# No installer is executed and no downloaded build tools are used.
# References: learn.microsoft.com/windows/win32/msi/{serviceinstall-table,
# msilockpermissionsex-table,msiserviceconfigfailureactions-table,cabinet-files}.
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Build on 64-bit Windows.' }
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $publish 'apiTool.exe'))) { throw 'PublishDirectory must contain the self-contained apiTool.exe runtime.' }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'OpenConsole.url'))) { throw 'PublishDirectory must include OpenConsole.url.' }
if(-not $PSBoundParameters.ContainsKey('OutputPath')) { $OutputPath=Join-Path (Split-Path $PSScriptRoot -Parent) ('MetasysPointBridge-'+$Version+'-x64.msi') }
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pointbridge-msi-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$upgradeCode = '{2FD32BF3-90A5-4722-B5AB-12AC6109A81D}'
function Stable-Guid([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes('MetasysPointBridge|' + $Text.ToLowerInvariant()))[0..15] }
    finally { $hash.Dispose() }
    return '{' + ([Guid]::new([byte[]]$bytes)).ToString().ToUpperInvariant() + '}'
}
function Import-Table([string]$Name, [string[]]$Columns, [string[]]$Types, [string[]]$Keys, [object[]]$Rows) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add(($Columns -join "`t")); $lines.Add(($Types -join "`t")); $lines.Add((@($Name) + $Keys -join "`t"))
    foreach ($row in $Rows) {
        if ($row.Count -ne $Columns.Count) { throw "Invalid column count in $Name" }
        $values = foreach ($value in $row) {
            if ($null -eq $value) { '' } else {
                $text = [string]$value
                if ($text -match "[`t`r`n]") { throw "Invalid control character in $Name" }
                $text
            }
        }
        $lines.Add(($values -join "`t"))
    }
    $file = Join-Path $scratch ($Name + '.idt')
    [IO.File]::WriteAllLines($file, $lines, [Text.Encoding]::GetEncoding(1252))
    [void]$database.Import($scratch, ($Name + '.idt'))
}
function Query-Rows([string]$Sql) {
    $view = $database.OpenView($Sql); [void]$view.Execute()
    try { while ($null -ne ($record = $view.Fetch())) {
        $values = for ($i=1; $i -le $record.FieldCount(); $i++) { ([string]$record.StringData($i)) }
        ,$values
    } } finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
try {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
    $database = $installer.OpenDatabase($output, 3)
    $directories = [Collections.Generic.List[object]]::new()
    $directories.Add(@('TARGETDIR',$null,'SourceDir'))
    $directories.Add(@('ProgramFiles64Folder','TARGETDIR','.'))
    $directories.Add(@('System64Folder','TARGETDIR','.'))
    $directories.Add(@('INSTALLDIR','ProgramFiles64Folder','MPBRDG|Metasys Point Bridge'))
    $directories.Add(@('CommonAppDataFolder','TARGETDIR','.'))
    $directories.Add(@('DATADIR','CommonAppDataFolder','MPDATA|MetasysPointBridge'))
    $directories.Add(@('ProgramMenuFolder','TARGETDIR','.'))
    $directories.Add(@('MENUDIR','ProgramMenuFolder','MPBRDG|Metasys Point Bridge'))
    $directoryIds = @{ ''='INSTALLDIR' }
    $components = [Collections.Generic.List[object]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $featureComponents = [Collections.Generic.List[object]]::new()
    $manifest = [Collections.Generic.List[object]]::new()
    $ddf = [Collections.Generic.List[string]]::new()
    foreach ($line in @('.OPTION EXPLICIT','.Set Cabinet=ON','.Set Compress=ON','.Set CompressionType=LZX','.Set CompressionMemory=21','.Set CabinetNameTemplate=payload.cab',('.Set DiskDirectoryTemplate="'+$scratch+'"'),('.Set InfFileName="'+(Join-Path $scratch 'setup.inf')+'"'),('.Set RptFileName="'+(Join-Path $scratch 'setup.rpt')+'"'),'.Set MaxDiskSize=0','.Set MaxCabinetSize=0','.Set FolderSizeThreshold=0')) { $ddf.Add($line) }
    $sequence = 0; $serviceComponent='';$consoleComponent='';$consoleFile=''
    foreach ($file in (Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($publish.Length+1)
        if ($relative -match '(^|\\)(data|logs|keys|obj|bin|work)(\\|$)' -or $file.Extension -in @('.pdb','.msi','.cab')) { continue }
        if ($file.Name -in @('setup-code.txt','users.json','config.json','metasys-login.json','metasys-setup-complete.txt','websettings.json','activity.jsonl','audit.jsonl')) { throw "Refusing to package mutable application data: $relative" }
        $parentRelative = [IO.Path]::GetDirectoryName($relative)
        if (-not $parentRelative) { $parentRelative = '' }
        if (-not $directoryIds.ContainsKey($parentRelative)) {
            $current=''
            foreach ($segment in $parentRelative.Split('\')) {
                $old=$current; $current=if($current){$current+'\'+$segment}else{$segment}
                if (-not $directoryIds.ContainsKey($current)) {
                    $id='D'+(Stable-Guid $current).Replace('{','').Replace('}','').Replace('-','')
                    $short='D'+$directoryIds.Count.ToString('000000')
                    $directories.Add(@($id,$directoryIds[$old],($short+'|'+$segment)))
                    $directoryIds[$current]=$id
                }
            }
        }
        $sequence++; $fileId='F'+$sequence.ToString('00000');$component='C'+$sequence.ToString('00000')
        $shortName='F'+$sequence.ToString('000000')
        if ($file.Extension -and $file.Extension.Length -le 4) { $shortName+=$file.Extension.ToUpperInvariant() }
        # ProgramFiles contains immutable application defaults; mutable settings live in ProgramData.
        $components.Add(@($component,(Stable-Guid ('component|'+$relative)),$directoryIds[$parentRelative],256,$null,$fileId))
        $files.Add(@($fileId,$component,($shortName+'|'+$file.Name),[string]$file.Length,$null,$null,16896,$sequence))
        $featureComponents.Add(@('MainFeature',$component))
        $ddf.Add(('"'+$file.FullName+'" '+$fileId))
        $manifest.Add([pscustomobject]@{Id=$fileId;Component=$component;Path=$relative;Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})
        if ($relative -eq 'apiTool.exe') { $serviceComponent=$component }
        if ($relative -eq 'OpenConsole.url') { $consoleComponent=$component;$consoleFile=$fileId }
    }
    if (-not $serviceComponent -or -not $consoleComponent) { throw 'Required service/console files missing from package.' }
    # A permanent directory component preserves all ProgramData settings, users and DPAPI keys.
    $components.Add(@('DataDirectory',(Stable-Guid 'component|permanentdata'),'DATADIR',272,$null,$null))
    $featureComponents.Add(@('MainFeature','DataDirectory'))
    Import-Table 'Property' @('Property','Value') @('s72','l0') @('Property') @(
        @('ProductCode',(Stable-Guid ('product|'+$Version))),@('ProductName','Metasys Point Bridge'),@('ProductVersion',$Version),
        @('ProductLanguage','1033'),@('Manufacturer','Point Bridge'),@('UpgradeCode',$upgradeCode),@('ALLUSERS','1'),
        @('ARPNOMODIFY','1'),@('ARPCOMMENTS','One-way Metasys COV synchronization service and local management console.'),
        @('SecureCustomProperties','OLDPRODUCTS;NEWERPRODUCTFOUND'),@('LIMITUI','1')
    )
    Import-Table 'Directory' @('Directory','Directory_Parent','DefaultDir') @('s72','S72','l255') @('Directory') $directories.ToArray()
    Import-Table 'Component' @('Component','ComponentId','Directory_','Attributes','Condition','KeyPath') @('s72','S38','s72','i2','S255','S72') @('Component') $components.ToArray()
    Import-Table 'Feature' @('Feature','Feature_Parent','Title','Description','Display','Level','Directory_','Attributes') @('s38','S38','L64','L255','I2','i2','S72','i2') @('Feature') @(,@('MainFeature',$null,'Point Bridge','Service, local console and protected configuration directory.',1,1,'INSTALLDIR',0))
    Import-Table 'FeatureComponents' @('Feature_','Component_') @('s38','s72') @('Feature_','Component_') $featureComponents.ToArray()
    Import-Table 'File' @('File','Component_','FileName','FileSize','Version','Language','Attributes','Sequence') @('s72','s72','l255','i4','S72','S20','I2','i4') @('File') $files.ToArray()
    Import-Table 'Media' @('DiskId','LastSequence','DiskPrompt','Cabinet','VolumeLabel','Source') @('i2','i4','L64','S255','S32','S72') @('DiskId') @(,@(1,$sequence,$null,'#payload.cab',$null,$null))
    Import-Table 'CreateFolder' @('Directory_','Component_') @('s72','s72') @('Directory_','Component_') @(,@('DATADIR','DataDirectory'))
    Import-Table 'MsiLockPermissionsEx' @('MsiLockPermissionsEx','LockObject','Table','SDDLText','Condition') @('s72','s72','s32','l0','S255') @('MsiLockPermissionsEx') @(,@('DataAcl','DATADIR','CreateFolder','D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;LS)','NOT REMOVE'))
    Import-Table 'ServiceInstall' @('ServiceInstall','Name','DisplayName','ServiceType','StartType','ErrorControl','LoadOrderGroup','Dependencies','StartName','Password','Arguments','Component_','Description') @('s72','l255','L255','i4','i4','i4','S255','S255','S255','S255','S255','s72','L255') @('ServiceInstall') @(,@('BridgeService','MetasysPointBridge','Metasys Point Bridge',16,2,32769,$null,$null,'NT AUTHORITY\LocalService',$null,$null,$serviceComponent,'Mirrors configured Metasys point values using change-of-value notifications.'))
    Import-Table 'ServiceControl' @('ServiceControl','Name','Event','Arguments','Wait','Component_') @('s72','l255','i2','L255','I2','s72') @('ServiceControl') @(,@('BridgeServiceControl','MetasysPointBridge',163,$null,1,$serviceComponent))
    Import-Table 'CustomAction' @('Action','Type','Source','Target') @('s72','i2','S72','S255') @('Action') @(,@('ConfigureBridgeRecovery',3106,'System64Folder','"[System64Folder]sc.exe" failure MetasysPointBridge reset= 86400 actions= restart/5000/restart/15000/restart/60000'))
    Import-Table 'Registry' @('Registry','Root','Key','Name','Value','Component_') @('s72','i2','l255','L255','L0','s72') @('Registry') @(
        @('EventMessageFile',2,'SYSTEM\CurrentControlSet\Services\EventLog\Application\MetasysPointBridge','EventMessageFile','[INSTALLDIR]System.Diagnostics.EventLog.Messages.dll',$serviceComponent),
        @('EventTypes',2,'SYSTEM\CurrentControlSet\Services\EventLog\Application\MetasysPointBridge','TypesSupported','#7',$serviceComponent)
    )
    Import-Table 'Shortcut' @('Shortcut','Directory_','Name','Component_','Target','Arguments','Description','Hotkey','Icon_','IconIndex','ShowCmd','WkDir') @('s72','s72','l128','s72','s255','S255','L255','I2','S72','I2','I2','S72') @('Shortcut') @(,@('ConsoleShortcut','MENUDIR','MPBRDG|Point Bridge Console',$consoleComponent,('[#'+$consoleFile+']'),$null,'Open local Point Bridge management console',$null,$null,$null,1,'INSTALLDIR'))
    Import-Table 'RemoveFile' @('FileKey','Component_','FileName','DirProperty','InstallMode') @('s72','s72','L255','s72','i2') @('FileKey') @(,@('RemoveMenuFolder',$consoleComponent,$null,'MENUDIR',2))
    Import-Table 'Upgrade' @('UpgradeCode','VersionMin','VersionMax','Language','Attributes','Remove','ActionProperty') @('s38','S20','S20','S255','i4','S255','s72') @('UpgradeCode','VersionMin','VersionMax','Language','Attributes') @(
        @($upgradeCode,$null,$Version,$null,1,$null,'OLDPRODUCTS'),@($upgradeCode,$Version,$null,$null,258,$null,'NEWERPRODUCTFOUND')
    )
    # VersionNT is 603 on modern Windows for MSI compatibility; use the real build registry value.
    Import-Table 'RegLocator' @('Signature_','Root','Key','Name','Type') @('s72','i2','s255','S255','I2') @('Signature_') @(
        @('WindowsBuild',2,'SOFTWARE\Microsoft\Windows NT\CurrentVersion','CurrentBuildNumber',18),
        @('MetasysCoreService',2,'SYSTEM\CurrentControlSet\Services\MIIIDM','ImagePath',18),
        @('MetasysUiServer',2,'SOFTWARE\Johnson Controls\Metasys UI Assets','InstallPath',18),
        @('MetasysUiServer32',2,'SOFTWARE\Johnson Controls\Metasys UI Assets','InstallPath',2)
    )
    Import-Table 'AppSearch' @('Property','Signature_') @('s72','s72') @('Property','Signature_') @(@('WINBUILD','WindowsBuild'),@('METASYSCORE','MetasysCoreService'),@('METASYSUI','MetasysUiServer'),@('METASYSUI32','MetasysUiServer32'))
    Import-Table 'Signature' @('Signature','FileName','MinVersion','MaxVersion','MinSize','MaxSize','MinDate','MaxDate','Languages') @('s72','s255','S20','S20','I4','I4','I4','I4','S255') @('Signature') @()
    Import-Table 'LaunchCondition' @('Condition','Description') @('s255','l255') @('Condition') @(
        @('Installed OR (METASYSCORE AND (METASYSUI OR METASYSUI32))','Install Point Bridge on a Metasys server with Metasys core services and Metasys UI installed.'),
        @('Installed OR (VersionNT64 AND WINBUILD >= 14393 AND MsiNTProductType > 1)','Requires 64-bit Windows Server 2016 or later (build 14393 or newer).'),
        @('Installed OR NOT NEWERPRODUCTFOUND','A newer version of Metasys Point Bridge is already installed.'),
        @('Installed OR Privileged','Administrator privileges are required to install Point Bridge.')
    )
    $actions=@(@('FindRelatedProducts',$null,25),@('AppSearch',$null,50),@('LaunchConditions',$null,100),@('CostInitialize',$null,800),@('FileCost',$null,900),@('CostFinalize',$null,1000),@('MigrateFeatureStates',$null,1200),@('InstallValidate',$null,1400),@('InstallInitialize',$null,1500),@('RemoveExistingProducts','OLDPRODUCTS',1510),@('ProcessComponents',$null,1600),@('UnpublishFeatures',$null,1800),@('StopServices','VersionNT',1900),@('DeleteServices','VersionNT',2000),@('RemoveRegistryValues',$null,2600),@('RemoveShortcuts',$null,3200),@('RemoveFiles',$null,3500),@('RemoveFolders',$null,3600),@('CreateFolders',$null,3700),@('InstallFiles',$null,4000),@('CreateShortcuts',$null,4500),@('WriteRegistryValues',$null,5000),@('InstallServices','VersionNT',5800),@('MsiConfigureServices','VersionNT',5850),@('StartServices','VersionNT',5900),@('RegisterUser',$null,6000),@('RegisterProduct',$null,6100),@('PublishFeatures',$null,6300),@('PublishProduct',$null,6400),@('InstallFinalize',$null,6600))
    $actions=@($actions | Where-Object {$_[0] -ne 'MsiConfigureServices'})
    $actions+=,@('ConfigureBridgeRecovery',('$'+$serviceComponent+'=3'),5850)
    Import-Table 'InstallExecuteSequence' @('Action','Condition','Sequence') @('s72','S255','I2') @('Action') $actions
    Import-Table 'InstallUISequence' @('Action','Condition','Sequence') @('s72','S255','I2') @('Action') @(@('FindRelatedProducts',$null,25),@('AppSearch',$null,50),@('LaunchConditions',$null,100),@('CostInitialize',$null,800),@('FileCost',$null,900),@('CostFinalize',$null,1000),@('ExecuteAction',$null,1300))
    $summary=$database.SummaryInformation(20)
    $summary.Property(1)=1252;$summary.Property(2)='Installation Database';$summary.Property(3)='Metasys Point Bridge';$summary.Property(4)='Point Bridge'
    $summary.Property(7)='x64;1033';$summary.Property(9)='{'+[Guid]::NewGuid().ToString().ToUpperInvariant()+'}'
    $summary.Property(14)=500;$summary.Property(15)=2;$summary.Property(18)='Point Bridge native MSI authoring';$summary.Property(19)=2;[void]$summary.Persist();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary)
    $ddfPath=Join-Path $scratch 'payload.ddf';[IO.File]::WriteAllLines($ddfPath,$ddf,[Text.Encoding]::Default)
    $cabLog=Join-Path $scratch 'makecab.log'
    & "$env:SystemRoot\System32\makecab.exe" /F $ddfPath *> $cabLog
    if ($LASTEXITCODE -ne 0) { throw "makecab failed. See $cabLog" }
    $verifyDirectory=Join-Path $scratch 'cab-verify';[IO.Directory]::CreateDirectory($verifyDirectory)|Out-Null
    & "$env:SystemRoot\System32\expand.exe" (Join-Path $scratch 'payload.cab') '-F:*' $verifyDirectory *> (Join-Path $scratch 'expand.log')
    if($LASTEXITCODE -ne 0){throw 'Cabinet integrity extraction failed.'}
    foreach($entry in $manifest){if((Get-FileHash -LiteralPath (Join-Path $verifyDirectory $entry.Id) -Algorithm SHA256).Hash -ne $entry.SHA256){throw "Cabinet hash mismatch: $($entry.Path)"}}
    $stream=$installer.CreateRecord(2);$stream.StringData(1)='payload.cab';[void]$stream.SetStream(2,(Join-Path $scratch 'payload.cab'))
    $view=$database.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)');[void]$view.Execute($stream);[void]$view.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream)
    [void]$database.Commit()
    # Read-only relational and package consistency checks, without invoking msiexec.
    $fileRows=@(Query-Rows 'SELECT `File`, `Component_`, `Sequence` FROM `File`')
    $componentRows=@(Query-Rows 'SELECT `Component`, `KeyPath` FROM `Component`')
    $componentKeys=@{};foreach($r in $componentRows){$componentKeys[$r[0]]=$r[1]}
    if($fileRows.Count -ne $sequence){throw 'MSI file count mismatch.'}
    foreach($r in $fileRows){if(-not $componentKeys.ContainsKey($r[1])){throw 'File references missing component.'}}
    $cabRows=@(Query-Rows 'SELECT `Name` FROM `_Streams` WHERE `Name` = ''payload.cab''')
    if($cabRows.Count -ne 1){throw 'Embedded cabinet stream is missing.'}
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($output+'.manifest.json') -Encoding utf8
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database);$database=$null
    $session=$installer.OpenPackage($output,1)
    try {
        if($session.DoAction('AppSearch') -ne 1){throw 'Windows Installer AppSearch validation failed.'}
        if($session.Property('WINBUILD') -ne [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber){throw 'Windows Installer Windows build detection failed.'}
    } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($session)}
    [pscustomobject]@{Msi=$output;Version=$Version;Files=$sequence;Bytes=(Get-Item -LiteralPath $output).Length;SHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;UpgradeCode=$upgradeCode;CabinetHashesVerified=$true;AppSearchVerified=$true;Signed=$false;InstallerExecuted=$false;AuthoringScratch=$scratch}
}
finally {
    if($database){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)}
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
