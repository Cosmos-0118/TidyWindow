param(
    [switch] $SkipShellExtensionCleanup,
    [switch] $SkipFileAssociationRepair,
    [switch] $SkipLibraryRestore,
    [switch] $SkipDoubleClickReset,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    if ($script:TidyOutputLines -is [System.Collections.IList]) {
        [void]$script:TidyOutputLines.Add($text)
    }

    TidyWindow.Automation\Write-TidyLog -Level Information -Message $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    if ($script:TidyErrorLines -is [System.Collections.IList]) {
        [void]$script:TidyErrorLines.Add($text)
    }

    TidyWindow.Automation\Write-TidyError -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }

    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess,
        [int[]] $AcceptableExitCodes = @()
    )

    Write-TidyLog -Level Information -Message $Description

    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    if ($exitCode -eq 0 -and $output) {
        $lastItem = ($output | Select-Object -Last 1)
        if ($lastItem -is [int] -or $lastItem -is [long]) {
            $exitCode = [int]$lastItem
        }
    }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) { continue }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-TidyError -Message $entry
        }
        else {
            Write-TidyOutput -Message $entry
        }
    }

    if ($RequireSuccess -and $exitCode -ne 0) {
        $acceptsExitCode = $false
        if ($AcceptableExitCodes -and ($AcceptableExitCodes -contains $exitCode)) {
            $acceptsExitCode = $true
        }

        if (-not $acceptsExitCode) {
            throw "$Description failed with exit code $exitCode."
        }
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-RegistryKey {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (Test-Path -LiteralPath $Path) {
        return
    }

    try {
        [void](New-Item -Path $Path -Force)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to create registry key {0}: {1}" -f $Path, $_.Exception.Message)
        throw
    }
}

function Get-InProcServerPath {
    param([Parameter(Mandatory = $true)][string] $Clsid)

    $clsidPath = "Registry::HKEY_CLASSES_ROOT\CLSID\$Clsid\InprocServer32"
    try {
        $value = (Get-ItemProperty -Path $clsidPath -ErrorAction Stop).'(default)'
        return $value
    }
    catch {
        return $null
    }
}

function Clean-ShellExtensions {
    $approvedKeys = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
    )

    $blockedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked'
    Ensure-RegistryKey -Path $blockedKey

    $blocked = 0
    foreach ($key in $approvedKeys) {
        if (-not (Test-Path -LiteralPath $key)) { continue }

        $properties = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }
        foreach ($prop in $properties) {
            $clsid = $prop.Name
            if ([string]::IsNullOrWhiteSpace($clsid)) { continue }

            $dllPath = Get-InProcServerPath -Clsid $clsid
            $isMissing = [string]::IsNullOrWhiteSpace($dllPath) -or -not (Test-Path -LiteralPath $dllPath)
            if (-not $isMissing) { continue }

            try {
                New-ItemProperty -Path $blockedKey -Name $clsid -PropertyType String -Value '' -Force | Out-Null
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to block shell extension {0}: {1}" -f $clsid, $_.Exception.Message)
                continue
            }

            try {
                Remove-ItemProperty -Path $key -Name $clsid -ErrorAction SilentlyContinue
            }
            catch {
                Write-TidyOutput -Message ("Could not remove stale entry {0} from Approved: {1}" -f $clsid, $_.Exception.Message)
            }

            $blocked++
            Write-TidyOutput -Message ("Blocked stale shell extension {0} (missing handler)." -f $clsid)
        }
    }

    if ($blocked -eq 0) {
        Write-TidyOutput -Message 'No stale shell extensions found in Approved lists.'
    }
    else {
        Write-TidyOutput -Message ("Blocked {0} stale shell extension(s) and pruned Approved entries." -f $blocked)
    }
}

