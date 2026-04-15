param(
    [switch] $SkipEdge,
    [switch] $SkipChrome,
    [switch] $SkipFirefox,
    [switch] $SkipDnsFlush,
    [switch] $SkipProxyReset,
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
        Write-TidyError -Message "  FAIL: $Name: $msg"
        $script:OperationSucceeded = $false
    }
}

# ══════════════════════════════════════════════════════════════════════
#  Helpers
# ══════════════════════════════════════════════════════════════════════

function Remove-BrowserCache {
    <#
        .SYNOPSIS
        Safely removes browser cache directories. Only targets well-known cache
        folders — never profile data, passwords, bookmarks, or extensions.
    #>
    param(
        [string]   $BrowserName,
        [string[]] $CachePaths,
        [string]   $ProcessName
    )

    # Ensure the browser is not running before cache deletion.
    $running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-TidyOutput -Message "  $BrowserName is running ($($running.Count) process(es)). Skipping cache clear to avoid data corruption."
        return 0
    }

    $bytesFreed = 0L
    foreach ($cachePath in $CachePaths) {
        $expanded = [Environment]::ExpandEnvironmentVariables($cachePath)
        if (-not (Test-Path -LiteralPath $expanded)) { continue }
        try {
            $size = (Get-ChildItem -LiteralPath $expanded -Recurse -File -Force -ErrorAction SilentlyContinue |
                     Measure-Object -Property Length -Sum).Sum
            Remove-Item -LiteralPath $expanded -Recurse -Force -ErrorAction Stop
            $bytesFreed += $size
        }
        catch {
            Write-TidyLog -Level Warning -Message "  Could not fully remove '$expanded': $($_.Exception.Message)"
        }
    }
    return $bytesFreed
}

