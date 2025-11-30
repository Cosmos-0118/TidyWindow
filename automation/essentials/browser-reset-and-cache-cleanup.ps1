param(
    [switch] $IncludeEdge,
    [switch] $IncludeChrome,
    [switch] $IncludeBrave,
    [switch] $IncludeFirefox,
    [switch] $IncludeOpera,
    [switch] $ClearProfileCaches,
    [switch] $ClearWebViewCaches,
    [switch] $ResetPolicies,
    [switch] $RepairEdgeInstall,
    [switch] $ForceCloseBrowsers,
    [switch] $DryRun,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ($IncludeEdge -or $IncludeChrome -or $IncludeBrave -or $IncludeFirefox -or $IncludeOpera)) {
    $IncludeEdge = $true
}

if (-not ($ClearProfileCaches -or $ClearWebViewCaches -or $ResetPolicies -or $RepairEdgeInstall -or $ForceCloseBrowsers)) {
    $ClearProfileCaches = $true
    $ClearWebViewCaches = $true
    $ForceCloseBrowsers = $true
}

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
$script:CleanupDirectories = 0
$script:CleanupBytes = [long]0
$script:PolicyKeysCleared = 0
$script:PolicyKeysSkipped = 0
$script:RepairAttempted = $false
$script:RepairSucceeded = $false
$script:IsAdminSession = [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
$script:TargetBrowsers = [System.Collections.Generic.List[string]]::new()

if ($IncludeEdge)    { [void]$script:TargetBrowsers.Add('Edge') }
if ($IncludeChrome)  { [void]$script:TargetBrowsers.Add('Chrome') }
if ($IncludeBrave)   { [void]$script:TargetBrowsers.Add('Brave') }
if ($IncludeFirefox) { [void]$script:TargetBrowsers.Add('Firefox') }
if ($IncludeOpera)   { [void]$script:TargetBrowsers.Add('Opera') }

if ($script:TargetBrowsers.Count -eq 0) {
    $IncludeEdge = $true
    [void]$script:TargetBrowsers.Add('Edge')
}

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyOutputLines.Add($text)
    Write-Output $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
        Metrics = [pscustomobject]@{
            BrowsersTargeted   = $script:TargetBrowsers
            ClearedDirectories = $script:CleanupDirectories
            ReclaimedBytes     = $script:CleanupBytes
            PolicyKeysCleared  = $script:PolicyKeysCleared
            RepairAttempted    = $script:RepairAttempted
            RepairSucceeded    = $script:RepairSucceeded
        }
    }

    $json = $payload | ConvertTo-Json -Depth 6
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Format-TidySize {
    param([long] $Bytes)

    if ($Bytes -le 0) { return '0 B' }
    $units = 'B','KB','MB','GB','TB'
    $value = [double]$Bytes
    $order = 0
    while ($value -ge 1024 -and $order -lt ($units.Length - 1)) {
        $value /= 1024
        $order++
    }
    return ('{0:N1} {1}' -f $value, $units[$order])
}

function Join-TidyPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,
        [Parameter(Mandatory = $true)]
        [object] $Segments
    )

    if (-not $Segments) {
        return $Root
    }

    $current = $Root
    $parts = @()
    if ($Segments -is [System.Array]) {
        $parts = $Segments
    }
    else {
        $parts = @($Segments)
    }

    foreach ($segment in $parts) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $current = Join-Path -Path $current -ChildPath $segment
    }

    return $current
}

