param(
    [int] $PreviewCount = 10,
    [bool] $IncludeDownloads = $false,
    [ValidateSet('Files', 'Folders', 'Both')]
    [string] $ItemKind = 'Files'
)

$callerModulePath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
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

Write-TidyLog -Level Information -Message "Starting cleanup preview scan (PreviewCount=$PreviewCount, IncludeDownloads=$IncludeDownloads, ItemKind=$ItemKind)."

function Add-TidyTopItem {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]] $Container,
        [Parameter(Mandatory = $true)]
        [int] $Capacity,
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Item
    )

    if ($Capacity -le 0) {
        return
    }

    if ($null -eq $Item) {
        return
    }

    if ($Container.Count -lt $Capacity) {
        $null = $Container.Add($Item)
        return
    }

    $minIndex = 0
    $minSize = [long]$Container[0].SizeBytes

    for ($index = 1; $index -lt $Container.Count; $index++) {
        $candidateSize = [long]$Container[$index].SizeBytes
        if ($candidateSize -lt $minSize) {
            $minSize = $candidateSize
            $minIndex = $index
        }
    }

    if ([long]$Item.SizeBytes -le $minSize) {
        return
    }

    $Container[$minIndex] = $Item
}

function Resolve-TidyPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [System.Environment]::ExpandEnvironmentVariables($Path)

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $null
    }
}

function Get-TidyDirectoryReport {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Category,
        [Parameter(Mandatory = $true)]
        [string] $Classification,
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [int] $PreviewCount,
        [string] $Notes,
        [string] $ItemKind
    )

    $effectiveNotes = if ([string]::IsNullOrWhiteSpace($Notes)) {
        'Dry run only. No files were deleted.'
    }
    else {
        $Notes
    }

    $resolvedPath = Resolve-TidyPath -Path $Path
    if (-not $resolvedPath) {
        Write-TidyLog -Level Warning -Message "Category '$Category' [Type=$Classification] has an invalid path definition: '$Path'."
        return [pscustomobject]@{
            Category       = $Category
            Classification = $Classification
            Path           = $Path
            Exists         = $false
            ItemCount      = 0
            TotalSizeBytes = 0
            DryRun         = $true
            Preview        = @()
            Notes          = 'Path could not be resolved.'
        }
    }

    $exists = Test-Path -LiteralPath $resolvedPath -PathType Container
    if (-not $exists) {
        Write-TidyLog -Level Information -Message "Category '$Category' [Type=$Classification] path '$resolvedPath' does not exist."
        return [pscustomobject]@{
            Category       = $Category
            Classification = $Classification
            Path           = $resolvedPath
            Exists         = $false
            ItemCount      = 0
            TotalSizeBytes = 0
            DryRun         = $true
            Preview        = @()
            Notes          = 'No directory located.'
        }
    }

    $directoryInfo = [System.IO.DirectoryInfo]::new($resolvedPath)
    $fileCount = 0
    $totalSize = [long]0

    $topFiles = [System.Collections.Generic.List[object]]::new()
    $topDirectories = [System.Collections.Generic.List[object]]::new()

    $allOptions = [System.IO.EnumerationOptions]::new()
    $allOptions.RecurseSubdirectories = $true
    $allOptions.IgnoreInaccessible = $true
    $allOptions.AttributesToSkip = [System.IO.FileAttributes]::ReparsePoint -bor [System.IO.FileAttributes]::Offline

    $directOptions = [System.IO.EnumerationOptions]::new()
    $directOptions.RecurseSubdirectories = $false
    $directOptions.IgnoreInaccessible = $true
    $directOptions.AttributesToSkip = [System.IO.FileAttributes]::ReparsePoint -bor [System.IO.FileAttributes]::Offline

    $directoryStats = [System.Collections.Hashtable]::new([System.StringComparer]::OrdinalIgnoreCase)
    $immediateDirectories = @()
    if ($ItemKind -ne 'Files') {
        try {
            $immediateDirectories = $directoryInfo.EnumerateDirectories('*', $directOptions)
        }
        catch {
            Write-TidyLog -Level Warning -Message "Category '$Category' [Type=$Classification] encountered errors enumerating directories under '$resolvedPath': $($_.Exception.Message)"
            $immediateDirectories = @()
        }

        foreach ($directory in $immediateDirectories) {
            if ($null -eq $directory) {
                continue
            }

            $directoryStats[$directory.FullName] = [pscustomobject]@{
                SizeBytes    = [long]0
                LastModified = $directory.LastWriteTimeUtc
            }
        }
    }

    try {
        foreach ($file in $directoryInfo.EnumerateFiles('*', $allOptions)) {
            if ($null -eq $file) {
                continue
            }

            $fileCount++
            $size = [long]$file.Length
            $totalSize += $size

            if ($PreviewCount -gt 0 -and $ItemKind -ne 'Folders') {
                $extension = $file.Extension
                if ($null -ne $extension) {
                    $extension = $extension.ToLowerInvariant()
                }

                $filePreview = [pscustomobject]@{
                    Name         = $file.Name
                    FullName     = $file.FullName
                    SizeBytes    = $size
                    LastModified = $file.LastWriteTimeUtc
                    IsDirectory  = $false
                    Extension    = $extension
                }

                Add-TidyTopItem -Container $topFiles -Capacity $PreviewCount -Item $filePreview
            }

            if ($directoryStats.Count -gt 0) {
                $parentPath = $file.DirectoryName

                while ($parentPath -and -not $directoryStats.ContainsKey($parentPath)) {
                    $parentPath = [System.IO.Path]::GetDirectoryName($parentPath)
                }

                if ($parentPath -and $directoryStats.ContainsKey($parentPath)) {
                    $stat = $directoryStats[$parentPath]
                    $stat.SizeBytes = [long]$stat.SizeBytes + $size
                    if ($file.LastWriteTimeUtc -gt $stat.LastModified) {
                        $stat.LastModified = $file.LastWriteTimeUtc
                    }
                }
            }
        }
    }
    catch {
        Write-TidyLog -Level Warning -Message "Category '$Category' [Type=$Classification] encountered errors enumerating files under '$resolvedPath': $($_.Exception.Message)"
    }

    $directoryPreviewItems = @()
    if ($ItemKind -ne 'Files' -and $PreviewCount -gt 0 -and $directoryStats.Count -gt 0) {
        foreach ($entry in $directoryStats.GetEnumerator()) {
            $dirPath = $entry.Key
            $stat = $entry.Value
            if (-not $dirPath) {
                continue
            }

            $name = [System.IO.Path]::GetFileName($dirPath)
            if ([string]::IsNullOrWhiteSpace($name)) {
                $name = $directoryInfo.Name
            }

            $dirPreview = [pscustomobject]@{
                Name         = $name
                FullName     = $dirPath
                SizeBytes    = [long]$stat.SizeBytes
                LastModified = $stat.LastModified
                IsDirectory  = $true
                Extension    = $null
            }

            Add-TidyTopItem -Container $topDirectories -Capacity $PreviewCount -Item $dirPreview
        }

        if ($topDirectories.Count -gt 0) {
            $directoryPreviewItems = $topDirectories.ToArray() | Sort-Object -Property SizeBytes -Descending
        }
    }

    $filePreviewItems = @()
    if ($topFiles.Count -gt 0) {
        $filePreviewItems = $topFiles.ToArray() | Sort-Object -Property SizeBytes -Descending
    }

    switch ($ItemKind) {
        'Folders' { $itemCount = ($directoryStats.Keys).Count }
        'Both' { $itemCount = $fileCount + ($directoryStats.Keys).Count }
        default { $itemCount = $fileCount }
    }

    $previewItems = @()
    if ($PreviewCount -gt 0) {
        $combined = @()
        if ($filePreviewItems.Count -gt 0) {
            $combined += $filePreviewItems
        }
        if ($directoryPreviewItems.Count -gt 0) {
            $combined += $directoryPreviewItems
        }

        if ($combined.Count -gt 0) {
            $previewItems = $combined | Sort-Object -Property SizeBytes -Descending | Select-Object -First $PreviewCount
        }
    }

    return [pscustomobject]@{
        Category       = $Category
        Classification = $Classification
        Path           = $resolvedPath
        Exists         = $true
        ItemCount      = $itemCount
        TotalSizeBytes = [long]$totalSize
        DryRun         = $true
        Preview        = $previewItems
        Notes          = $effectiveNotes
    }
}

