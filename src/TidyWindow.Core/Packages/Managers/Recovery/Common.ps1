# Shared by every recovery script. The app prepends the variables ($root, $configuration, $job, $problem, $executable, $powershell) and, for a run
# with administrator rights, Elevated.ps1; it appends <Kind>.ps1 and Complete.ps1. The whole thing travels as one encoded command, so full-line
# comments and indentation are stripped before sending.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$env:NO_COLOR = '1'
# Git must fail at once on a repository that wants a sign-in, not wait on a prompt nobody can see or open a sign-in window.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'
$exitCode = 1
$failure = $null
$work = $null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Test-OfficialAddress([uri]$uri) {
    return $uri.Scheme -eq 'https' -and $uri.Host -in @('community.chocolatey.org', 'packages.chocolatey.org', 'raw.githubusercontent.com', 'github.com', 'codeload.github.com', 'objects.githubusercontent.com')
}
function Download-Official([string]$uri, [string]$destination, [string]$sha256) {
    # Every hop of a redirect is checked, not only the first address, and the file is only kept when it is byte-for-byte the one this build expects.
    for ($hop = 0; $hop -le 5; $hop++) {
        if (-not (Test-OfficialAddress $uri)) { throw "Unexpected download address: $uri" }
        $request = [Net.HttpWebRequest]::Create($uri)
        $request.AllowAutoRedirect = $false
        $request.Timeout = 90000
        $request.ReadWriteTimeout = 90000
        $request.UserAgent = 'TidyWindow'
        try { $response = $request.GetResponse() } catch [Net.WebException] { if ($_.Exception.Response) { $response = $_.Exception.Response } else { throw } }
        $status = [int]$response.StatusCode
        if ($status -ge 300 -and $status -lt 400) {
            $next = $response.Headers['Location']
            $response.Close()
            if (-not $next -or $hop -eq 5) { throw "The download from $uri redirected nowhere or too often." }
            $uri = [uri]::new([uri]$uri, $next).AbsoluteUri
            continue
        }
        if ($status -ne 200) { $response.Close(); throw "The download from $uri answered $status." }
        $stream = $response.GetResponseStream()
        $file = [IO.File]::Create($destination)
        try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose(); $response.Close() }
        break
    }
    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($actual -ne $sha256) { throw "The download from $uri is not the file this version of TidyWindow expects (SHA-256 $actual), so nothing from it was run." }
}
function Assert-ChocolateySigned([string]$path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN="?Chocolatey Software, Inc\.?"?,') { throw "$(Split-Path -Leaf $path) does not carry a valid signature from Chocolatey Software ($($signature.Status)), so it was not run." }
}
function Assert-Native([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed (exit $LASTEXITCODE)." }
}
function Backup-File([string]$source, [string]$name) {
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $job $name) -Force }
}
try {
    $work = $job
    if ($hardened) {
        $work = Use-ProtectedWorkspace
        # Installers and the programs they start put their temporary files where nobody else can swap them.
        $env:TEMP = $work
        $env:TMP = $work
    }
    Start-Transcript -LiteralPath (Join-Path $work 'operation.log') -Force | Out-Null
