# Isolated Windows integration test for the active local process source.
[CmdletBinding()]
param([ValidateSet('auto', 'snapshot')][string]$Mode = 'auto')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$port = Get-Random -Minimum 40001 -Maximum 49000
$base = 'http://127.0.0.1:' + $port + '/api/v1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('SysWitness-ProcessSmoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$api = $null
$probe = $null
$token = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') }) }
finally { $sha.Dispose() }
$keyFile = Join-Path $temp 'keys.json'
@{ clients = @(@{ id = 'test-client'; tokenSha256 = $hash; scopes = @('coverage.read', 'processes.read') }) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $keyFile -Encoding UTF8
$settings = @{
    SYSWITNESS_CLIENT_KEYS_FILE = $keyFile
    SYSWITNESS_API_TOKEN = $null
    SYSWITNESS_DATA_DIRECTORY = $temp
    SYSWITNESS_PORT = $port.ToString()
    SYSWITNESS_EVENT_CHANNELS = 'System,Application'
    SYSWITNESS_PROCESS_MODE = $Mode
    SYSWITNESS_RETENTION_DAYS = $null
}
$saved = @{}
$headers = @{ 'X-SysWitness-Token' = $token }
try {
    foreach ($key in $settings.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $dll = Join-Path $repo 'src\SysWitness.Api\bin\Release\net10.0\SysWitness.Api.dll'
    $apiArgs = @{
        ArgumentList = ('"' + $dll + '"')
        WindowStyle = 'Hidden'
        PassThru = $true
        RedirectStandardOutput = Join-Path $temp 'out.log'
        RedirectStandardError = Join-Path $temp 'err.log'
    }
    $api = Start-Process dotnet @apiArgs
    $status = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        if ($api.HasExited) { throw 'Process collector test API exited during startup.' }
        try {
            $coverage = Invoke-RestMethod ($base + '/monitoring/coverage') -Headers $headers -TimeoutSec 2
            $status = $coverage.processCollector
        } catch {}
        if ($status.status -eq 'collecting') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($status.status -ne 'collecting' -or $status.mode -ne $Mode) {
        throw ('Expected active process collector for mode ' + $Mode + ', got ' + ($status | ConvertTo-Json -Compress))
    }
    if ($Mode -eq 'snapshot' -and ($status.activeSource -ne 'snapshot' -or $status.fallbackReason)) {
        throw 'Snapshot mode should select snapshots directly.'
    }
    if ($Mode -eq 'auto' -and $status.activeSource -eq 'snapshot' -and -not $status.fallbackReason) {
        throw 'Auto mode must disclose why the direct event subscription was unavailable.'
    }
    $channel = switch ($status.activeSource) {
        'snapshot' { 'WindowsProcessSnapshot' }
        'wmi' { 'WindowsProcessWmi' }
        default { throw 'Unexpected process evidence source.' }
    }

    $from = [uri]::EscapeDataString([DateTimeOffset]::UtcNow.ToString('O'))
    $probeArgs = @('-NoProfile', '-NonInteractive', '-Command', '"Start-Sleep -Seconds 5; exit 23"')
    $probe = Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $probeArgs -WindowStyle Hidden -PassThru
    if (-not $probe.WaitForExit(12000) -or $probe.ExitCode -ne 23) { throw 'Test process did not exit normally.' }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $uri = $base + '/processes/events?channel=' + $channel + '&fromUtc=' + $from + '&limit=500'
        $feed = Invoke-RestMethod $uri -Headers $headers -TimeoutSec 3
        $matching = @($feed.items | Where-Object processId -eq $probe.Id)
        $start = $matching | Where-Object kind -eq 'start' | Select-Object -First 1
        $exit = $matching | Where-Object kind -eq 'exit' | Select-Object -First 1
        if ($start -and $exit) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not ($start -and $exit)) { throw 'Test process start and exit were not both captured.' }
    if ($channel -eq 'WindowsProcessSnapshot') {
        if ($start.quality -ne 'derived' -or $exit.quality -ne 'derived' -or
            -not $start.localProcessInstanceId -or $start.localProcessInstanceId -ne $exit.localProcessInstanceId) {
            throw 'Snapshots did not report consistent identity and observation quality.'
        }
    } elseif ($start.quality -ne 'observed' -or $exit.quality -ne 'observed' -or $exit.exitStatus -ne 23) {
        throw 'WMI did not report the native lifecycle event fields.'
    }
    Write-Host ('PASS: process start and exit mode=' + $Mode + '; source=' + $status.activeSource +
        '; fallback=' + $status.fallbackReason)
} finally {
    foreach ($process in @($probe, $api)) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            $process.WaitForExit(3000) | Out-Null
        }
        if ($process) { $process.Dispose() }
    }
    foreach ($key in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($key, $saved[$key])
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
