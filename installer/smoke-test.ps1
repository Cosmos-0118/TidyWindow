# Installs the setup silently into a scratch folder, checks the app and both helpers arrived, starts the app a few times, uninstalls and
# checks it is gone. Whatever happens on the way, the finally block removes the install and the scratch folder again.
#   pwsh installer/smoke-test.ps1 -Setup artifacts/TidyWindow-Setup-0.0.0.exe
# Setup installs for all users unless told otherwise, so run this from an administrator window (CI runners are one).
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'

$dir = Join-Path ([IO.Path]::GetTempPath()) ('tidywindow-smoke-' + [Guid]::NewGuid().ToString('N'))
$quiet = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-'
$exe = Join-Path $dir 'TidyWindow.Next.exe'
$uninstaller = Join-Path $dir 'unins000.exe'

# Runs Setup or the uninstaller and waits, but not forever: a hung installer fails this check instead of the job's timeout ending it.
function Invoke-Installer([string]$Path, [string[]]$Arguments, [string]$What, [int]$TimeoutSeconds = 300) {
    $p = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        throw "$What did not finish within $TimeoutSeconds seconds."
    }
    if ($p.ExitCode -ne 0) { throw "$What exited with $($p.ExitCode)." }
}

# The uninstaller deletes its own file after it exits, so give it a moment before looking.
function Wait-Gone([string]$Path, [int]$TimeoutSeconds = 30) {
    for ($i = 0; $i -lt $TimeoutSeconds -and (Test-Path $Path); $i++) { Start-Sleep -Seconds 1 }
    return -not (Test-Path $Path)
}

try {
    Invoke-Installer (Resolve-Path $Setup).Path ($quiet + "/DIR=`"$dir`"") 'Setup'
    foreach ($path in 'TidyWindow.Next.exe', 'TidyWindow.Next.pri', 'Shell\MainWindow.xbf', 'CleanHost\TidyWindow.CleanHost.exe', 'ScoutHost\TidyWindow.SearchHost.exe', 'unins000.exe') {
        if (-not (Test-Path (Join-Path $dir $path))) { throw "Not installed: $path" }
    }
    Write-Host "Installed to $dir"

    # The files being present is not enough: a package without the compiled pages installs fine and then dies on start. A startup crash
    # can also depend on timing (a missing [STAThread] failed only some of the time), so the app is started several times in a row.
    $launches = 4
    # The app declines to start with a full administrator token (User Account Control on, and this console elevated), and would sit on its message
    # looking like a window that stayed open. GitHub's Windows runners have User Account Control off, so there the launches below do run.
    Add-Type -Namespace TidyWindowSmoke -Name Token -MemberDefinition '[DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);'
    $elevationType = 0; $returned = 0
    [void][TidyWindowSmoke.Token]::GetTokenInformation([Security.Principal.WindowsIdentity]::GetCurrent().Token, 18, [ref]$elevationType, 4, [ref]$returned)
    if ($elevationType -eq 2) {
        $launches = 0
        Write-Warning 'This console is elevated, so the app would decline to start: the launch checks were skipped.'
        if ($env:GITHUB_ACTIONS) { Write-Host '::warning::The launch checks were skipped because the runner has a full administrator token.' }
    }
    for ($n = 1; $n -le $launches; $n++) {
        $app = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
        Start-Sleep -Seconds 8
        if ($app.HasExited) { throw ("Launch $n of ${launches}: the installed app closed by itself within 8 seconds (exit code 0x{0:X8})." -f $app.ExitCode) }
        # Close the window the way a person would. An app that ignores that is ended by force, and that is a failure of its own kind,
        # reported as such rather than as the crash its exit code would suggest.
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(10000)) {
            Stop-Process -Id $app.Id -Force
            if (-not $app.WaitForExit(10000)) { throw "Launch $n of ${launches}: the installed app did not close when asked and could not be ended." }
            throw "Launch $n of ${launches}: the installed app did not close when asked and had to be ended by force."
        }
        # 0 is a normal close. A crash ends with a status such as 0xC0000005 or 0xC000027B (shown as a negative number); any other
        # non-zero code is an error exit, and both fail the check.
        if ($app.ExitCode -ne 0) { throw ("Launch $n of ${launches}: the installed app ended with exit code 0x{0:X8} instead of closing normally." -f $app.ExitCode) }
        Write-Host "Launch $n of ${launches}: the installed app started, stayed open and closed."
    }

    Invoke-Installer $uninstaller $quiet 'Uninstall'
    if (-not (Wait-Gone $exe)) { throw 'The app is still there after uninstalling.' }
    Write-Host 'Uninstalled cleanly.'
}
finally {
    # Leave nothing behind after a failure: no app process, no registered install, no scratch folder.
    foreach ($process in Get-Process -Name 'TidyWindow.Next', 'TidyWindow.CleanHost', 'TidyWindow.SearchHost' -ErrorAction SilentlyContinue) {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($path -and $path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path $uninstaller) {
        try { Invoke-Installer $uninstaller $quiet 'Uninstall (cleanup after a failure)' } catch { Write-Warning "Cleanup: $_" }
        Wait-Gone $exe | Out-Null
    }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