function Clear-TidyDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [string] $Label = $Path
    )

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return
    }

    if (-not (Test-Path -LiteralPath $resolved)) {
        Write-TidyOutput -Message ("{0}: nothing to clear (path not found)." -f $Label)
        return
    }

    $bytes = 0
    try {
        $bytes = Measure-TidyDirectoryBytes -Path $resolved
    }
    catch {
        $bytes = 0
    }

    if ($DryRun.IsPresent) {
        $hint = if ($bytes -gt 0) { " (~$((Format-TidySize -Bytes $bytes)))" } else { '' }
        Write-TidyOutput -Message ("[DryRun] Would clear {0}{1}." -f $Label, $hint)
        return
    }

    try {
        Get-ChildItem -LiteralPath $resolved -Force -ErrorAction Stop | Remove-Item -Recurse -Force -ErrorAction Stop
        $script:CleanupDirectories++
        $script:CleanupBytes += $bytes
        Write-TidyOutput -Message ("Cleared {0} (reclaimed {1})." -f $Label, (Format-TidySize -Bytes $bytes))
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to clear {0}: {1}" -f $Label, $_.Exception.Message)
    }
}

function Get-BrowserDisplayName {
    param([string] $Browser)

    switch ($Browser) {
        'Edge'    { return 'Microsoft Edge' }
        'Chrome'  { return 'Google Chrome' }
        'Brave'   { return 'Brave Browser' }
        'Firefox' { return 'Mozilla Firefox' }
        'Opera'   { return 'Opera' }
        default   { return $Browser }
    }
}

function Get-BrowserProcessNames {
    param([string] $Browser)

    switch ($Browser) {
        'Edge'    { return @('msedge','msedgewebview2','msedgebroker','msedgeupdate') }
        'Chrome'  { return @('chrome','googleupdate') }
        'Brave'   { return @('brave','bravesoftwareupdate','bravevpn') }
        'Firefox' { return @('firefox') }
        'Opera'   { return @('opera','opera_browser','opera_autoupdate') }
        default   { return @() }
    }
}

function Stop-TidyBrowserProcesses {
    if (-not $ForceCloseBrowsers.IsPresent) {
        return
    }

    $processNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($browser in $script:TargetBrowsers) {
        foreach ($name in (Get-BrowserProcessNames -Browser $browser)) {
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                [void]$processNames.Add($name)
            }
        }
    }

    if ($processNames.Count -eq 0) {
        Write-TidyOutput -Message 'No known browser processes to close.'
        return
    }

    foreach ($name in $processNames) {
        $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
        if (-not $processes) {
            continue
        }

        foreach ($proc in $processes) {
            if ($DryRun.IsPresent) {
                Write-TidyOutput -Message ("[DryRun] Would stop process {0} (Id {1})." -f $proc.ProcessName, $proc.Id)
                continue
            }

            try {
                if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
                    $null = $proc.CloseMainWindow()
                    Start-Sleep -Milliseconds 600
                }

                if (-not $proc.HasExited) {
                    Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                }

                Write-TidyOutput -Message ("Stopped process {0} (Id {1})." -f $proc.ProcessName, $proc.Id)
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to stop {0} (Id {1}): {2}" -f $proc.ProcessName, $proc.Id, $_.Exception.Message)
            }
        }
    }
}

$script:ChromiumProfileSubFolders = @(
    @('Cache'),
    @('Code Cache'),
    @('GPUCache'),
    @('Service Worker','CacheStorage'),
    @('Service Worker','Database'),
    @('IndexedDB'),
    @('Local Storage'),
    @('Session Storage'),
    @('OptimizationGuidePredictionModels'),
    @('GrShaderCache')
)

$script:ChromiumGlobalFolderSegments = @(
    @('Crashpad'),
    @('ShaderCache'),
    @('SwReporter'),
    @('CertificateTransparency'),
    @('GrShaderCache'),
    @('BrowserMetrics')
)

$script:FirefoxProfileSubFolders = @(
    @('cache2'),
    @('startupCache'),
    @('offlineCache'),
    @('safebrowsing'),
    @('security_state'),
    @('shader-cache'),
    @('storage','default'),
    @('storage','permanent'),
    @('storage','temporary')
)

