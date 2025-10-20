$command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
if (-not $command) { throw 'scoop not found' }
$exe = if ($command.Source) { $command.Source } else { 'scoop' }
$output = & $exe 'list' 2>$null
foreach ($item in $output) {
    $typeName = if ($item) { $item.GetType().FullName } else { '<null>' }
    Write-Host "TYPE: $typeName | VALUE: $item"
}
