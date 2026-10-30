    $exitCode = 0
} catch {
    $failure = $_.Exception.Message
    Write-Output "Recovery failed: $failure"
} finally {
    @{ exitCode = $exitCode; error = $failure } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $job 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
exit $exitCode
