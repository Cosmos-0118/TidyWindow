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

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
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
        @{ Classification = 'Temp'; Name = 'User Temp'; Path = $env:TEMP; Notes = 'Temporary files generated for the current user.' },
        @{ Classification = 'Temp'; Name = 'Local AppData Temp'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Temp'); Notes = 'Local application temp directory for the current user.' },
        @{ Classification = 'Temp'; Name = 'Windows Temp'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Temp'); Notes = 'System-wide temporary files created by Windows.' },
        @{ Classification = 'Temp'; Name = 'Windows Prefetch'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Prefetch'); Notes = 'Prefetch hints used by Windows to speed up application launches.' },

        @{ Classification = 'Cache'; Name = 'Windows Update Cache'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'SoftwareDistribution\Download'); Notes = 'Cached Windows Update payloads that can be regenerated as needed.' },
        @{ Classification = 'Cache'; Name = 'Delivery Optimization Cache'; Path = 'C:\ProgramData\Microsoft\Network\Downloader'; Notes = 'Delivery Optimization cache for Windows Update and Store content.' },
        @{ Classification = 'Cache'; Name = 'Microsoft Store Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache'); Notes = 'Microsoft Store cached assets.' },
        @{ Classification = 'Cache'; Name = 'WinGet Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalCache'); Notes = 'WinGet package metadata and cache files.' },
        @{ Classification = 'Cache'; Name = 'NuGet HTTP Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'NuGet\Cache'); Notes = 'NuGet HTTP cache used by developer tooling.' },

        @{ Classification = 'Cache'; Name = 'Microsoft Edge Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Edge\User Data'
                if (-not (Test-Path -LiteralPath $base)) {
                    return @()
                }

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
                            [pscustomobject]@{
                                Path  = $candidate
                                Label = "$labelPrefix $($target.Suffix)"
                                Notes = $target.Notes
                            }
                        }
                    }
                }
            }
        },
        @{ Classification = 'Cache'; Name = 'Google Chrome Cache'; Resolve = {
                $base = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Google\Chrome\User Data'
                if (-not (Test-Path -LiteralPath $base)) {
                    return @()
                }

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
                            [pscustomobject]@{
                                Path  = $candidate
                                Label = "$labelPrefix $($target.Suffix)"
                                Notes = $target.Notes
                            }
                        }
                    }
                }
            }
        },
        @{ Classification = 'Cache'; Name = 'Mozilla Firefox Cache'; Resolve = {
                $base = Join-Path -Path $env:APPDATA -ChildPath 'Mozilla\Firefox\Profiles'
                if (-not (Test-Path -LiteralPath $base)) {
                    return @()
                }

                Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $cachePath = Join-Path -Path $_.FullName -ChildPath 'cache2'
                    if (Test-Path -LiteralPath $cachePath) {
                        [pscustomobject]@{
                            Path  = $cachePath
                            Label = "Mozilla Firefox ($($_.Name))"
                            Notes = 'Firefox disk cache. Close Firefox before cleaning.'
                        }
                    }
                }
            }
        },
        @{ Classification = 'Cache'; Name = 'Microsoft Teams Cache'; Resolve = {
                $root = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Teams'
                if (-not (Test-Path -LiteralPath $root)) {
                    return @()
                }

                $subFolders = @('Cache', 'Code Cache', 'GPUCache', 'databases', 'IndexedDB', 'Local Storage', 'blob_storage', 'Service Worker\CacheStorage')
                foreach ($subFolder in $subFolders) {
                    $candidate = Join-Path -Path $root -ChildPath $subFolder
                    if (Test-Path -LiteralPath $candidate) {
                        [pscustomobject]@{
                            Path  = $candidate
                            Label = "Microsoft Teams ($subFolder)"
                            Notes = 'Microsoft Teams application caches. Close Teams before cleaning.'
                        }
                    }
                }
            }
        },

        @{ Classification = 'Logs'; Name = 'Windows Error Reporting Queue'; Path = 'C:\ProgramData\Microsoft\Windows\WER\ReportQueue'; Notes = 'Queued Windows Error Reporting crash dumps and diagnostics.' },
        @{ Classification = 'Logs'; Name = 'Windows Update Logs'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Logs\WindowsUpdate'); Notes = 'Windows Update diagnostic logs.' },
        @{ Classification = 'Logs'; Name = 'OneDrive Logs'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\OneDrive\logs'); Notes = 'Microsoft OneDrive sync client logs.' },

        @{ Classification = 'Orphaned'; Name = 'User Crash Dumps'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'CrashDumps'); Notes = 'Application crash dump files created for troubleshooting.' },
        @{ Classification = 'Orphaned'; Name = 'System Crash Dumps'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Minidump'); Notes = 'System crash dump files.' },
        @{ Classification = 'Orphaned'; Name = 'Squirrel Installer Cache'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'SquirrelTemp'); Notes = 'Residual setup artifacts from Squirrel-based installers.' }
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