function Get-ChromiumProfileDirectories {
    param(
        [string] $Root,
        [switch] $TreatRootAsProfile
    )

    $resolved = Resolve-TidyPath -Path $Root
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-Path -LiteralPath $resolved)) {
        return @()
    }

    if ($TreatRootAsProfile.IsPresent) {
        $item = Get-Item -LiteralPath $resolved -ErrorAction SilentlyContinue
        if ($item -and $item.PSIsContainer) {
            return @($item)
        }
        return @()
    }

    try {
        $candidates = Get-ChildItem -LiteralPath $resolved -Directory -ErrorAction Stop
    }
    catch {
        return @()
    }

    $filtered = @()
    foreach ($candidate in $candidates) {
        if ($candidate.Name -eq 'Default' -or $candidate.Name -like 'Profile *' -or $candidate.Name -eq 'Guest Profile') {
            $filtered += $candidate
        }
    }

    if ($filtered.Count -eq 0) {
        return $candidates
    }

    return $filtered
}

function Get-ChromiumCacheTargets {
    param(
        [System.IO.DirectoryInfo[]] $Profiles,
        [string] $BrowserLabel,
        [string] $UserDataRoot,
        [object[]] $GlobalFolders
    )

    $targets = [System.Collections.Generic.List[psobject]]::new()
    foreach ($profile in $Profiles) {
        foreach ($segments in $script:ChromiumProfileSubFolders) {
            $path = Join-TidyPath -Root $profile.FullName -Segments $segments
            $segmentLabel = ($segments -join '/')
            $label = "{0} profile '{1}' - {2}" -f $BrowserLabel, $profile.Name, $segmentLabel
            $targets.Add([pscustomobject]@{ Path = $path; Label = $label }) | Out-Null
        }
    }

    $resolvedRoot = Resolve-TidyPath -Path $UserDataRoot
    if ($resolvedRoot -and (Test-Path -LiteralPath $resolvedRoot) -and $GlobalFolders) {
        foreach ($segments in $GlobalFolders) {
            $path = Join-TidyPath -Root $resolvedRoot -Segments $segments
            $segmentLabel = ($segments -join '/')
            $label = "{0} global {1}" -f $BrowserLabel, $segmentLabel
            $targets.Add([pscustomobject]@{ Path = $path; Label = $label }) | Out-Null
        }
    }

    return $targets
}

function Get-LegacyEdgeTargets {
    $targets = [System.Collections.Generic.List[psobject]]::new()
    $legacyRoot = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe\AC\MicrosoftEdge\User\Default')
    if (-not $legacyRoot -or -not (Test-Path -LiteralPath $legacyRoot)) {
        return $targets
    }

    $folders = @('Cache','Databases','Indexed DB','LocalState','Service Worker')
    foreach ($folder in $folders) {
        $path = Join-TidyPath -Root $legacyRoot -Segments @($folder)
        $targets.Add([pscustomobject]@{ Path = $path; Label = "Edge legacy $folder" }) | Out-Null
    }

    return $targets
}

function Get-OperaProfileDirectories {
    $list = [System.Collections.Generic.List[System.IO.DirectoryInfo]]::new()
    $candidateRoots = @(
        Join-Path -Path $env:APPDATA -ChildPath 'Opera Software'
        Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Opera Software'
    )

    foreach ($candidate in $candidateRoots) {
        $root = Resolve-TidyPath -Path $candidate
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
            continue
        }

        $dirs = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Opera*' }
        foreach ($dir in $dirs) {
            $list.Add($dir) | Out-Null
        }
    }

    return $list.ToArray()
}

