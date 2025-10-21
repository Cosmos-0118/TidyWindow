param(
    [string]$RootPath,
    [int]$Top = 25,
    [int]$MinSizeMB = 200,
    [switch]$IncludeHidden
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

if (-not $PSBoundParameters.ContainsKey('RootPath') -or [string]::IsNullOrWhiteSpace($RootPath)) {
    $RootPath = $env:USERPROFILE
}

try {
    $resolvedRoot = (Resolve-Path -LiteralPath $RootPath -ErrorAction Stop).ProviderPath
}
catch {
    throw "Unable to resolve root path '$RootPath'. $_"
}

$minimumSizeBytes = [math]::Max(0, $MinSizeMB) * 1MB

$enumerationOptions = @{
    Path = $resolvedRoot
    Recurse = $true
    File = $true
    ErrorAction = 'SilentlyContinue'
}

if ($IncludeHidden.IsPresent) {
    $enumerationOptions.Force = $true
}

$files = Get-ChildItem @enumerationOptions |
    Where-Object { $_.Length -ge $minimumSizeBytes } |
    Sort-Object -Property Length -Descending |
    Select-Object -First $Top

$totalSize = 0
$totalCount = 0
$findings = @()

foreach ($file in $files) {
    $totalSize += $file.Length
    $totalCount++

    $findings += [pscustomobject]@{
        Path = $file.FullName
        Directory = $file.DirectoryName
        Name = $file.Name
        Extension = $file.Extension
        SizeBytes = [long]$file.Length
        Modified = $file.LastWriteTimeUtc
    }
}

$result = [pscustomobject]@{
    RootPath = $resolvedRoot
    GeneratedAt = (Get-Date).ToUniversalTime()
    TotalCandidates = $totalCount
    TotalSizeBytes = [long]$totalSize
    Findings = $findings
}

$result | ConvertTo-Json -Depth 6 -Compress