function Repair-FileAssociations {
    try {
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\.exe'
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\exefile'
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\exefile\shell\open\command'
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\exefile\DefaultIcon'

        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\.exe' -Name '(default)' -Value 'exefile' -ErrorAction Stop
        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\exefile' -Name 'FriendlyTypeName' -Value '@%SystemRoot%\System32\shell32.dll,-10150' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\exefile\shell\open\command' -Name '(default)' -Value '"%1" %*' -ErrorAction Stop
        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\exefile\DefaultIcon' -Name '(default)' -Value '"%1"' -ErrorAction SilentlyContinue

        Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.exe\UserChoice' -Recurse -Force -ErrorAction SilentlyContinue

        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\.lnk'
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\lnkfile'
        Ensure-RegistryKey -Path 'Registry::HKEY_CLASSES_ROOT\lnkfile\ShellEx'

        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\.lnk' -Name '(default)' -Value 'lnkfile' -ErrorAction Stop
        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\lnkfile' -Name 'IsShortcut' -Value '' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'Registry::HKEY_CLASSES_ROOT\lnkfile' -Name 'NeverShowExt' -Value '' -ErrorAction SilentlyContinue

        Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.lnk\UserChoice' -Recurse -Force -ErrorAction SilentlyContinue

        Write-TidyOutput -Message 'Repaired .exe and .lnk file associations to defaults.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("File association repair failed: {0}" -f $_.Exception.Message)
    }
}

function New-LibraryXml {
    param(
        [Parameter(Mandatory = $true)][string] $DisplayName,
        [Parameter(Mandatory = $true)][string] $ShellUrl,
        [Parameter(Mandatory = $true)][string] $FolderTypeGuid,
        [Parameter(Mandatory = $true)][string] $IconReference
    )

    return @"
<?xml version="1.0" encoding="UTF-8"?>
<libraryDescription xmlns="http://schemas.microsoft.com/windows/2009/library">
  <name>$DisplayName</name>
  <version>6</version>
  <isLibraryPinned>true</isLibraryPinned>
  <iconReference>$IconReference</iconReference>
  <templateInfo>
    <folderType>$FolderTypeGuid</folderType>
  </templateInfo>
  <searchConnectorDescriptionList>
    <searchConnectorDescription>
      <isDefaultSaveLocation>true</isDefaultSaveLocation>
      <isPinnedToNavigationPane>true</isPinnedToNavigationPane>
      <simpleLocation>
        <url>$ShellUrl</url>
      </simpleLocation>
    </searchConnectorDescription>
  </searchConnectorDescriptionList>
</libraryDescription>
"@
}

function Restore-DefaultLibraries {
    $libraryRoot = Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\Libraries'
    Ensure-RegistryKey -Path $libraryRoot

    $templates = @(
        (Join-Path -Path $env:SystemDrive -ChildPath 'Users\Default\AppData\Roaming\Microsoft\Windows\Libraries'),
        (Join-Path -Path $env:PUBLIC -ChildPath 'Libraries')
    )

    $definitions = @(
        @{ Name = 'Documents'; ShellUrl = 'shell:Personal'; FolderType = '{7d49d726-3c21-4f05-99aa-fdc2c9474656}'; Icon = 'imageres.dll,-1002' },
        @{ Name = 'Music'; ShellUrl = 'shell:My Music'; FolderType = '{94d6ddcc-4a68-4175-a374-bd584a510b78}'; Icon = 'imageres.dll,-108' },
        @{ Name = 'Pictures'; ShellUrl = 'shell:My Pictures'; FolderType = '{b3690e58-e961-423b-b687-386ebfd83239}'; Icon = 'imageres.dll,-113' },
        @{ Name = 'Videos'; ShellUrl = 'shell:My Video'; FolderType = '{5fa96407-7e77-483c-ac93-691d05850de8}'; Icon = 'imageres.dll,-189' }
    )

    $restored = 0
    foreach ($entry in $definitions) {
        $targetPath = Join-Path -Path $libraryRoot -ChildPath ("{0}.library-ms" -f $entry.Name)
        if (Test-Path -LiteralPath $targetPath) {
            Write-TidyOutput -Message ("Library '{0}' already present." -f $entry.Name)
            continue
        }

        $copied = $false
        foreach ($templateRoot in $templates) {
            if ([string]::IsNullOrWhiteSpace($templateRoot)) { continue }

            $templatePath = Join-Path -Path $templateRoot -ChildPath ("{0}.library-ms" -f $entry.Name)
            if (Test-Path -LiteralPath $templatePath) {
                try {
                    Copy-Item -LiteralPath $templatePath -Destination $targetPath -Force
                    $copied = $true
                    break
                }
                catch {
                    Write-TidyOutput -Message ("Failed to copy template for {0}: {1}" -f $entry.Name, $_.Exception.Message)
                }
            }
        }

        if (-not $copied) {
            try {
                $xml = New-LibraryXml -DisplayName $entry.Name -ShellUrl $entry.ShellUrl -FolderTypeGuid $entry.FolderType -IconReference $entry.Icon
                Set-Content -Path $targetPath -Value $xml -Encoding UTF8 -Force
                $copied = $true
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to generate library file for {0}: {1}" -f $entry.Name, $_.Exception.Message)
                continue
            }
        }

        if ($copied) {
            $restored++
            Write-TidyOutput -Message ("Restored '{0}' library." -f $entry.Name)
        }
    }

    if ($restored -eq 0) {
        Write-TidyOutput -Message 'No libraries required restoration.'
    }
    else {
        Write-TidyOutput -Message ("Restored {0} library file(s)." -f $restored)
    }
}

