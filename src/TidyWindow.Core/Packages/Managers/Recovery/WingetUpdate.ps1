    # Brings a working WinGet to the newest release. Its own script, apart from Winget.ps1: that one also runs with administrator rights, and everything
    # in a recovery travels on a command line of fixed length.
    #
    # WinGet is the App Installer package. It is never asked to upgrade itself: Windows would have to stop the very winget.exe that asked. This script runs in
    # its own process, so the package is free to be replaced. The newest release comes from Microsoft's own repository, and nothing is installed until the
    # bundle is byte-for-byte the file the release publishes a SHA-256 for and names Microsoft's App Installer as its identity and publisher; Windows itself
    # then refuses a package whose signature is not valid for that publisher.
    # Appx registration is available in Windows PowerShell, not all PowerShell 7 hosts.
    Import-Module "$PSHOME\Modules\Appx\Appx.psd1"
    function Assert-AppInstallerBundle([string]$path) {
        $zip = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entry = $zip.GetEntry('AppxMetadata/AppxBundleManifest.xml')
            if (-not $entry) { throw 'The App Installer bundle has no manifest.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        $identity = $manifest.Bundle.Identity
        if ($identity.Name -ne 'Microsoft.DesktopAppInstaller' -or $identity.Publisher -notlike 'CN=Microsoft Corporation,*') { throw "The download is not Microsoft's App Installer bundle ($($identity.Name) from $($identity.Publisher)), so it was not installed." }
    }
    $updated = $false
    try {
        $release = Get-Redirect 'https://github.com/microsoft/winget-cli/releases/latest'
        if ($release -notmatch '^https://github\.com/microsoft/winget-cli/releases/tag/(v\d+\.\d+\.\d+)$') { throw "GitHub did not name a WinGet release: $release" }
        $address = "https://github.com/microsoft/winget-cli/releases/download/$($Matches[1])/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe"
        $digestFile = Join-Path $work 'AppInstaller.txt'
        Download-Official "$address.txt" $digestFile
        $digest = [regex]::Match((Get-Content -LiteralPath $digestFile -Raw), '(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])').Value
        if (-not $digest) { throw 'The release did not publish a SHA-256 for the App Installer bundle.' }
        $bundle = Join-Path $work 'AppInstaller.msixbundle'
        Download-Official "$address.msixbundle" $bundle $digest
        Assert-AppInstallerBundle $bundle
        Add-AppxPackage -Path $bundle -ForceApplicationShutdown -ErrorAction Stop
        $updated = $true
    } catch {
        # A missing framework package, an offline address or a bundle that did not check out all end here: Microsoft's own recovery module is the way on.
        Write-Output "Installing the App Installer bundle directly did not work ($($_.Exception.Message)); using Microsoft recovery."
    }
    if (!$updated) {
        $module = Get-Module -ListAvailable -Name Microsoft.WinGet.Client | Sort-Object Version -Descending | Select-Object -First 1
        if (!$module) {
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
            Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Scope CurrentUser -Force -AllowClobber -Confirm:$false
        }
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Repair-WinGetPackageManager -Latest -Force -ErrorAction Stop
    }
