# Installs the setup silently into a scratch folder, checks the app and both helpers arrived, then uninstalls and checks it is gone.
#   pwsh installer/smoke-test.ps1 -Setup artifacts/TidyWindow-Setup-0.0.0.exe
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'

$dir = Join-Path ([IO.Path]::GetTempPath()) ('tidywindow-smoke-' + [Guid]::NewGuid().ToString('N'))
$quiet = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-'

$p = Start-Process -FilePath (Resolve-Path $Setup).Path -ArgumentList ($quiet + "/DIR=`"$dir`"") -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Setup exited with $($p.ExitCode)." }

foreach ($path in 'TidyWindow.Next.exe', 'TidyWindow.Next.pri', 'MainWindow.xbf', 'CleanHost\TidyWindow.CleanHost.exe', 'ScoutHost\TidyWindow.SearchHost.exe', 'unins000.exe') {
    if (-not (Test-Path (Join-Path $dir $path))) { throw "Not installed: $path" }
}
Write-Host "Installed to $dir"

# The files being present is not enough: a package without the compiled pages installs fine and then dies on start.
$app = Start-Process -FilePath (Join-Path $dir 'TidyWindow.Next.exe') -WorkingDirectory $dir -PassThru
Start-Sleep -Seconds 12
if ($app.HasExited) { throw ("The installed app closed by itself within 12 seconds (exit code 0x{0:X8})." -f $app.ExitCode) }
Write-Host 'The installed app started and stayed open.'
Stop-Process -Id $app.Id -Force
$app.WaitForExit(10000) | Out-Null

$p = Start-Process -FilePath (Join-Path $dir 'unins000.exe') -ArgumentList $quiet -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Uninstall exited with $($p.ExitCode)." }

# The uninstaller removes its own file after it exits, so give it a moment.
for ($i = 0; $i -lt 30 -and (Test-Path (Join-Path $dir 'TidyWindow.Next.exe')); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path (Join-Path $dir 'TidyWindow.Next.exe')) { throw 'The app is still there after uninstalling.' }
Write-Host 'Uninstalled cleanly.'
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