function Reset-DoubleClickAndExplorerTweaks {
    try {
        Ensure-RegistryKey -Path 'HKCU:\Control Panel\Mouse'
        Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name 'DoubleClickSpeed' -Value '500' -ErrorAction Stop
        Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name 'DoubleClickWidth' -Value 4 -ErrorAction Stop
        Set-ItemProperty -Path 'HKCU:\Control Panel\Mouse' -Name 'DoubleClickHeight' -Value 4 -ErrorAction Stop

        Ensure-RegistryKey -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'LaunchTo' -ErrorAction SilentlyContinue

        foreach ($policyKey in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer')) {
            if (-not (Test-Path -LiteralPath $policyKey)) { continue }
            foreach ($name in @('NoViewContextMenu', 'NoFolderOptions', 'NoFileAssociate')) {
                Remove-ItemProperty -Path $policyKey -Name $name -ErrorAction SilentlyContinue
            }
        }

        Invoke-TidyCommand -Command { Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue } -Description 'Restarting explorer to apply double-click and policy resets.'
        Start-Sleep -Milliseconds 300
        Invoke-TidyCommand -Command { Start-Process explorer.exe } -Description 'Starting explorer.' -RequireSuccess

        Write-TidyOutput -Message 'Reset double-click thresholds and cleared Explorer policy overrides.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Double-click/Explorer reset failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'File Explorer and context repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting File Explorer and context menu repair pack.'

    if (-not $SkipShellExtensionCleanup.IsPresent) {
        Clean-ShellExtensions
    }
    else {
        Write-TidyOutput -Message 'Skipping shell extension cleanup per operator request.'
    }

    if (-not $SkipFileAssociationRepair.IsPresent) {
        Repair-FileAssociations
    }
    else {
        Write-TidyOutput -Message 'Skipping file association repair per operator request.'
    }

    if (-not $SkipLibraryRestore.IsPresent) {
        Restore-DefaultLibraries
    }
    else {
        Write-TidyOutput -Message 'Skipping default library restore per operator request.'
    }

    if (-not $SkipDoubleClickReset.IsPresent) {
        Reset-DoubleClickAndExplorerTweaks
    }
    else {
        Write-TidyOutput -Message 'Skipping double-click and Explorer reset per operator request.'
    }

    Write-TidyOutput -Message 'File Explorer and context repair pack completed.'
}
catch {
    $script:OperationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_ | Out-String
    }

    Write-TidyLog -Level Error -Message $message
    Write-TidyError -Message $message
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
    Write-TidyLog -Level Information -Message 'File Explorer and context repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
