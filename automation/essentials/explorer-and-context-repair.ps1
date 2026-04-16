param(
    [switch] $SkipShellExtensionCleanup,
    [switch] $SkipFileAssociationRepair,
    [switch] $SkipLibraryRestore,
    [switch] $DryRun,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Module bootstrap ──────────────────────────────────────────────────
$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) { $callerModulePath = $PSCommandPath }
$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) { $scriptDirectory = (Get-Location).Path }
$modulePath = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'))
if (-not (Test-Path $modulePath)) { throw "Automation module not found at '$modulePath'." }
Import-Module $modulePath -Force

# ── Result tracking ───────────────────────────────────────────────────
$script:TidyOutputLines  = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines   = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile  = -not [string]::IsNullOrWhiteSpace($ResultPath)
$script:DryRunMode       = $DryRun.IsPresent
if ($script:UsingResultFile) { $ResultPath = [System.IO.Path]::GetFullPath($ResultPath) }

function Write-TidyOutput { param([Parameter(Mandatory)][object]$Message)
    $t = Convert-TidyLogMessage -InputObject $Message; if ([string]::IsNullOrWhiteSpace($t)) { return }
    [void]$script:TidyOutputLines.Add($t); Write-TidyLog -Level Information -Message $t
}
function Write-TidyError { param([Parameter(Mandatory)][object]$Message)
    $t = Convert-TidyLogMessage -InputObject $Message; if ([string]::IsNullOrWhiteSpace($t)) { return }
    [void]$script:TidyErrorLines.Add($t); TidyWindow.Automation\Write-TidyError -Message $t
}
function Save-TidyResult {
    if (-not $script:UsingResultFile) { return }
    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }
    Set-Content -Path $ResultPath -Value ($payload | ConvertTo-Json -Depth 5) -Encoding UTF8
}
function Test-TidyAdmin {
    [bool](New-Object System.Security.Principal.WindowsPrincipal(
        [System.Security.Principal.WindowsIdentity]::GetCurrent()
    )).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]      $Name,
        [Parameter(Mandatory)][scriptblock] $Action,
        [switch] $Critical
    )
    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Name"
        return
    }
    Write-TidyOutput -Message "-> $Name"
    try {
        & $Action
        Write-TidyOutput -Message "  OK: $Name succeeded."
    }
    catch {
        $msg = $_.Exception.Message
        if ($Critical) {
            Write-TidyError -Message "  FAIL: $Name (critical): $msg"
            $script:OperationSucceeded = $false
            throw
        }
        Write-TidyError -Message "  FAIL: ${Name}: $msg"
        $script:OperationSucceeded = $false
    }
}