function Get-FirefoxCacheTargets {
    $targets = [System.Collections.Generic.List[psobject]]::new()
    $roots = @(
        @{ Path = Resolve-TidyPath -Path (Join-Path -Path $env:APPDATA -ChildPath 'Mozilla\Firefox\Profiles'); Label = 'Firefox (Roaming)' },
        @{ Path = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Mozilla\Firefox\Profiles'); Label = 'Firefox (Local)' }
    )

    foreach ($root in $roots) {
        if (-not $root.Path -or -not (Test-Path -LiteralPath $root.Path)) {
            continue
        }

        $profiles = Get-ChildItem -LiteralPath $root.Path -Directory -ErrorAction SilentlyContinue
        foreach ($profile in $profiles) {
            foreach ($segments in $script:FirefoxProfileSubFolders) {
                $path = Join-TidyPath -Root $profile.FullName -Segments $segments
                $segmentLabel = ($segments -join '/')
                $label = "{0} profile '{1}' - {2}" -f $root.Label, $profile.Name, $segmentLabel
                $targets.Add([pscustomobject]@{ Path = $path; Label = $label }) | Out-Null
            }
        }
    }

    return $targets
}

function Get-BrowserCacheTargets {
    param([string] $Browser)

    switch ($Browser) {
        'Edge' {
            $root = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Edge\User Data')
            $profiles = Get-ChromiumProfileDirectories -Root $root
            $targets = Get-ChromiumCacheTargets -Profiles $profiles -BrowserLabel 'Edge' -UserDataRoot $root -GlobalFolders $script:ChromiumGlobalFolderSegments
            foreach ($legacy in (Get-LegacyEdgeTargets)) {
                $targets.Add($legacy) | Out-Null
            }
            return $targets
        }
        'Chrome' {
            $root = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Google\Chrome\User Data')
            $profiles = Get-ChromiumProfileDirectories -Root $root
            return Get-ChromiumCacheTargets -Profiles $profiles -BrowserLabel 'Chrome' -UserDataRoot $root -GlobalFolders $script:ChromiumGlobalFolderSegments
        }
        'Brave' {
            $root = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'BraveSoftware\Brave-Browser\User Data')
            $profiles = Get-ChromiumProfileDirectories -Root $root
            return Get-ChromiumCacheTargets -Profiles $profiles -BrowserLabel 'Brave' -UserDataRoot $root -GlobalFolders $script:ChromiumGlobalFolderSegments
        }
        'Opera' {
            $profiles = Get-OperaProfileDirectories
            return Get-ChromiumCacheTargets -Profiles $profiles -BrowserLabel 'Opera' -UserDataRoot $null -GlobalFolders @()
        }
        'Firefox' {
            return Get-FirefoxCacheTargets
        }
        default { return [System.Collections.Generic.List[psobject]]::new() }
    }
}

function Invoke-BrowserCacheCleanup {
    if (-not $ClearProfileCaches.IsPresent) {
        return
    }

    foreach ($browser in $script:TargetBrowsers) {
        $displayName = Get-BrowserDisplayName -Browser $browser
        $targets = @(Get-BrowserCacheTargets -Browser $browser)
        if (-not $targets -or $targets.Count -eq 0) {
            Write-TidyOutput -Message ("{0}: no cache directories found." -f $displayName)
            continue
        }

        Write-TidyOutput -Message ("Clearing cache data for {0}." -f $displayName)
        foreach ($target in $targets) {
            Clear-TidyDirectory -Path $target.Path -Label $target.Label
        }
    }
}

