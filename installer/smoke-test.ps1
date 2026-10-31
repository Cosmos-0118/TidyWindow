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

# The files being present is not enough: a package without the compiled pages installs fine and then dies on start. A startup crash can also
# depend on timing (a missing [STAThread] failed only some of the time), so the app is started several times in a row.
$exe = Join-Path $dir 'TidyWindow.Next.exe'
$launches = 4
for ($n = 1; $n -le $launches; $n++) {
    $app = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 8
    if ($app.HasExited) { throw ("Launch $n of ${launches}: the installed app closed by itself within 8 seconds (exit code 0x{0:X8})." -f $app.ExitCode) }
    # Close the window the way a person would; killing is only for an app that ignores that.
    $app.CloseMainWindow() | Out-Null
    if (-not $app.WaitForExit(10000)) {
        Write-Warning "Launch ${n}: the app did not close when asked; ending it."
        Stop-Process -Id $app.Id -Force
        $app.WaitForExit(10000) | Out-Null
    }
    # 0 is a normal close; a crash ends with a status such as 0xC0000005 or 0xC000027B, which shows as a negative number.
    if ($app.ExitCode -lt 0) { throw ("Launch $n of ${launches}: the installed app crashed (exit code 0x{0:X8})." -f $app.ExitCode) }
    Write-Host "Launch $n of ${launches}: the installed app started, stayed open and closed."
}

$p = Start-Process -FilePath (Join-Path $dir 'unins000.exe') -ArgumentList $quiet -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Uninstall exited with $($p.ExitCode)." }

# The uninstaller removes its own file after it exits, so give it a moment.
for ($i = 0; $i -lt 30 -and (Test-Path (Join-Path $dir 'TidyWindow.Next.exe')); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path (Join-Path $dir 'TidyWindow.Next.exe')) { throw 'The app is still there after uninstalling.' }
Write-Host 'Uninstalled cleanly.'
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
