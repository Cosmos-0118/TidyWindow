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
    if (!(Test-Path -LiteralPath $root) -or !(Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) {
        $installer = Join-Path $job 'install-chocolatey.ps1'
        Download-Official 'https://community.chocolatey.org/install.ps1' $installer
        & $installer
        Assert-Native 'Installing Chocolatey'
    } else {
        # The official bootstrap refuses an existing directory. Its documented package installer
        # repairs binaries in place, leaving lib/, config/ and installed applications intact.
        $archive = Join-Path $job 'chocolatey.zip'
        Download-Official 'https://community.chocolatey.org/api/v2/package/chocolatey' $archive
        $unpacked = Join-Path $job 'chocolatey-package'
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $installer = Join-Path $unpacked 'tools\chocolateyInstall.ps1'
        if (!(Test-Path -LiteralPath $installer)) { throw 'The Chocolatey package did not contain its installation script.' }
        & $installer
        Assert-Native 'Repairing Chocolatey'
    }
