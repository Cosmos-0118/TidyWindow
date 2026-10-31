$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$env:NO_COLOR = '1'
$exitCode = 1
Start-Transcript -LiteralPath (Join-Path $job 'operation.log') -Force | Out-Null
function Download-Official([string]$uri, [string]$destination) {
    if ($uri -notmatch '^https://(community\.chocolatey\.org|raw\.githubusercontent\.com|github\.com|api\.github\.com|cdn\.winget\.microsoft\.com)/') { throw 'Unexpected download host.' }
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $destination -TimeoutSec 90
    if ((Get-Item -LiteralPath $destination).Length -eq 0) { throw 'The downloaded file was empty.' }
}
function Assert-Native([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed (exit $LASTEXITCODE)." }
}
function Backup-File([string]$source, [string]$name) {
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $job $name) -Force }
}
try {
