# Isolated native inventory integration. Only owned test processes/sockets and a temporary evidence directory.
[CmdletBinding()]
param([ValidateSet('auto', 'off')][string]$Mode = 'auto')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('SysWitness-NetworkSmoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$port = Get-Random -Minimum 40001 -Maximum 49000
$base = 'http://127.0.0.1:' + $port + '/api/v1'
$token = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') }) }
finally { $sha.Dispose() }
$keyFile = Join-Path $temp 'keys.json'
@{ clients = @(@{ id = 'native-test'; tokenSha256 = $hash; scopes = @('coverage.read', 'network.read') }) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $keyFile -Encoding UTF8
$settings = @{
    SYSWITNESS_CLIENT_KEYS_FILE = $keyFile
    SYSWITNESS_API_TOKEN = $null
    SYSWITNESS_DATA_DIRECTORY = $temp
    SYSWITNESS_PORT = $port.ToString()
    SYSWITNESS_EVENT_CHANNELS = 'System,Application'
    SYSWITNESS_PROCESS_MODE = 'off'
    SYSWITNESS_NETWORK_MODE = $Mode
}
$saved = @{}
$api = $null
$udp = $null
$headers = @{ 'X-SysWitness-Token' = $token }
try {
    foreach ($key in $settings.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $dll = Join-Path $repo 'src\SysWitness.Api\bin\Release\net10.0\SysWitness.Api.dll'
    $api = Start-Process dotnet -ArgumentList ('"' + $dll + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $temp 'out.log') -RedirectStandardError (Join-Path $temp 'err.log')
    $status = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        if ($api.HasExited) { throw 'Native network test API exited during startup.' }
        try { $status = (Invoke-RestMethod ($base + '/monitoring/coverage') -Headers $headers -TimeoutSec 2).networkCollector } catch {}
        if ($status.status -eq 'collecting' -or ($Mode -eq 'off' -and $status.status -eq 'disabled')) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($Mode -eq 'off') {
        if ($status.status -ne 'disabled') { throw 'Network off mode did not stay disabled.' }
        $feed = Invoke-RestMethod ($base + '/network/inventory?limit=1') -Headers $headers -TimeoutSec 3
        if ($feed.returned -ne 0) { throw 'Disabled native collector wrote inventory.' }
        Write-Host 'PASS: native network off mode'
        return
    }
    if ($status.status -ne 'collecting') { throw 'Native network source did not become available.' }
    $from = [uri]::EscapeDataString([DateTimeOffset]::UtcNow.ToString('O'))
    $udp = [Net.Sockets.UdpClient]::new()
    $udp.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    $localPort = $udp.Client.LocalEndPoint.Port
    $found = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $feed = Invoke-RestMethod ($base + '/network/inventory?sourceEventId=1&limit=500&fromUtc=' + $from) -Headers $headers -TimeoutSec 3
        $found = $feed.items | Where-Object { $_.endpoint.protocol -eq 'udp' -and $_.endpoint.processId -eq $PID -and $_.endpoint.localPort -eq $localPort } | Select-Object -First 1
        if ($found) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $found -or $found.processAttribution -ne 'pidOnly' -or $found.endpoint.remoteAddress -or $null -ne $found.endpoint.remotePort) {
        throw 'Own UDP endpoint did not appear with PID-only attribution and no invented peer.'
    }
    $udp.Dispose()
    $udp = $null
    $missing = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $feed = Invoke-RestMethod ($base + '/network/inventory?sourceEventId=3&limit=500&fromUtc=' + $from) -Headers $headers -TimeoutSec 3
        $missing = $feed.items | Where-Object { $_.endpoint.protocol -eq 'udp' -and $_.endpoint.processId -eq $PID -and $_.endpoint.localPort -eq $localPort } | Select-Object -First 1
        if ($missing) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $missing -or $missing.change -ne 'notObserved' -or $missing.quality -ne 'derived') {
        throw 'Successful next snapshot did not report the owned endpoint disappearance.'
    }
    Write-Host 'PASS: native network first observation, PID-only attribution, and disappearance diff'
} finally {
    if ($udp) { $udp.Dispose() }
    if ($api) {
        if (-not $api.HasExited) { Stop-Process -Id $api.Id -ErrorAction SilentlyContinue; $api.WaitForExit(3000) | Out-Null }
        $api.Dispose()
    }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