# ══════════════════════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════════════════════
try {
    if (-not (Test-TidyAdmin)) {
        throw 'Explorer and context repair requires an elevated PowerShell session.'
    }

    $backupDir = Get-TidyBackupDirectory -FeatureName 'ExplorerRepair'
    Write-TidyOutput -Message "Backup directory: $backupDir"
    Write-TidyOutput -Message 'Starting Explorer and context menu repair.'

    # ── 1. Clean stale shell extensions (opt-out) ─────────────────────
    if ($SkipShellExtensionCleanup.IsPresent) {
        Write-TidyOutput -Message 'Shell extension cleanup skipped.'
    }
    else {
        Invoke-Step -Name 'Clean stale shell extensions' -Action {
            $handlerPaths = @(
                'Registry::HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers',
                'Registry::HKEY_CLASSES_ROOT\Directory\shellex\ContextMenuHandlers',
                'Registry::HKEY_CLASSES_ROOT\Directory\Background\shellex\ContextMenuHandlers',
                'Registry::HKEY_CLASSES_ROOT\Folder\shellex\ContextMenuHandlers'
            )

            $removed = 0
            foreach ($basePath in $handlerPaths) {
                if (-not (Test-Path -LiteralPath $basePath)) { continue }
                $handlers = Get-ChildItem -LiteralPath $basePath -ErrorAction SilentlyContinue
                foreach ($handler in $handlers) {
                    try {
                        $clsid = (Get-ItemProperty -LiteralPath $handler.PSPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
                        if ([string]::IsNullOrWhiteSpace($clsid)) { continue }

                        # Verify the CLSID exists and has a valid InprocServer32.
                        $clsidPath = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\InprocServer32"
                        if (-not (Test-Path -LiteralPath $clsidPath)) {
                            # Backup before removal.
                            Backup-TidyRegistryKey -KeyPath $handler.PSPath -BackupDirectory $backupDir -Label 'shellex'
                            Remove-Item -LiteralPath $handler.PSPath -Recurse -Force -ErrorAction Stop
                            $removed++
                            continue
                        }

                        $dllPath = (Get-ItemProperty -LiteralPath $clsidPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
                        if (-not [string]::IsNullOrWhiteSpace($dllPath)) {
                            $resolved = [Environment]::ExpandEnvironmentVariables($dllPath)
                            if (-not (Test-Path -LiteralPath $resolved)) {
                                Backup-TidyRegistryKey -KeyPath $handler.PSPath -BackupDirectory $backupDir -Label 'shellex'
                                Remove-Item -LiteralPath $handler.PSPath -Recurse -Force -ErrorAction Stop
                                $removed++
                            }
                        }
                    }
                    catch {
                        Write-TidyLog -Level Warning -Message "Shell handler check failed for '$($handler.Name)': $($_.Exception.Message)"
                    }
                }
            }
            Write-TidyOutput -Message "  Removed $removed stale shell extension handler(s)."
        }
    }

    # ── 2. Repair .exe and .lnk file associations (opt-out) ──────────
    if ($SkipFileAssociationRepair.IsPresent) {
        Write-TidyOutput -Message 'File association repair skipped.'
    }
    else {
        Invoke-Step -Name 'Repair .exe and .lnk associations' -Action {
            # Backup current associations before modifying.
            foreach ($ext in @('.exe', '.lnk')) {
                $extKey = "Registry::HKEY_CLASSES_ROOT\$ext"
                if (Test-Path -LiteralPath $extKey) {
                    Backup-TidyRegistryKey -KeyPath $extKey -BackupDirectory $backupDir -Label "assoc-$ext"
                }
            }

            # Ensure .exe association is set to exefile.
            $exeKeyPath = 'Registry::HKEY_CLASSES_ROOT\.exe'
            if (-not (Test-Path -LiteralPath $exeKeyPath)) {
                New-Item -Path $exeKeyPath -Force | Out-Null
            }
            $current = (Get-ItemProperty -LiteralPath $exeKeyPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
            if ($current -ne 'exefile') {
                Set-ItemProperty -Path $exeKeyPath -Name '(default)' -Value 'exefile' -Type String
                Write-TidyOutput -Message '  Restored .exe -> exefile association.'
            }

            # Ensure exefile\shell\open\command is correct.
            $openCmdPath = 'Registry::HKEY_CLASSES_ROOT\exefile\shell\open\command'
            if (-not (Test-Path -LiteralPath $openCmdPath)) {
                New-Item -Path $openCmdPath -Force | Out-Null
            }
            Set-ItemProperty -Path $openCmdPath -Name '(default)' -Value '"%1" %*' -Type String

            # Ensure .lnk association is set to lnkfile.
            $lnkKeyPath = 'Registry::HKEY_CLASSES_ROOT\.lnk'
            if (-not (Test-Path -LiteralPath $lnkKeyPath)) {
                New-Item -Path $lnkKeyPath -Force | Out-Null
            }
            $currentLnk = (Get-ItemProperty -LiteralPath $lnkKeyPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
            if ($currentLnk -ne 'lnkfile') {
                Set-ItemProperty -Path $lnkKeyPath -Name '(default)' -Value 'lnkfile' -Type String
                Write-TidyOutput -Message '  Restored .lnk -> lnkfile association.'
            }
        }
    }

    # ── 3. Restore default Windows libraries (opt-out) ────────────────
    if ($SkipLibraryRestore.IsPresent) {
        Write-TidyOutput -Message 'Library restore skipped.'
    }
    else {
        Invoke-Step -Name 'Restore default libraries' -Action {
            $librariesDir = Join-Path $env:APPDATA 'Microsoft\Windows\Libraries'
            if (-not (Test-Path -LiteralPath $librariesDir)) {
                New-Item -Path $librariesDir -ItemType Directory -Force | Out-Null
            }

            $defaultLibs = @(
                @{ Name = 'Documents'; FolderType = '{7D49D726-3C21-4F05-99AA-FDC2C9474656}'; KnownFolder = '{FDD39AD0-238F-46AF-ADB4-6C85480369C7}'; Icon = 'imageres.dll,-112' },
                @{ Name = 'Music';     FolderType = '{94D6DDCC-4A68-4175-A374-BD584A510B78}'; KnownFolder = '{4BD8D571-6D19-48D3-BE97-422220080E43}'; Icon = 'imageres.dll,-108' },
                @{ Name = 'Pictures';  FolderType = '{B3690E58-E961-423B-B687-386EBFD83239}'; KnownFolder = '{33E28130-4E1E-4676-835A-98395C3BC3BB}'; Icon = 'imageres.dll,-113' },
                @{ Name = 'Videos';    FolderType = '{5FA96407-7E77-483C-AC93-691D05850DE8}'; KnownFolder = '{18989B1D-99B5-455B-841C-AB7C74E4DDFC}'; Icon = 'imageres.dll,-189' }
            )

            # Try to copy from system templates first.
            $templateRoots = @(
                (Join-Path $env:SystemRoot 'SysWOW64'),
                (Join-Path $env:SystemRoot 'System32')
            )

            foreach ($lib in $defaultLibs) {
                $targetPath = Join-Path $librariesDir "$($lib.Name).library-ms"
                $restored = $false

                foreach ($root in $templateRoots) {
                    $templateFile = Join-Path $root "$($lib.Name).library-ms"
                    if (Test-Path -LiteralPath $templateFile) {
                        Copy-Item -LiteralPath $templateFile -Destination $targetPath -Force
                        $restored = $true
                        break
                    }
                }

                if (-not $restored) {
                    # Generate minimal library XML as a last resort.
                    $xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<libraryDescription xmlns="http://schemas.microsoft.com/windows/2009/library">
  <name>@shell32.dll,-34575</name>
  <version>1</version>
  <isLibraryPinned>true</isLibraryPinned>
  <iconReference>$($lib.Icon)</iconReference>
  <templateInfo><folderType>$($lib.FolderType)</folderType></templateInfo>
  <searchConnectorDescriptionList>
    <searchConnectorDescription><isDefaultSaveLocation>true</isDefaultSaveLocation>
      <simpleLocation><url>knownfolder:$($lib.KnownFolder)</url></simpleLocation>
    </searchConnectorDescription>
  </searchConnectorDescriptionList>
</libraryDescription>
"@
                    Set-Content -Path $targetPath -Value $xml -Encoding UTF8
                }
            }
            Write-TidyOutput -Message "  Restored $($defaultLibs.Count) default library definitions."
        }
    }

    # ── 4. Graceful Explorer restart ──────────────────────────────────
    Invoke-Step -Name 'Restart Explorer' -Action {
        # Give Explorer a chance to close gracefully rather than force-killing.
        $explorerProcs = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
        if ($explorerProcs.Count -eq 0) {
            Write-TidyOutput -Message '  Explorer is not running. Starting it.'
            Start-Process explorer.exe
            return
        }

        foreach ($proc in $explorerProcs) {
            try {
                # Try graceful close first (sends WM_CLOSE).
                $closed = $proc.CloseMainWindow()
                if ($closed) {
                    $proc.WaitForExit(5000) | Out-Null
                }
            }
            catch { }
        }

        # If any explorer processes remain, force-stop them.
        $remaining = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
        foreach ($proc in $remaining) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
        }

        Start-Sleep -Milliseconds 500
        Start-Process explorer.exe

        # Wait for Explorer to fully start.
        $waited = 0
        while ($waited -lt 10) {
            Start-Sleep -Seconds 1; $waited++
            $running = Get-Process -Name explorer -ErrorAction SilentlyContinue
            if ($running) { break }
        }

        if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
            # Emergency: start Explorer if it didn't come back.
            Start-Process explorer.exe
            Write-TidyError -Message '  Explorer did not restart within 10 seconds. Emergency start issued.'
        }
    }

    Write-TidyOutput -Message ''
    Write-TidyOutput -Message 'Explorer and context menu repair completed.'
    Write-TidyOutput -Message "Registry backups saved to: $backupDir"
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Explorer repair failed: $($_.Exception.Message)"
}
finally {
    Save-TidyResult
}
