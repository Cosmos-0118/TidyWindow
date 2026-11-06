#requires -Version 7
<#
.SYNOPSIS
  Decides what the CI workflow has to run for a change, from the rules in .github/ci/test-areas.json.

.DESCRIPTION
  Writes these outputs for the workflow (to $GITHUB_OUTPUT when it is set):
    build      'true' when the solution has to be built
    installer  'true' when the installer has to be built and smoke-tested
    tests      'true' when any test group has to run
    matrix     JSON for a job matrix: one entry per test group, with the xunit filter that selects only the areas that need to run

  The rules err toward running more: a file no rule recognises, or a run with no change list (a push to main, a manual run), runs everything.
  The check at the top makes a new test folder that is missing from the config fail here instead of never running.

.EXAMPLE
  ./scripts/ci/plan.ps1 -Base origin/main -Head HEAD          # what would a pull request from this branch run?
  ./scripts/ci/plan.ps1 -Files src/TidyWindow.Core/Crashes/X.cs # what would changing this file run?
#>
[CmdletBinding()]
param(
    [string]$Base,
    [string]$Head = 'HEAD',
    [string[]]$Files,
    [switch]$Everything,
    [string]$ConfigPath = '.github/ci/test-areas.json',
    [string]$TestRoot = 'tests/TidyWindow.Tests'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
$areas = $config.areas
# Optional keys get their defaults here, so the rest of the script can read them without checking.
foreach ($name in @($areas.Keys)) {
    foreach ($key in 'dependsOn', 'testsUse') { if (-not $areas[$name].ContainsKey($key)) { $areas[$name][$key] = @() } }
    if (-not $areas[$name].ContainsKey('source')) { $areas[$name]['source'] = @("src/TidyWindow.Core/$name/**") }
}

# ---- The config has to describe the tests that exist -----------------------------------------------------------------------------------------------------------
$problems = [System.Collections.Generic.List[string]]::new()
$folders = Get-ChildItem -LiteralPath $TestRoot -Directory | Where-Object Name -NotIn 'bin', 'obj', 'TestResults' | ForEach-Object Name
foreach ($folder in $folders) { if (-not $areas.ContainsKey($folder)) { $problems.Add("Test folder '$folder' is not an area in $ConfigPath, so its tests would never run on a pull request.") } }
foreach ($name in $areas.Keys) {
    if ($name -notin $folders) { $problems.Add("Area '$name' has no folder $TestRoot/$name.") }
    foreach ($dependency in @($areas[$name].dependsOn) + @($areas[$name].testsUse) | Where-Object { $_ }) { if (-not $areas.ContainsKey($dependency)) { $problems.Add("Area '$name' lists '$dependency', which is not an area.") } }
    $homes = @($config.groups.Keys | Where-Object { $name -in $config.groups[$_] })
    if ($homes.Count -ne 1) { $problems.Add("Area '$name' must be in exactly one group (it is in $($homes.Count)).") }
}
foreach ($group in $config.groups.Keys) { foreach ($name in $config.groups[$group]) { if (-not $areas.ContainsKey($name)) { $problems.Add("Group '$group' lists '$name', which is not an area.") } } }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host "::error::$_" }; throw "$ConfigPath does not match the tests." }

# ---- Which files changed ---------------------------------------------------------------------------------------------------------------------------------------
function ConvertTo-Regex([string]$glob) {
    $pattern = [regex]::Escape($glob).Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    [regex]::new("^$pattern$", 'IgnoreCase')
}
function Test-Glob([string]$path, $globs) { foreach ($glob in @($globs)) { if ((ConvertTo-Regex $glob).IsMatch($path)) { return $true } } $false }

$all = [bool]$Everything
if (-not $all) {
    if ($Files) { $changed = $Files }
    elseif ($Base) { $changed = git diff --name-only --no-renames "$Base...$Head"; if ($LASTEXITCODE -ne 0) { throw "git diff $Base...$Head failed." } }
    else { $all = $true }
}

$build = $false; $installer = $false
$selected = [System.Collections.Generic.HashSet[string]]::new()   # areas to run
$sourceChanged = [System.Collections.Generic.HashSet[string]]::new()
$testsChanged = [System.Collections.Generic.HashSet[string]]::new()
$why = [System.Collections.Generic.List[string]]::new()