function Get-TidyDefinitionTargets {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Definition
    )

    $targets = @()

    if ($Definition.ContainsKey('Path') -and $Definition.Path) {
        $targets += [pscustomobject]@{
            Path  = $Definition.Path
            Label = $Definition.Name
            Notes = $Definition.Notes
        }
    }

    if ($Definition.ContainsKey('Resolve') -and $null -ne $Definition.Resolve) {
        try {
            $resolved = & $Definition.Resolve
        }
        catch {
            Write-TidyLog -Level Warning -Message "Definition '$($Definition.Name)' failed to resolve dynamic paths: $($_.Exception.Message)"
            $resolved = @()
        }

        foreach ($entry in $resolved) {
            if (-not $entry) {
                continue
            }

            if ($entry -is [string]) {
                $targets += [pscustomobject]@{
                    Path  = $entry
                    Label = $Definition.Name
                    Notes = $Definition.Notes
                }
                continue
            }

            if ($entry.PSObject.Properties['Path']) {
                $label = if ($entry.PSObject.Properties['Label'] -and -not [string]::IsNullOrWhiteSpace($entry.Label)) {
                    $entry.Label
                }
                else {
                    $Definition.Name
                }

                $notes = if ($entry.PSObject.Properties['Notes'] -and -not [string]::IsNullOrWhiteSpace($entry.Notes)) {
                    $entry.Notes
                }
                else {
                    $Definition.Notes
                }

                $targets += [pscustomobject]@{
                    Path  = $entry.Path
                    Label = $label
                    Notes = $notes
                }
            }
        }
    }

    return $targets
}

