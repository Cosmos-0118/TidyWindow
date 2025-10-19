param(
    [int] $PreviewCount = 10,
    [bool] $IncludeDownloads = $false
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

Write-TidyLog -Level Information -Message "Starting cleanup preview scan (PreviewCount=$PreviewCount, IncludeDownloads=$IncludeDownloads)."

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
        [string] $Path,
        [int] $PreviewCount
    )

    $resolvedPath = Resolve-TidyPath -Path $Path
    if (-not $resolvedPath) {
        Write-TidyLog -Level Warning -Message "Category '$Category' has an invalid path definition: '$Path'."
        return [pscustomobject]@{
            Category       = $Category
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
        Write-TidyLog -Level Information -Message "Category '$Category' path '$resolvedPath' does not exist."
        return [pscustomobject]@{
            Category       = $Category
            Path           = $resolvedPath
            Exists         = $false
            ItemCount      = 0
            TotalSizeBytes = 0
            DryRun         = $true
            Preview        = @()
            Notes          = 'No directory located.'
        }
    }

    $fileItems = @()
    try {
        $fileItems = Get-ChildItem -LiteralPath $resolvedPath -Force -Recurse -ErrorAction Stop -File
    }
    catch {
        Write-TidyLog -Level Warning -Message "Category '$Category' encountered errors enumerating '$resolvedPath': $($_.Exception.Message)"
        try {
            $fileItems = Get-ChildItem -LiteralPath $resolvedPath -Force -Recurse -ErrorAction SilentlyContinue -File
        }
        catch {
            $fileItems = @()
        }
    }

    $itemCount = $fileItems.Count
    $totalSize = 0

    if ($itemCount -gt 0) {
        $totalSize = ($fileItems | Measure-Object -Property Length -Sum).Sum
    }

    if ($null -eq $totalSize) {
        $totalSize = 0
    }

    $previewItems = @()
    if ($itemCount -gt 0 -and $PreviewCount -gt 0) {
        $previewItems = $fileItems |
        Sort-Object -Property Length -Descending |
        Select-Object -First $PreviewCount |
        ForEach-Object {
            [pscustomobject]@{
                Name         = $_.Name
                FullName     = $_.FullName
                SizeBytes    = [long]$_.Length
                LastModified = $_.LastWriteTimeUtc
            }
        }
    }

    return [pscustomobject]@{
        Category       = $Category
        Path           = $resolvedPath
        Exists         = $true
        ItemCount      = $itemCount
        TotalSizeBytes = [long]$totalSize
        DryRun         = $true
        Preview        = $previewItems
        Notes          = 'Dry run only. No files were deleted.'
    }
}

$candidates = @(
    @{ Category = 'UserTemp'; Path = $env:TEMP },
    @{ Category = 'LocalAppDataTemp'; Path = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Temp') },
    @{ Category = 'WindowsTemp'; Path = (Join-Path -Path $env:WINDIR -ChildPath 'Temp') }
)

if ($IncludeDownloads -and $env:USERPROFILE) {
    $downloadsPath = Join-Path -Path $env:USERPROFILE -ChildPath 'Downloads'
    $candidates += @{ Category = 'UserDownloads'; Path = $downloadsPath }
}

$reports = @()
$seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($candidate in $candidates) {
    $report = Get-TidyDirectoryReport -Category $candidate.Category -Path $candidate.Path -PreviewCount $PreviewCount

    if ($report.Exists -and $seenPaths.Contains($report.Path)) {
        Write-TidyLog -Level Information -Message "Skipping duplicate directory '$($report.Path)' for category '$($candidate.Category)'."
        continue
    }

    if ($report.Exists) {
        $null = $seenPaths.Add($report.Path)
    }

    $reports += $report
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
