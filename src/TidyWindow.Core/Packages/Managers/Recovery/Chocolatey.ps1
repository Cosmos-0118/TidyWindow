    # Runs with administrator rights. The installation folder is taken from the PC-wide setting here, not from the unelevated app, and the account's
    # own setting must agree: choco.exe loads its helpers from wherever ChocolateyInstall points, and an account could point that at its own folder.
    if ($hardened) {
        $pcWide = [Environment]::GetEnvironmentVariable('ChocolateyInstall', 'Machine')
        if (-not $pcWide) { $pcWide = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'chocolatey' }
        $root = [IO.Path]::GetFullPath($pcWide).TrimEnd('\')
        $account = [Environment]::GetEnvironmentVariable('ChocolateyInstall', 'User')
        if ($account -and [IO.Path]::GetFullPath($account).TrimEnd('\') -ne $root) { throw "A ChocolateyInstall setting for this account points Chocolatey at $account, not at the PC-wide installation at $root. Remove that setting first; nothing was changed." }
        $profiles = Split-Path -Parent ([Environment]::GetFolderPath('UserProfile'))
        if ($root.StartsWith($profiles + '\', 'OrdinalIgnoreCase') -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw "The PC-wide ChocolateyInstall setting names $root, which is not a place Chocolatey can run from with administrator rights. Nothing was changed." }
    }
    $env:ChocolateyInstall = $root
    $config = Join-Path $root 'config\chocolatey.config'
    Backup-File $config 'chocolatey.config.backup'
    if (Test-Path -LiteralPath $config) {
        try { [xml](Get-Content -LiteralPath $config -Raw) | Out-Null }
        catch {
            # Retain malformed configuration for recovery; Chocolatey generates its default file.
            Move-Item -LiteralPath $config -Destination ($config + '.tidywindow-backup-' + [guid]::NewGuid().ToString('N'))
        }
    }
    # The current Chocolatey package serves both a fresh install and a repair: its own installer creates the folder when there is none and repairs the
    # binaries in place when there is, leaving lib/, config/ and installed applications intact. It comes from Chocolatey's own address for the latest release, so
    # a new Chocolatey needs no new TidyWindow; nothing from it runs until every file in it that can run has been checked for Chocolatey's signature.
    $archive = Join-Path $work 'chocolatey.nupkg'
    Download-Official 'https://community.chocolatey.org/api/v2/package/chocolatey' $archive
    $unpacked = Join-Path $work 'chocolatey-package'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpacked)
    $installer = Join-Path $unpacked 'tools\chocolateyInstall.ps1'
    foreach ($file in $installer, (Join-Path $unpacked 'tools\chocolateysetup.psm1'), (Join-Path $unpacked 'tools\chocolateyInstall\choco.exe')) {
        if (!(Test-Path -LiteralPath $file)) { throw 'The Chocolatey package did not contain its installation files.' }
    }
    Assert-ChocolateyPackageSigned $unpacked
    & $installer
    $choco = Join-Path $root 'bin\choco.exe'
    if (!(Test-Path -LiteralPath $choco)) { throw 'The Chocolatey setup finished, but choco.exe was not created.' }
    Assert-ChocolateySigned $choco