# ══════════════════════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════════════════════
try {
    Write-TidyOutput -Message 'Starting browser reset and cache cleanup.'
    $totalFreed = 0L

    # ── 1. Microsoft Edge cache ───────────────────────────────────────
    if (-not $SkipEdge.IsPresent) {
        Invoke-Step -Name 'Clear Microsoft Edge cache' -Action {
            $edgeBase = Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data'
            if (-not (Test-Path -LiteralPath $edgeBase)) {
                Write-TidyOutput -Message '  Edge profile directory not found. Skipped.'
                return
            }

            # Collect cache folders from all profiles.
            $profiles = @(Get-ChildItem -LiteralPath $edgeBase -Directory -ErrorAction SilentlyContinue |
                          Where-Object { $_.Name -eq 'Default' -or $_.Name -match '^Profile \d+$' })
            $cacheDirs = foreach ($prof in $profiles) {
                Join-Path $prof.FullName 'Cache\Cache_Data'
                Join-Path $prof.FullName 'Code Cache'
                Join-Path $prof.FullName 'Service Worker\CacheStorage'
            }

            $freed = Remove-BrowserCache -BrowserName 'Edge' -CachePaths $cacheDirs -ProcessName 'msedge'
            $totalFreed += $freed
            Write-TidyOutput -Message "  Edge cache freed: $([math]::Round($freed / 1MB, 1)) MB"
        }
    }
    else { Write-TidyOutput -Message 'Edge cache cleanup skipped.' }

    # ── 2. Google Chrome cache ────────────────────────────────────────
    if (-not $SkipChrome.IsPresent) {
        Invoke-Step -Name 'Clear Google Chrome cache' -Action {
            $chromeBase = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data'
            if (-not (Test-Path -LiteralPath $chromeBase)) {
                Write-TidyOutput -Message '  Chrome profile directory not found. Skipped.'
                return
            }

            $profiles = @(Get-ChildItem -LiteralPath $chromeBase -Directory -ErrorAction SilentlyContinue |
                          Where-Object { $_.Name -eq 'Default' -or $_.Name -match '^Profile \d+$' })
            $cacheDirs = foreach ($prof in $profiles) {
                Join-Path $prof.FullName 'Cache\Cache_Data'
                Join-Path $prof.FullName 'Code Cache'
                Join-Path $prof.FullName 'Service Worker\CacheStorage'
            }

            $freed = Remove-BrowserCache -BrowserName 'Chrome' -CachePaths $cacheDirs -ProcessName 'chrome'
            $totalFreed += $freed
            Write-TidyOutput -Message "  Chrome cache freed: $([math]::Round($freed / 1MB, 1)) MB"
        }
    }
    else { Write-TidyOutput -Message 'Chrome cache cleanup skipped.' }

    # ── 3. Mozilla Firefox cache ──────────────────────────────────────
    if (-not $SkipFirefox.IsPresent) {
        Invoke-Step -Name 'Clear Mozilla Firefox cache' -Action {
            $ffBase = Join-Path $env:LOCALAPPDATA 'Mozilla\Firefox\Profiles'
            if (-not (Test-Path -LiteralPath $ffBase)) {
                Write-TidyOutput -Message '  Firefox profile directory not found. Skipped.'
                return
            }

            $profiles = @(Get-ChildItem -LiteralPath $ffBase -Directory -ErrorAction SilentlyContinue)
            $cacheDirs = foreach ($prof in $profiles) {
                Join-Path $prof.FullName 'cache2'
            }

            $freed = Remove-BrowserCache -BrowserName 'Firefox' -CachePaths $cacheDirs -ProcessName 'firefox'
            $totalFreed += $freed
            Write-TidyOutput -Message "  Firefox cache freed: $([math]::Round($freed / 1MB, 1)) MB"
        }
    }
    else { Write-TidyOutput -Message 'Firefox cache cleanup skipped.' }

    # ── 4. Flush DNS client cache ─────────────────────────────────────
    if (-not $SkipDnsFlush.IsPresent) {
        Invoke-Step -Name 'Flush DNS client cache' -Action {
            if (Get-Command Clear-DnsClientCache -ErrorAction SilentlyContinue) {
                Clear-DnsClientCache
            }
            else {
                $r = Invoke-TidyNativeCommand -FilePath 'ipconfig.exe' -Arguments '/flushdns' -TimeoutSeconds 15
                if (-not $r.Success) { throw "ipconfig /flushdns failed: $($r.Error)" }
            }
        }
    }
    else { Write-TidyOutput -Message 'DNS flush skipped.' }

    # ── 5. Reset WinHTTP / IE proxy settings ──────────────────────────
    if (-not $SkipProxyReset.IsPresent) {
        Invoke-Step -Name 'Reset WinHTTP proxy settings' -Action {
            if (-not (Test-TidyAdmin)) {
                Write-TidyOutput -Message '  Proxy reset requires elevation. Skipped.'
                return
            }
            $r = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments 'winhttp reset proxy' -TimeoutSeconds 15
            if (-not $r.Success) { throw "netsh winhttp reset proxy failed: $($r.Error)" }
        }

        Invoke-Step -Name 'Clear Internet Explorer / system SSL state' -Action {
            # Delete cached SSL sessions in the registry (non-destructive).
            $sslPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
            if (Test-Path -LiteralPath $sslPath) {
                # Remove ZoneMap override entries that may have been injected.
                $zonePath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap'
                if (Test-Path -LiteralPath $zonePath) {
                    # Only clear user-scoped overrides, never machine-level policy.
                    if (-not (Test-TidyGroupPolicyManaged -RegistryPath $zonePath)) {
                        # Clearing UNCAsIntranet and AutoDetect re-enables defaults.
                        Remove-ItemProperty -Path $zonePath -Name 'UNCAsIntranet' -ErrorAction SilentlyContinue
                        Remove-ItemProperty -Path $zonePath -Name 'AutoDetect' -ErrorAction SilentlyContinue
                    }
                    else {
                        Write-TidyOutput -Message '  ZoneMap is Group Policy managed. Skipped.'
                    }
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Proxy reset skipped.' }

    # ── Summary ───────────────────────────────────────────────────────
    Write-TidyOutput -Message ''
    Write-TidyOutput -Message "Browser cache cleanup completed. Total freed: $([math]::Round($totalFreed / 1MB, 1)) MB"
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Browser reset failed: $($_.Exception.Message)"
}
finally {
    Save-TidyResult
}