function Get-TidyCleanupDefinitions {
    param(
        [bool] $IncludeDownloads
    )

    $definitions = @(
        # ═══════════════════════════════════════════════════════════════════════════════
        # TEMP FILES
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Temp'; Name = 'User Temp'; Path = $env:TEMP; Notes = 'Temporary files generated for the current user.' },
        @{ Classification = 'Temp'; Name = 'Local AppData Temp'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Temp'); Notes = 'Local application temp directory for the current user.' },
        @{ Classification = 'Temp'; Name = 'Windows Temp'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Temp'); Notes = 'System-wide temporary files created by Windows.' },
        @{ Classification = 'Temp'; Name = 'Windows Prefetch'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Prefetch'); Notes = 'Prefetch hints used by Windows to speed up application launches.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # SYSTEM CACHE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Windows Update Cache'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'SoftwareDistribution\Download'); Notes = 'Cached Windows Update payloads that can be regenerated as needed.' },
        @{ Classification = 'Cache'; Name = 'Delivery Optimization Cache'; Path = 'C:\ProgramData\Microsoft\Network\Downloader'; Notes = 'Delivery Optimization cache for Windows Update and Store content.' },
        @{ Classification = 'Cache'; Name = 'Microsoft Store Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache'); Notes = 'Microsoft Store cached assets.' },
        @{ Classification = 'Cache'; Name = 'WinGet Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalCache'); Notes = 'WinGet package metadata and cache files.' },
        @{ Classification = 'Cache'; Name = 'NuGet HTTP Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'NuGet\Cache'); Notes = 'NuGet HTTP cache used by developer tooling.' },
        @{ Classification = 'Cache'; Name = 'DirectX Shader Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'D3DSCache'); Notes = 'Compiled DirectX shader cache generated by games and apps.' },
        @{ Classification = 'Cache'; Name = 'Windows Font Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'FontCache'); Notes = 'Font cache data regenerated automatically by Windows.' },
        @{ Classification = 'Cache'; Name = 'Legacy INet Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\INetCache'); Notes = 'Legacy browser/WebView cache files.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # THUMBNAIL & ICON CACHE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Explorer Thumbnail Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\Explorer'); Notes = 'Windows Explorer thumbnail cache files (thumbcache_*.db). Regenerated automatically.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # WINDOWS UPGRADE RESIDUE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Orphaned'; Name = 'Windows.old'; Path = 'C:\Windows.old'; Notes = 'Previous Windows installation. Can reclaim 10-30 GB after major updates.' },
        @{ Classification = 'Orphaned'; Name = 'Windows Update staging'; Path = 'C:\$Windows.~WS'; Notes = 'Windows Update staging folder from feature updates.' },
        @{ Classification = 'Orphaned'; Name = 'Windows Download staging'; Path = 'C:\$Windows.~BT'; Notes = 'Windows upgrade download and staging folder.' },
        @{ Classification = 'Orphaned'; Name = 'Windows Upgrade'; Path = 'C:\$WINDOWS.~Q'; Notes = 'Windows upgrade temporary files.' },
        @{ Classification = 'Orphaned'; Name = 'GetCurrent folder'; Path = 'C:\$GetCurrent'; Notes = 'Windows Update Assistant temporary folder.' },
        @{ Classification = 'Orphaned'; Name = 'SysReset Temp'; Path = 'C:\$SysReset'; Notes = 'System Reset temporary files.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # RECYCLE BIN
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Orphaned'; Name = 'Recycle Bin'; Resolve = {
                Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 } | ForEach-Object {
                    $recyclePath = Join-Path -Path $_.Root -ChildPath '$Recycle.Bin'
                    if (Test-Path -LiteralPath $recyclePath) {
                        [pscustomobject]@{
                            Path  = $recyclePath
                            Label = "Recycle Bin ($($_.Name):)"
                            Notes = 'Deleted files in Recycle Bin waiting to be permanently removed.'
                        }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # RECENT FILES & JUMP LISTS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'History'; Name = 'Recent files list'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\Recent'); Notes = 'List of recently opened files. Clears file access history.' },
        @{ Classification = 'History'; Name = 'Jump Lists (Automatic)'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\Recent\AutomaticDestinations'); Notes = 'Automatic jump list data for taskbar pins.' },
        @{ Classification = 'History'; Name = 'Jump Lists (Custom)'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\Recent\CustomDestinations'); Notes = 'Custom jump list data for frequently used items.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # WINDOWS AI / COPILOT / RECALL
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Windows Recall snapshots'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'CoreAIPlatform.00\UKP'); Notes = 'Windows Recall AI snapshots and screenshot data.' },
        @{ Classification = 'Cache'; Name = 'Windows Recall database'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'CoreAIPlatform.00'); Notes = 'Windows Recall AI database and metadata.' },
        @{ Classification = 'Cache'; Name = 'Copilot cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.Copilot_8wekyb3d8bbwe\LocalCache'); Notes = 'Windows Copilot application cache.' },
        @{ Classification = 'Cache'; Name = 'AI Host cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\AIHost'); Notes = 'Windows AI Host runtime cache.' },
        @{ Classification = 'Cache'; Name = 'Semantic Index'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\MicrosoftWindows.Client.AIX_cw5n1h2txyewy\LocalCache'); Notes = 'Windows Semantic Index AI cache.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - EDGE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Microsoft Edge Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Edge\User Data'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                $targets = @(
                    @{ SubPath = 'Cache'; Suffix = 'Cache'; Notes = 'Browser cache for Microsoft Edge profiles. Close Edge before cleaning.' },
                    @{ SubPath = 'Code Cache'; Suffix = 'Code Cache'; Notes = 'JavaScript bytecode cache for Microsoft Edge profiles.' },
                    @{ SubPath = 'GPUCache'; Suffix = 'GPU Cache'; Notes = 'GPU shader cache for Microsoft Edge profiles.' },
                    @{ SubPath = 'Service Worker\CacheStorage'; Suffix = 'Service Worker Cache'; Notes = 'Service Worker cache data for Microsoft Edge profiles.' }
                )

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $profileRoot = $_.FullName
                    $labelPrefix = if ($_.Name -eq 'Default') { 'Microsoft Edge (Default profile)' } else { "Microsoft Edge ($($_.Name))" }

                    foreach ($target in $targets) {
                        $candidate = Join-Path -Path $profileRoot -ChildPath $target.SubPath
                        if (Test-Path -LiteralPath $candidate) {
                            [pscustomobject]@{ Path = $candidate; Label = "$labelPrefix $($target.Suffix)"; Notes = $target.Notes }
                        }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - CHROME
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Google Chrome Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Google\Chrome\User Data'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                $targets = @(
                    @{ SubPath = 'Cache'; Suffix = 'Cache'; Notes = 'Browser cache for Google Chrome profiles. Close Chrome before cleaning.' },
                    @{ SubPath = 'Code Cache'; Suffix = 'Code Cache'; Notes = 'JavaScript bytecode cache for Google Chrome profiles.' },
                    @{ SubPath = 'GPUCache'; Suffix = 'GPU Cache'; Notes = 'GPU shader cache for Google Chrome profiles.' },
                    @{ SubPath = 'Service Worker\CacheStorage'; Suffix = 'Service Worker Cache'; Notes = 'Service Worker cache data for Google Chrome profiles.' }
                )

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'Default' -or $_.Name -like 'Profile *' -or $_.Name -like 'Guest Profile*' } |
                ForEach-Object {
                    $profileRoot = $_.FullName
                    $labelPrefix = if ($_.Name -eq 'Default') { 'Google Chrome (Default profile)' } else { "Google Chrome ($($_.Name))" }

                    foreach ($target in $targets) {
                        $candidate = Join-Path -Path $profileRoot -ChildPath $target.SubPath
                        if (Test-Path -LiteralPath $candidate) {
                            [pscustomobject]@{ Path = $candidate; Label = "$labelPrefix $($target.Suffix)"; Notes = $target.Notes }
                        }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - BRAVE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Brave Browser Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'BraveSoftware\Brave-Browser\User Data'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                $targets = @(
                    @{ SubPath = 'Cache'; Suffix = 'Cache'; Notes = 'Browser cache for Brave Browser profiles.' },
                    @{ SubPath = 'Code Cache'; Suffix = 'Code Cache'; Notes = 'JavaScript bytecode cache for Brave Browser.' },
                    @{ SubPath = 'GPUCache'; Suffix = 'GPU Cache'; Notes = 'GPU shader cache for Brave Browser.' },
                    @{ SubPath = 'Service Worker\CacheStorage'; Suffix = 'Service Worker Cache'; Notes = 'Service Worker cache for Brave Browser.' }
                )

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'Default' -or $_.Name -like 'Profile *' } |
                ForEach-Object {
                    $profileRoot = $_.FullName
                    $labelPrefix = if ($_.Name -eq 'Default') { 'Brave Browser (Default profile)' } else { "Brave Browser ($($_.Name))" }

                    foreach ($target in $targets) {
                        $candidate = Join-Path -Path $profileRoot -ChildPath $target.SubPath
                        if (Test-Path -LiteralPath $candidate) {
                            [pscustomobject]@{ Path = $candidate; Label = "$labelPrefix $($target.Suffix)"; Notes = $target.Notes }
                        }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - OPERA
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Opera Browser Cache'; Resolve = {
                $base = Join-Path -Path $env:APPDATA -ChildPath 'Opera Software\Opera Stable'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                $targets = @(
                    @{ SubPath = 'Cache'; Notes = 'Opera browser cache.' },
                    @{ SubPath = 'Code Cache'; Notes = 'Opera JavaScript bytecode cache.' },
                    @{ SubPath = 'GPUCache'; Notes = 'Opera GPU shader cache.' }
                )

                foreach ($target in $targets) {
                    $candidate = Join-Path -Path $base -ChildPath $target.SubPath
                    if (Test-Path -LiteralPath $candidate) {
                        [pscustomobject]@{ Path = $candidate; Label = "Opera $($target.SubPath)"; Notes = $target.Notes }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - VIVALDI
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Vivaldi Browser Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Vivaldi\User Data'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                $targets = @(
                    @{ SubPath = 'Cache'; Suffix = 'Cache'; Notes = 'Browser cache for Vivaldi profiles.' },
                    @{ SubPath = 'Code Cache'; Suffix = 'Code Cache'; Notes = 'JavaScript bytecode cache for Vivaldi.' },
                    @{ SubPath = 'GPUCache'; Suffix = 'GPU Cache'; Notes = 'GPU shader cache for Vivaldi.' }
                )

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'Default' -or $_.Name -like 'Profile *' } |
                ForEach-Object {
                    $profileRoot = $_.FullName
                    $labelPrefix = if ($_.Name -eq 'Default') { 'Vivaldi (Default profile)' } else { "Vivaldi ($($_.Name))" }

                    foreach ($target in $targets) {
                        $candidate = Join-Path -Path $profileRoot -ChildPath $target.SubPath
                        if (Test-Path -LiteralPath $candidate) {
                            [pscustomobject]@{ Path = $candidate; Label = "$labelPrefix $($target.Suffix)"; Notes = $target.Notes }
                        }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # BROWSER CACHES - FIREFOX
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Mozilla Firefox Cache'; Resolve = {
                $base = Join-Path -Path $env:APPDATA -ChildPath 'Mozilla\Firefox\Profiles'
                if (-not (Test-Path -LiteralPath $base)) { return @() }

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $cachePath = Join-Path -Path $_.FullName -ChildPath 'cache2'
                    if (Test-Path -LiteralPath $cachePath) {
                        [pscustomobject]@{ Path = $cachePath; Label = "Mozilla Firefox ($($_.Name))"; Notes = 'Firefox disk cache. Close Firefox before cleaning.' }
                    }
                    $thumbsPath = Join-Path -Path $_.FullName -ChildPath 'thumbnails'
                    if (Test-Path -LiteralPath $thumbsPath) {
                        [pscustomobject]@{ Path = $thumbsPath; Label = "Mozilla Firefox ($($_.Name)) thumbnails"; Notes = 'Firefox thumbnail cache.' }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # MICROSOFT TEAMS (CLASSIC)
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Microsoft Teams Cache'; Resolve = {
                $root = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Teams'
                if (-not (Test-Path -LiteralPath $root)) { return @() }

                $subFolders = @('Cache', 'Code Cache', 'GPUCache', 'databases', 'IndexedDB', 'Local Storage', 'blob_storage', 'Service Worker\CacheStorage')
                foreach ($subFolder in $subFolders) {
                    $candidate = Join-Path -Path $root -ChildPath $subFolder
                    if (Test-Path -LiteralPath $candidate) {
                        [pscustomobject]@{ Path = $candidate; Label = "Microsoft Teams ($subFolder)"; Notes = 'Microsoft Teams application caches. Close Teams before cleaning.' }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # MICROSOFT TEAMS (NEW / 2.0)
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'New Teams Cache'; Resolve = {
                $packagesRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages'
                if (-not (Test-Path -LiteralPath $packagesRoot)) { return @() }

                Get-ChildItem -LiteralPath $packagesRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'MSTeams_*' } |
                ForEach-Object {
                    $localCache = Join-Path -Path $_.FullName -ChildPath 'LocalCache'
                    if (Test-Path -LiteralPath $localCache) {
                        [pscustomobject]@{ Path = $localCache; Label = "New Teams ($($_.Name)) LocalCache"; Notes = 'New Microsoft Teams (2.0) local cache files.' }
                    }
                    $tempPath = Join-Path -Path $_.FullName -ChildPath 'AC\Temp'
                    if (Test-Path -LiteralPath $tempPath) {
                        [pscustomobject]@{ Path = $tempPath; Label = "New Teams ($($_.Name)) Temp"; Notes = 'New Microsoft Teams (2.0) temporary files.' }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # MESSAGING APPS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Slack Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Slack\Cache'); Notes = 'Slack desktop app cache. Close Slack before cleaning.' },
        @{ Classification = 'Cache'; Name = 'Slack Code Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Slack\Code Cache'); Notes = 'Slack JavaScript bytecode cache.' },
        @{ Classification = 'Logs'; Name = 'Slack logs'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Slack\logs'); Notes = 'Slack diagnostic logs.' },
        @{ Classification = 'Cache'; Name = 'Zoom data'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Zoom\data'); Notes = 'Zoom cached meeting data.' },
        @{ Classification = 'Logs'; Name = 'Zoom logs'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Zoom\logs'); Notes = 'Zoom meeting and diagnostic logs.' },
        @{ Classification = 'Cache'; Name = 'WhatsApp Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'WhatsApp\Cache'); Notes = 'WhatsApp desktop cache.' },
        @{ Classification = 'Cache'; Name = 'Discord Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'discord\Cache'); Notes = 'Discord cache files. Close Discord before cleaning.' },
        @{ Classification = 'Cache'; Name = 'Discord Code Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'discord\Code Cache'); Notes = 'Discord JavaScript cache.' },
        @{ Classification = 'Logs'; Name = 'Discord logs'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'discord\logs'); Notes = 'Discord diagnostic logs.' },
        @{ Classification = 'Cache'; Name = 'Telegram cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Telegram Desktop\tdata\user_data'); Notes = 'Telegram Desktop cached media.' },
        @{ Classification = 'Cache'; Name = 'Skype Cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Skype for Desktop\Cache'); Notes = 'Skype desktop cache files.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # DEVELOPER TOOLS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'VS Code cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Code\Cache'); Notes = 'Visual Studio Code disk cache.' },
        @{ Classification = 'Cache'; Name = 'VS Code cached data'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Code\CachedData'); Notes = 'Visual Studio Code cached metadata.' },
        @{ Classification = 'Cache'; Name = 'VS Code GPU cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Code\GPUCache'); Notes = 'Visual Studio Code GPU cache.' },
        @{ Classification = 'Cache'; Name = 'npm cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'npm-cache'); Notes = 'Node.js npm package cache. Safe to clear; packages will re-download.' },
        @{ Classification = 'Cache'; Name = 'Yarn cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Yarn\Cache'); Notes = 'Yarn package manager cache.' },
        @{ Classification = 'Cache'; Name = 'pnpm cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'pnpm-cache'); Notes = 'pnpm package manager store cache.' },
        @{ Classification = 'Cache'; Name = 'pip cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'pip\Cache'); Notes = 'Python pip package cache.' },
        @{ Classification = 'Logs'; Name = 'Docker logs'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Docker\log'); Notes = 'Docker Desktop logs.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # VISUAL STUDIO
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Visual Studio Cache'; Resolve = {
                $root = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\VisualStudio'
                if (-not (Test-Path -LiteralPath $root)) { return @() }

                Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $componentCache = Join-Path -Path $_.FullName -ChildPath 'ComponentModelCache'
                    if (Test-Path -LiteralPath $componentCache) {
                        [pscustomobject]@{ Path = $componentCache; Label = "Visual Studio $($_.Name) ComponentModelCache"; Notes = 'Component catalog cache regenerated on next launch.' }
                    }
                    $cache = Join-Path -Path $_.FullName -ChildPath 'Cache'
                    if (Test-Path -LiteralPath $cache) {
                        [pscustomobject]@{ Path = $cache; Label = "Visual Studio $($_.Name) Cache"; Notes = 'General Visual Studio cache data.' }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # JETBRAINS IDES
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'JetBrains Cache'; Resolve = {
                $root = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'JetBrains'
                if (-not (Test-Path -LiteralPath $root)) { return @() }

                Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $cachePath = Join-Path -Path $_.FullName -ChildPath 'caches'
                    if (Test-Path -LiteralPath $cachePath) {
                        [pscustomobject]@{ Path = $cachePath; Label = "$($_.Name) caches"; Notes = 'JetBrains IDE caches.' }
                    }
                    $logPath = Join-Path -Path $_.FullName -ChildPath 'log'
                    if (Test-Path -LiteralPath $logPath) {
                        [pscustomobject]@{ Path = $logPath; Label = "$($_.Name) logs"; Notes = 'JetBrains IDE logs.' }
                    }
                }
            }
        },

        # ═══════════════════════════════════════════════════════════════════════════════
        # GPU DRIVER CACHES
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'NVIDIA shader cache'; Path = 'C:\ProgramData\NVIDIA Corporation\NV_Cache'; Notes = 'Global NVIDIA shader cache.' },
        @{ Classification = 'Cache'; Name = 'NVIDIA DX cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'NVIDIA\DXCache'); Notes = 'DirectX shader cache used by NVIDIA drivers.' },
        @{ Classification = 'Cache'; Name = 'NVIDIA GL cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'NVIDIA\GLCache'); Notes = 'OpenGL shader cache used by NVIDIA drivers.' },
        @{ Classification = 'Cache'; Name = 'AMD DX cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'AMD\DxCache'); Notes = 'DirectX shader cache used by AMD drivers.' },
        @{ Classification = 'Cache'; Name = 'AMD GL cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'AMD\GLCache'); Notes = 'OpenGL shader cache used by AMD drivers.' },
        @{ Classification = 'Cache'; Name = 'AMD binary cache'; Path = 'C:\ProgramData\AMD'; Notes = 'AMD generated shader and installer cache.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # GAME LAUNCHERS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Steam HTML cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Steam\htmlcache'); Notes = 'Steam browser HTML cache.' },
        @{ Classification = 'Cache'; Name = 'Steam shader cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Steam\shadercache'); Notes = 'Steam shader cache compilation output.' },
        @{ Classification = 'Logs'; Name = 'Epic Games logs'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'EpicGamesLauncher\Saved\Logs'); Notes = 'Epic Games Launcher logs.' },
        @{ Classification = 'Cache'; Name = 'Epic Games webcache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'EpicGamesLauncher\Saved\webcache'); Notes = 'Epic Games Launcher web cache.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # MEDIA PLAYERS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'VLC art cache'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'vlc\art'); Notes = 'VLC media player album art cache.' },
        @{ Classification = 'Cache'; Name = 'Spotify cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Spotify\Storage'); Notes = 'Spotify music streaming cache.' },
        @{ Classification = 'Cache'; Name = 'Spotify data'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Spotify\Data'); Notes = 'Spotify cached data.' },
        @{ Classification = 'Cache'; Name = 'iTunes cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Apple Computer\iTunes'); Notes = 'iTunes media cache.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # ADOBE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Adobe cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Adobe'); Notes = 'Adobe application cache files.' },
        @{ Classification = 'Cache'; Name = 'Adobe roaming'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Adobe'); Notes = 'Adobe roaming application data.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # CLOUD STORAGE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Logs'; Name = 'OneDrive logs'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\OneDrive\logs'); Notes = 'Microsoft OneDrive sync client logs.' },
        @{ Classification = 'Logs'; Name = 'Google Drive logs'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Google\DriveFS\Logs'); Notes = 'Google Drive sync logs.' },
        @{ Classification = 'Cache'; Name = 'Dropbox cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Dropbox'); Notes = 'Dropbox local cache.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # WINDOWS SPOTLIGHT & LOCK SCREEN
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Windows Spotlight assets'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\Assets'); Notes = 'Windows Spotlight lock screen images. New images will download.' },
        @{ Classification = 'Cache'; Name = 'Windows Widgets cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\LocalCache'); Notes = 'Windows Widgets cached data.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # OFFICE & PRODUCTIVITY
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Office File Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Office\16.0\OfficeFileCache'); Notes = 'Microsoft 365 document cache.' },
        @{ Classification = 'Cache'; Name = 'Office WEF cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Office\16.0\Wef'); Notes = 'Web Extension Framework cache for Office add-ins.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # WINDOWS LOGS & ERROR REPORTS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Logs'; Name = 'Windows Error Reporting Queue'; Path = 'C:\ProgramData\Microsoft\Windows\WER\ReportQueue'; Notes = 'Queued Windows Error Reporting crash dumps and diagnostics.' },
        @{ Classification = 'Logs'; Name = 'Windows Error Reporting Archive'; Path = 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive'; Notes = 'Stored Windows Error Reporting results.' },
        @{ Classification = 'Logs'; Name = 'Windows Error Reporting Temp'; Path = 'C:\ProgramData\Microsoft\Windows\WER\Temp'; Notes = 'Temporary files generated by Windows Error Reporting.' },
        @{ Classification = 'Logs'; Name = 'Windows Update Logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Logs\WindowsUpdate'); Notes = 'Windows Update diagnostic logs.' },
        @{ Classification = 'Logs'; Name = 'CBS logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Logs\CBS'); Notes = 'Component-Based Servicing logs.' },
        @{ Classification = 'Logs'; Name = 'DISM logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Logs\DISM'); Notes = 'Deployment Image Servicing and Management logs.' },
        @{ Classification = 'Logs'; Name = 'MoSetup logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Logs\MoSetup'); Notes = 'Modern setup logs generated by feature updates.' },
        @{ Classification = 'Logs'; Name = 'Panther setup logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Panther'); Notes = 'Windows setup migration logs.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # CRASH DUMPS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Orphaned'; Name = 'User Crash Dumps'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'CrashDumps'); Notes = 'Application crash dump files created for troubleshooting.' },
        @{ Classification = 'Orphaned'; Name = 'System Crash Dumps'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Minidump'); Notes = 'System crash dump files.' },
        @{ Classification = 'Orphaned'; Name = 'ProgramData Crash Dumps'; Path = 'C:\ProgramData\CrashDumps'; Notes = 'Crash dumps for system services.' },
        @{ Classification = 'Orphaned'; Name = 'Live Kernel Reports'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'LiveKernelReports'); Notes = 'Live kernel reports and watchdog dumps.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # INSTALLER RESIDUE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Installer'; Name = 'Package Cache'; Path = 'C:\ProgramData\Package Cache'; Notes = 'Cached installer payloads left behind by setup engines.' },
        @{ Classification = 'Installer'; Name = 'Patch Cache'; Path = 'C:\ProgramData\Microsoft\Windows\Installer\$PatchCache$'; Notes = 'Windows Installer baseline cache used for patching.' },
        @{ Classification = 'Installer'; Name = 'User Package Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Package Cache'); Notes = 'Per-user package caches and installer logs.' },
        @{ Classification = 'Orphaned'; Name = 'Squirrel Installer Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'SquirrelTemp'); Notes = 'Residual setup artifacts from Squirrel-based installers.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # PERIPHERAL SOFTWARE
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Logs'; Name = 'Razer Synapse logs'; Path = 'C:\ProgramData\Razer\Synapse\Logs'; Notes = 'Razer Synapse peripheral software logs.' },
        @{ Classification = 'Cache'; Name = 'Razer cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Razer'); Notes = 'Razer software cache.' },
        @{ Classification = 'Cache'; Name = 'Logitech cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Logitech'); Notes = 'Logitech software cache.' },
        @{ Classification = 'Logs'; Name = 'Corsair iCUE logs'; Path = (Join-Path -Path $env:APPDATA -ChildPath 'Corsair\CUE\logs'); Notes = 'Corsair iCUE software logs.' },
        @{ Classification = 'Logs'; Name = 'SteelSeries logs'; Path = 'C:\ProgramData\SteelSeries\GG\logs'; Notes = 'SteelSeries GG software logs.' },

        # ═══════════════════════════════════════════════════════════════════════════════
        # UWP APPS
        # ═══════════════════════════════════════════════════════════════════════════════
        @{ Classification = 'Cache'; Name = 'Photos app cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.Windows.Photos_8wekyb3d8bbwe\LocalCache'); Notes = 'Windows Photos app cache.' },
        @{ Classification = 'Cache'; Name = 'Snipping Tool cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.ScreenSketch_8wekyb3d8bbwe\LocalCache'); Notes = 'Snipping Tool cache and temporary screenshots.' },
        @{ Classification = 'Cache'; Name = 'Calculator app cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.WindowsCalculator_8wekyb3d8bbwe\LocalCache'); Notes = 'Windows Calculator app cache.' },
        @{ Classification = 'Cache'; Name = 'Windows Maps cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.WindowsMaps_8wekyb3d8bbwe\LocalCache'); Notes = 'Windows Maps offline cache.' },
        @{ Classification = 'Cache'; Name = 'Weather app cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.BingWeather_8wekyb3d8bbwe\LocalCache'); Notes = 'Weather app cached data.' },
        @{ Classification = 'Cache'; Name = 'News app cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.BingNews_8wekyb3d8bbwe\LocalCache'); Notes = 'News app cached articles and images.' },
        @{ Classification = 'Cache'; Name = 'Cortana cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.549981C3F5F10_8wekyb3d8bbwe\LocalCache'); Notes = 'Cortana app cache.' }
    )

    if ($IncludeDownloads -and $env:USERPROFILE) {
        $downloadsPath = Join-Path -Path $env:USERPROFILE -ChildPath 'Downloads'
        $definitions += @{ Classification = 'Downloads'; Name = 'User Downloads'; Path = $downloadsPath; Notes = 'Files downloaded by the current user.' }
    }

    return $definitions
}

$definitions = Get-TidyCleanupDefinitions -IncludeDownloads:$IncludeDownloads

$reports = @()
$seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($definition in $definitions) {
    $targets = Get-TidyDefinitionTargets -Definition $definition
    foreach ($target in $targets) {
        if ([string]::IsNullOrWhiteSpace($target.Path)) {
            continue
        }

        $category = if ([string]::IsNullOrWhiteSpace($target.Label)) { $definition.Name } else { $target.Label }
        $classification = if ([string]::IsNullOrWhiteSpace($definition.Classification)) { 'Other' } else { $definition.Classification }

        $report = Get-TidyDirectoryReport -Category $category -Classification $classification -Path $target.Path -PreviewCount $PreviewCount -Notes $target.Notes -ItemKind $ItemKind

        if ($report.Exists -and $seenPaths.Contains($report.Path)) {
            Write-TidyLog -Level Information -Message "Skipping duplicate directory '$($report.Path)' for category '$category'."
            continue
        }

        if ($report.Exists) {
            $null = $seenPaths.Add($report.Path)
        }

        $reports += $report
    }
}

if ($reports.Count -gt 0) {
    $reports = $reports | Sort-Object -Property @{ Expression = 'Classification'; Descending = $false }, @{ Expression = 'TotalSizeBytes'; Descending = $true }
}

$aggregateSize = 0
if ($reports.Count -gt 0) {
    $aggregateSize = ($reports | Measure-Object -Property TotalSizeBytes -Sum).Sum
    if ($null -eq $aggregateSize) {
        $aggregateSize = 0
    }
}

Write-TidyLog -Level Information -Message ("Cleanup preview scan completed. Targets={0}, TotalSize={1}" -f $reports.Count, $aggregateSize)

$json = $reports | ConvertTo-Json -Depth 5 -Compress
Write-Output $json

return $reports