function Invoke-WebViewCacheCleanup {
    if (-not $ClearWebViewCaches.IsPresent) {
        return
    }

    if (-not $script:TargetBrowsers.Contains('Edge')) {
        Write-TidyOutput -Message 'WebView2 cleanup skipped (Microsoft Edge not selected).'
        return
    }

    $webViewRoot = Resolve-TidyPath -Path (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\EdgeWebView')
    if (-not $webViewRoot -or -not (Test-Path -LiteralPath $webViewRoot)) {
        Write-TidyOutput -Message 'Edge WebView2 root not found; nothing to clear.'
        return
    }

    Write-TidyOutput -Message 'Clearing Edge WebView2 runtime caches.'

    $containers = @()
    try {
        $containers = Get-ChildItem -LiteralPath $webViewRoot -Directory -ErrorAction Stop
    }
    catch {
        $containers = @()
    }

    foreach ($container in $containers) {
        $paths = @('Cache','Code Cache','GPUCache','Service Worker','EBWebView','User Data')
        foreach ($relative in $paths) {
            $fullPath = Join-TidyPath -Root $container.FullName -Segments @($relative)
            Clear-TidyDirectory -Path $fullPath -Label ("WebView2 '{0}' - {1}" -f $container.Name, $relative)
        }
    }
}

function Get-PolicyDefinitions {
    param([string] $Browser)

    switch ($Browser) {
        'Edge' {
            return @(
                @{ Path = 'HKCU:SOFTWARE\Policies\Microsoft\Edge'; RequiresAdmin = $false },
                @{ Path = 'HKLM:SOFTWARE\Policies\Microsoft\Edge'; RequiresAdmin = $true },
                @{ Path = 'HKLM:SOFTWARE\Policies\Microsoft\EdgeUpdate'; RequiresAdmin = $true },
                @{ Path = 'HKLM:SOFTWARE\Policies\Microsoft\EdgePerformance'; RequiresAdmin = $true }
            )
        }
        'Chrome' {
            return @(
                @{ Path = 'HKCU:SOFTWARE\Policies\Google\Chrome'; RequiresAdmin = $false },
                @{ Path = 'HKLM:SOFTWARE\Policies\Google\Chrome'; RequiresAdmin = $true },
                @{ Path = 'HKLM:SOFTWARE\Policies\Google\Update'; RequiresAdmin = $true }
            )
        }
        'Brave' {
            return @(
                @{ Path = 'HKCU:SOFTWARE\Policies\BraveSoftware\Brave'; RequiresAdmin = $false },
                @{ Path = 'HKLM:SOFTWARE\Policies\BraveSoftware\Brave'; RequiresAdmin = $true }
            )
        }
        'Opera' {
            return @(
                @{ Path = 'HKCU:SOFTWARE\Policies\Opera Software\Opera'; RequiresAdmin = $false },
                @{ Path = 'HKLM:SOFTWARE\Policies\Opera Software\Opera'; RequiresAdmin = $true }
            )
        }
        default { return @() }
    }
}

function Invoke-BrowserPolicyReset {
    if (-not $ResetPolicies.IsPresent) {
        return
    }

    foreach ($browser in $script:TargetBrowsers) {
        $definitions = @(Get-PolicyDefinitions -Browser $browser)
        if (-not $definitions -or $definitions.Count -eq 0) {
            Write-TidyOutput -Message ("{0}: no registry policy keys defined for reset." -f (Get-BrowserDisplayName -Browser $browser))
            continue
        }

        Write-TidyOutput -Message ("Resetting policy keys for {0}." -f (Get-BrowserDisplayName -Browser $browser))
        foreach ($definition in $definitions) {
            $path = $definition.Path
            $resolved = ConvertTo-TidyRegistryPath -KeyPath $path
            if (-not $resolved -or -not (Test-Path -LiteralPath $resolved)) {
                Write-TidyOutput -Message ("{0}: no policy key found." -f $path)
                continue
            }

            if ($definition.RequiresAdmin -and -not $script:IsAdminSession) {
                $script:PolicyKeysSkipped++
                Write-TidyOutput -Message ("Skipping {0}: administrator privileges required." -f $path)
                continue
            }

            if ($DryRun.IsPresent) {
                Write-TidyOutput -Message ("[DryRun] Would remove {0}." -f $path)
                continue
            }

            try {
                Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
                $script:PolicyKeysCleared++
                Write-TidyOutput -Message ("Removed {0}." -f $path)
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to remove {0}: {1}" -f $path, $_.Exception.Message)
            }
        }
    }
}

function Get-EdgeSetupPath {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $roots = @($env:ProgramFiles, $programFilesX86) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($root in $roots) {
        $applicationRoot = Join-Path -Path $root -ChildPath 'Microsoft\Edge\Application'
        if (-not (Test-Path -LiteralPath $applicationRoot)) {
            continue
        }

        $versions = @()
        try {
            $versions = Get-ChildItem -LiteralPath $applicationRoot -Directory -ErrorAction Stop | Where-Object { $_.Name -match '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' }
        }
        catch {
            $versions = @()
        }

        $ordered = $versions | Sort-Object { try { [version]$_.Name } catch { [version]'0.0.0.0' } } -Descending
        $candidate = $ordered | Select-Object -First 1
        if (-not $candidate) {
            continue
        }

        $setupPath = Join-Path -Path $candidate.FullName -ChildPath 'Installer\setup.exe'
        if (Test-Path -LiteralPath $setupPath) {
            return $setupPath
        }
    }

    return $null
}

function Invoke-EdgeRepair {
    if (-not $RepairEdgeInstall.IsPresent) {
        return
    }

    if (-not $script:TargetBrowsers.Contains('Edge')) {
        Write-TidyOutput -Message 'Edge installer repair requested but Microsoft Edge is not selected; skipping.'
        return
    }

    if (-not $script:IsAdminSession) {
        throw 'Edge installer repair requires an elevated PowerShell session.'
    }

    $setupPath = Get-EdgeSetupPath
    if (-not $setupPath) {
        $script:OperationSucceeded = $false
        Write-TidyError -Message 'Could not locate Microsoft Edge setup.exe for repair.'
        return
    }

    if ($DryRun.IsPresent) {
        Write-TidyOutput -Message ("[DryRun] Would run '{0}' with repair arguments." -f $setupPath)
        return
    }

    Write-TidyOutput -Message 'Running Microsoft Edge installer repair (force reinstall).'
    try {
        $arguments = '--force-reinstall --system-level --repair --verbose-logging'
        $process = Start-Process -FilePath $setupPath -ArgumentList $arguments -Wait -PassThru
        $script:RepairAttempted = $true
        if ($process.ExitCode -eq 0) {
            $script:RepairSucceeded = $true
            Write-TidyOutput -Message 'Edge installer repair completed successfully.'
        }
        else {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Edge installer repair exited with code {0}." -f $process.ExitCode)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Edge installer repair failed: {0}" -f $_.Exception.Message)
    }
}

function Write-TidySummary {
    $sizeText = Format-TidySize -Bytes $script:CleanupBytes
    $browserList = ($script:TargetBrowsers -join ', ')
    Write-TidyOutput -Message '--- Browser reset summary ---'
    $browserSummary = if ([string]::IsNullOrWhiteSpace($browserList)) { 'None' } else { $browserList }
    Write-TidyOutput -Message ("Browsers targeted: {0}" -f $browserSummary)
    Write-TidyOutput -Message ("Cleared directories: {0}" -f $script:CleanupDirectories)
    Write-TidyOutput -Message ("Estimated space reclaimed: {0}" -f $sizeText)
    if ($ResetPolicies.IsPresent) {
        Write-TidyOutput -Message ("Policy keys removed: {0}" -f $script:PolicyKeysCleared)
        if ($script:PolicyKeysSkipped -gt 0) {
            Write-TidyOutput -Message ("Policy keys skipped (no admin): {0}" -f $script:PolicyKeysSkipped)
        }
    }
    if ($RepairEdgeInstall.IsPresent) {
        $status = if ($script:RepairSucceeded) { 'Success' } elseif ($script:RepairAttempted) { 'Failed' } else { 'Not run' }
        Write-TidyOutput -Message ("Edge installer repair status: {0}" -f $status)
    }
}

try {
    Write-TidyLog -Level Information -Message ("Starting browser reset & cache cleanup task for: {0}." -f ($script:TargetBrowsers -join ', '))

    Stop-TidyBrowserProcesses
    Invoke-BrowserCacheCleanup
    Invoke-WebViewCacheCleanup
    Invoke-BrowserPolicyReset
    Invoke-EdgeRepair

    Write-TidySummary
    Write-TidyOutput -Message 'Browser reset & cleanup routine completed.'
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
    Write-TidyLog -Level Information -Message 'Browser reset script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
