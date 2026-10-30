    $env:SCOOP = $root
    $core = Join-Path $root 'apps\scoop\current'
    $entryPoint = Join-Path $core 'bin\scoop.ps1'
    Backup-File $configuration 'scoop-config.json.backup'
    if ($configuration -and (Test-Path -LiteralPath $configuration)) {
        try { Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null }
        catch {
            Move-Item -LiteralPath $configuration -Destination ($configuration + '.tidywindow-backup-' + [guid]::NewGuid().ToString('N'))
            @{ root_path = $root } | ConvertTo-Json | Set-Content -LiteralPath $configuration -Encoding UTF8
        }
    }
    $installer = Join-Path $job 'install-scoop.ps1'
    Download-Official 'https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1' $installer
    $env:SCOOP_NOINSTALL = 'true'
    # Official installer supports loading its functions without running an installation.
    . $installer -ScoopDir $root
    if ($problem -eq 'NotInstalled' -and !(Test-Path -LiteralPath $core)) {
        Install-Scoop
    } else {
        $coreWorks = $false
        if (Test-Path -LiteralPath $entryPoint) {
            # A separate host keeps a damaged entry point's exit statement from ending recovery.
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint --version
            $coreWorks = $LASTEXITCODE -eq 0
        }
        if (!$coreWorks) {
            $archive = Join-Path $job 'scoop.zip'
            Download-Official 'https://github.com/ScoopInstaller/Scoop/archive/refs/heads/master.zip' $archive
            $unpacked = Join-Path $job 'scoop-core'
            Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
            $replacement = Join-Path $unpacked 'Scoop-master'
            if (!(Test-Path -LiteralPath (Join-Path $replacement 'bin\scoop.ps1'))) { throw 'The Scoop archive did not contain a valid entry point.' }
            New-Item -ItemType Directory -Path (Split-Path -Parent $core) -Force | Out-Null
            # Move only Scoop's own core; never remove apps/, buckets/, persist/ or caches.
            if (Test-Path -LiteralPath $core) {
                $resolvedCore = [IO.Path]::GetFullPath($core)
                if ($resolvedCore -ne [IO.Path]::GetFullPath((Join-Path $root 'apps\scoop\current'))) { throw 'Unexpected Scoop core path.' }
                Move-Item -LiteralPath $core -Destination ($core + '.tidywindow-backup-' + [guid]::NewGuid().ToString('N'))
            }
            Move-Item -LiteralPath $replacement -Destination $core
        }
        Import-ScoopShim
        if ($problem -eq 'Source') {
            # The main bucket is additive; existing custom buckets stay intact.
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint bucket add main
            if ($LASTEXITCODE -ne 0) {
                & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint update
                Assert-Native 'Updating Scoop and its buckets'
            }
        }
    }
