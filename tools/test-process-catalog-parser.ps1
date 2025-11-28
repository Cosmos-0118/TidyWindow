param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot "..\data\catalog\processes.catalog.json"),
    [string]$Configuration = "Debug"
)

$solutionRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $solutionRoot "src\TidyWindow.Core\TidyWindow.Core.csproj"
$assemblyPath = Join-Path $solutionRoot "src\TidyWindow.Core\bin\$Configuration\net8.0\TidyWindow.Core.dll"

if (-not (Test-Path $CatalogPath)) {
    throw "Catalog file not found: $CatalogPath"
}

if (-not (Test-Path $assemblyPath)) {
    Write-Host "Building TidyWindow.Core in $Configuration..." -ForegroundColor Cyan
    dotnet build $projectPath -c $Configuration | Out-Host

    if (-not (Test-Path $assemblyPath)) {
        throw "Failed to locate compiled assembly at $assemblyPath"
    }
}

Add-Type -Path $assemblyPath

$parser = [TidyWindow.Core.Processes.ProcessCatalogParser]::new($CatalogPath)
$snapshot = $parser.LoadSnapshot()

$expectedBackslashIdentifiers = @(
    '\\microsoft\\windows\\edgeupdate\\microsoftedgeupdatetaskmachinecore',
    '\\microsoft\\windows\\rds\\*'
)

$missing = @()
foreach ($identifier in $expectedBackslashIdentifiers) {
    $found = $snapshot.Entries | Where-Object { $_.Identifier -eq $identifier }
    if (-not $found) {
        $missing += $identifier
    }
}

if ($missing.Count -gt 0) {
    throw "Parser did not surface expected identifiers: $($missing -join ', ')"
}

Write-Host ("Parsed {0} catalog entries from {1}" -f $snapshot.Entries.Count, $CatalogPath) -ForegroundColor Green
Write-Host "Verified double-backslash identifiers from the catalog." -ForegroundColor Green

$fixturePath = Join-Path ([System.IO.Path]::GetTempPath()) "TidyWindow_CatalogFixture.json"
$fixtureJson = @"
{
    "entries": [
        {
            "identifier": "\\microsoft\\windows\\fixture",
            "displayName": "\\Microsoft\\Windows\\Fixture",
            "categoryKey": "M",
            "recommendedAction": "AutoStop",
            "risk": "Safe"
        }
        // Parser should accept this comment
    ]
}
"@

$fixtureJson | Set-Content -Path $fixturePath -Encoding UTF8
try {
    $fixtureParser = [TidyWindow.Core.Processes.ProcessCatalogParser]::new($fixturePath)
    $fixtureSnapshot = $fixtureParser.LoadSnapshot()
    if ($fixtureSnapshot.Entries.Count -ne 1) {
        throw "Fixture catalog did not parse correctly."
    }

    Write-Host "Fixture JSON with // comments parsed successfully." -ForegroundColor Green
}
finally {
    Remove-Item -Path $fixturePath -ErrorAction SilentlyContinue
}
