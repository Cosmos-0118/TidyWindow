    $exitCode = 0
} catch {
    $failure = $_.Exception.Message
    Write-Output "Recovery failed: $failure"
} finally {
    try { Stop-Transcript | Out-Null } catch { }
    # The result goes to the job folder and, for an elevated run, to its own folder too: the account that approved the prompt may not be able to write to the job folder.
    try {
        $result = @{ exitCode = $exitCode; error = $failure } | ConvertTo-Json
        foreach ($folder in @($work, $job) | Select-Object -Unique) {
            if ($folder -and (Test-Path -LiteralPath $folder)) { Set-Content -LiteralPath (Join-Path $folder 'result.json') -Value $result -Encoding UTF8 }
        }
        if ($work -and $work -ne $job -and (Test-Path -LiteralPath (Join-Path $work 'operation.log'))) { Copy-Item -LiteralPath (Join-Path $work 'operation.log') -Destination (Join-Path $job 'operation.log') -Force }
    } catch { }
}
exit $exitCode
