    # Never runs with administrator rights: Scoop belongs to the account. Downloads are pinned to a release and an installer commit, with their checksums.
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
    function Write-ScoopShim() {
        # What Scoop's installer writes for its own shim: three launchers that find the core next to them.
        $shims = Join-Path $root 'shims'
        New-Item -ItemType Directory -Path $shims -Force | Out-Null
        $shim = Join-Path $shims 'scoop'
        $absolute = Join-Path $core 'bin\scoop.ps1'
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText("$shim.ps1", (@("# $absolute", '$path = Join-Path $PSScriptRoot "..\apps\scoop\current\bin\scoop.ps1"', 'if ($MyInvocation.ExpectingInput) { $input | & $path @args } else { & $path @args }', 'exit $LASTEXITCODE') -join "`r`n"), $utf8)
        [IO.File]::WriteAllText("$shim.cmd", (@("@rem $absolute", '@echo off', 'setlocal enabledelayedexpansion', 'set args=%*', ':: replace problem characters in arguments', 'set args=%args:"=''%', 'set args=%args:(=`(%', 'set args=%args:)=`)%', 'set invalid="=''', 'if !args! == !invalid! ( set args= )', 'where /q pwsh.exe', 'if %errorlevel% equ 0 (', "    pwsh -noprofile -ex unrestricted -file `"$absolute`" %args%", ') else (', "    powershell -noprofile -ex unrestricted -file `"$absolute`" %args%", ')') -join "`r`n"), $utf8)
        [IO.File]::WriteAllText($shim, (@('#!/bin/sh', "# $absolute", 'if command -v pwsh.exe > /dev/null 2>&1; then', "    pwsh.exe -noprofile -ex unrestricted -file `"$absolute`" `"`$@`"", 'else', "    powershell.exe -noprofile -ex unrestricted -file `"$absolute`" `"`$@`"", 'fi') -join "`n"), $utf8)
    }
    if ($problem -eq 'NotInstalled' -and !(Test-Path -LiteralPath $core)) {
        $installer = Join-Path $work 'install-scoop.ps1'
        # Scoop's own documented installer, from its official repository. Scoop belongs to the account and never runs with administrator rights, so this is the same
        # trust as installing it by hand, and a new Scoop needs no new TidyWindow.
        Download-Official 'https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1' $installer
        # In its own host: the installer ends with exit when it refuses (already installed, running as administrator), which must not end this recovery.
        & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $installer -ScoopDir $root
        Assert-Native 'The Scoop installer'
        if (!(Test-Path -LiteralPath $entryPoint)) { throw 'The Scoop installer finished, but Scoop was not created.' }
    } else {
        $coreWorks = $false
        if (Test-Path -LiteralPath $entryPoint) {
            # A separate host keeps a damaged entry point's exit statement from ending recovery.
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint --version
            $coreWorks = $LASTEXITCODE -eq 0
        }
        if (!$coreWorks) {
            $archive = Join-Path $work 'scoop.zip'
            # The latest release, asked of GitHub: its "latest" address points at the tag, and only a plain version tag is accepted.
            $release = Get-Redirect 'https://github.com/ScoopInstaller/Scoop/releases/latest'
            if ($release -notmatch '^https://github\.com/ScoopInstaller/Scoop/releases/tag/(v\d+\.\d+\.\d+)$') { throw "GitHub did not name a Scoop release: $release" }
            Download-Official "https://github.com/ScoopInstaller/Scoop/archive/refs/tags/$($Matches[1]).zip" $archive
            $unpacked = Join-Path $work 'scoop-core'
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpacked)
            # The archive holds one folder, named after the release.
            $folders = @(Get-ChildItem -LiteralPath $unpacked -Directory -Force)
            if ($folders.Count -ne 1) { throw 'The Scoop archive did not contain a single Scoop folder.' }
            $replacement = $folders[0].FullName
            if (!(Test-Path -LiteralPath (Join-Path $replacement 'bin\scoop.ps1'))) { throw 'The Scoop archive did not contain a valid entry point.' }
            New-Item -ItemType Directory -Path (Split-Path -Parent $core) -Force | Out-Null
            # Move only Scoop's own core; never remove apps/, buckets/, persist/ or caches. A link in its place could make the move act elsewhere, so only a real folder is moved aside.
            if (Test-Path -LiteralPath $core) {
                if ((Get-Item -LiteralPath $core -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Scoop's core folder is a link, which this repair does not replace: $core" }
                Move-Item -LiteralPath $core -Destination ($core + '.tidywindow-backup-' + [guid]::NewGuid().ToString('N'))
            }
            Move-Item -LiteralPath $replacement -Destination $core
        }
        Write-ScoopShim
        if ($problem -eq 'Source') {
            # The main bucket is additive; existing custom buckets stay intact.
            & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint bucket add main
            if ($LASTEXITCODE -ne 0) {
                & $powershell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $entryPoint update
                Assert-Native 'Updating Scoop and its buckets'
            }
        }
    }
