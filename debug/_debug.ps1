$command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
if (-not $command) { throw 'scoop not found' }
$exe = if ($command.Source) { $command.Source } else { 'scoop' }
$output = & $exe 'info' 'openjdk24' 2>$null
foreach ($line in $output) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    Write-Host "LINE: [$line]"
    if ($line -match '^Installed\s*:\s*(?<ver>.+)$') {
        Write-Host "MATCH: $($matches['ver'])"
    }
}
