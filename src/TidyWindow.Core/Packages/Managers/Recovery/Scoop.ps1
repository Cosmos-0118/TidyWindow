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
        Download-Official 'https://raw.githubusercontent.com/ScoopInstaller/Install/1e2f334083d609986d8c8bc9e31ae8e87c39fab4/install.ps1' $installer '94F983B190438311E006B957DB7C8422709E0BA62A6C2AC04E278164108F2512'
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
            Download-Official 'https://github.com/ScoopInstaller/Scoop/archive/refs/tags/v0.6.0.zip' $archive '77FA70F3133C70A7B17698235130767C3301D072C7BA2DEAA2C939014E380875'
            $unpacked = Join-Path $work 'scoop-core'
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpacked)
            $replacement = Join-Path $unpacked 'Scoop-0.6.0'
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