if ($all) { $why.Add('No change list (push, schedule or manual run): everything runs.') }
else {
    foreach ($raw in @($changed | Where-Object { $_ })) {
        $path = $raw.Trim().Replace('\', '/')
        if (Test-Glob $path $config.ignore) { $why.Add("``$path``: ignored, nothing to run."); continue }
        if (Test-Glob $path $config.everything) { $all = $true; $why.Add("``$path``: affects the whole build, so everything runs."); continue }
        if (Test-Glob $path $config.installer) { $installer = $true; $why.Add("``$path``: installer only."); continue }
        if (Test-Glob $path $config.buildOnly) { $build = $true; $why.Add("``$path``: build only."); continue }

        $matched = $false
        foreach ($name in $areas.Keys) {
            if (Test-Glob $path $areas[$name].source) { [void]$sourceChanged.Add($name); $matched = $true }
            if (Test-Glob $path "$TestRoot/$name/**") { [void]$testsChanged.Add($name); $matched = $true }
        }
        if ($matched) { $build = $true; if ($path.StartsWith('src/')) { $installer = $true }; continue }

        $all = $true; $why.Add("``$path``: no rule covers it, so everything runs.")
    }
}

if ($all) { $build = $true; $installer = $true; $areas.Keys | ForEach-Object { [void]$selected.Add($_) } }
else {
    # A source change runs that area's tests and the tests of the areas that use it directly. It is not followed further: an area's own tests are where its
    # behaviour is checked, and anything the change breaks at build time is caught by the solution build. (Following every link makes Packages and
    # Infrastructure, which the rest of the code sits on, pull in nearly everything.) A push to main runs everything, so a miss here is caught after the merge.
    foreach ($name in $sourceChanged) { [void]$selected.Add($name) }
    foreach ($name in $areas.Keys) { if (@($areas[$name].dependsOn | Where-Object { $sourceChanged.Contains($_) }).Count -gt 0) { [void]$selected.Add($name) } }
    # A test change runs that area's tests and the tests of areas that borrow its test helpers.
    foreach ($name in $testsChanged) { [void]$selected.Add($name) }
    foreach ($name in $areas.Keys) { if (@($areas[$name].testsUse | Where-Object { $testsChanged.Contains($_) }).Count -gt 0) { [void]$selected.Add($name) } }
}

# ---- The xunit filter for each group that has something to run -------------------------------------------------------------------------------------------------
# A class is selected by name (the test's full name starts with namespace.Class), so the filter follows the folders without a list to keep in step.
function Get-Filter([string[]]$names) {
    $terms = [System.Collections.Generic.SortedSet[string]]::new()
    foreach ($name in $names) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $TestRoot $name) -Recurse -Filter *.cs) {
            $lines = Get-Content -LiteralPath $file.FullName
            $found = $lines | Select-String -Pattern '^namespace\s+([\w.]+)\s*[;{]' | Select-Object -First 1
            if (-not $found) { continue }
            $space = $found.Matches[0].Groups[1].Value
            foreach ($line in $lines) { if ($line -match '^(?:(?:public|internal|sealed|abstract|static|partial)\s+)*class\s+(\w+)') { [void]$terms.Add("FullyQualifiedName~$space.$($Matches[1])") } }
        }
    }
    if ($terms.Count -eq 0) { throw "No test classes found for: $($names -join ', ')." }
    $terms -join '|'
}

$include = foreach ($group in $config.groups.Keys) {
    $run = @($config.groups[$group] | Where-Object { $selected.Contains($_) })
    if ($run.Count -eq 0) { continue }
    [ordered]@{ name = $group; areas = ($run -join ', '); filter = (Get-Filter $run) }
}
$include = @($include)
$matrix = ConvertTo-Json -InputObject ([ordered]@{ include = $include }) -Compress -Depth 5

# ---- Report ----------------------------------------------------------------------------------------------------------------------------------------------------
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('### What this run does')
$lines.Add('')
$lines.Add("- Build the solution: **$($build.ToString().ToLower())**")
$lines.Add("- Build and smoke-test the installer: **$($installer.ToString().ToLower())**")
$lines.Add("- Test groups: " + $(if ($include.Count -eq 0) { '**none**' } else { ($include | ForEach-Object { "**$($_.name)** ($($_.areas))" }) -join ', ' }))
if ($why.Count -gt 0) { $lines.Add(''); $lines.Add('Why:'); $why | Select-Object -First 15 | ForEach-Object { $lines.Add("- $_") }; if ($why.Count -gt 15) { $lines.Add("- … and $($why.Count - 15) more") } }
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($env:GITHUB_OUTPUT) {
    @("build=$($build.ToString().ToLower())", "installer=$($installer.ToString().ToLower())", "tests=$(($include.Count -gt 0).ToString().ToLower())", "matrix=$matrix") | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
else { Write-Host "matrix=$($matrix.Length) characters of JSON (set GITHUB_OUTPUT to a file to read it)" }
