# Windows-only integration test. Isolated temporary database; no real event contents printed.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$port = Get-Random -Minimum 20000 -Maximum 40000
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('SysWitness-ApiSmoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$token = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$env:SYSWITNESS_API_TOKEN = $token
$env:SYSWITNESS_PORT = [string]$port
$env:SYSWITNESS_DATA_DIRECTORY = $testDir
$dll = Join-Path $repo 'src\SysWitness.Api\bin\Debug\net10.0\SysWitness.Api.dll'
$baseUrl = 'http://127.0.0.1:' + $port
$headers = @{ 'X-SysWitness-Token' = $token }
$script:process = $null
$script:generation = 0

function Stop-TestApi {
    if ($script:process) {
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        try { $script:process.WaitForExit(3000) | Out-Null } catch {}
        $script:process = $null
    }
}

function Start-TestApi {
    $script:generation++
    $outFile = Join-Path $testDir ('api-' + $script:generation + '.out')
    $errFile = Join-Path $testDir ('api-' + $script:generation + '.err')
    $script:process = Start-Process -FilePath dotnet -ArgumentList @($dll) -PassThru -WindowStyle Hidden -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $ready = $false
    for ($i = 0; $i -lt 35; $i++) {
        Start-Sleep -Milliseconds 250
        if ($script:process.HasExited) { break }
        try {
            $health = Invoke-RestMethod ($baseUrl + '/api/v1/health') -TimeoutSec 2
            $coverage = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage') -Headers $headers -TimeoutSec 2
            if ($health.status -eq 'ok' -and $coverage.status -eq 'collecting') {
                $ready = $true
                break
            }
        } catch {}
    }
    if (-not $ready) {
        Get-Content $errFile -Tail 20 -ErrorAction SilentlyContinue
        throw 'API did not start or collector did not initialize'
    }
    return $coverage
}

function Expect-Status([string]$path, [string]$method, [int]$status, [bool]$authenticated) {
    $requestHeaders = @{}
    if ($authenticated) { $requestHeaders['X-SysWitness-Token'] = $token }
    $found = -1
    try {
        Invoke-WebRequest ($baseUrl + $path) -Method $method -Headers $requestHeaders -TimeoutSec 4 | Out-Null
        $found = 200
    } catch {
        if ($_.Exception.Response) { $found = [int]$_.Exception.Response.StatusCode }
    }
    if ($found -ne $status) { throw ("Expected HTTP " + $status + " got " + $found + " for " + $path) }
}

try {
    & dotnet build (Join-Path $repo 'SysWitness.sln') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    $coverage = Start-TestApi
    Expect-Status '/api/v1/events' 'GET' 401 $false
    Expect-Status '/api/v1/monitoring/gaps' 'GET' 401 $false
    Expect-Status '/api/v1/events?cursor=invalid' 'GET' 400 $true
    Expect-Status '/api/v1/events?limit=100000' 'GET' 400 $true
    Expect-Status '/api/v1/events?unknownField=1' 'GET' 400 $true
    Expect-Status '/api/v1/events?sourceEventId=-1' 'GET' 400 $true
    Expect-Status '/api/v1/events?fromUtc=2026-11-20' 'GET' 400 $true
    Expect-Status '/api/v1/firewall/block' 'POST' 404 $true

    # A loopback HTTP socket must reject a non-loopback Host header.
    $rejectedHost = $false
    try {
        Invoke-WebRequest ($baseUrl + '/api/v1/health') -Headers @{ Host = 'unexpected.example' } -TimeoutSec 4 | Out-Null
    } catch {
        $rejectedHost = ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 400)
    }
    if (-not $rejectedHost) { throw 'Non-loopback Host header must be rejected' }

    $events = Invoke-RestMethod ($baseUrl + '/api/v1/events?limit=5') -Headers $headers -TimeoutSec 4
    if ($events.returned -gt 5) { throw 'Result limit ignored' }
    if ($events.returned -gt 0 -and ($events.items[0].PSObject.Properties.Name -contains 'rawXml')) {
        throw 'Raw event XML exposed in summary endpoint'
    }
    if ($events.returned -ge 2) {
        $firstPage = Invoke-RestMethod ($baseUrl + '/api/v1/events?limit=1') -Headers $headers -TimeoutSec 4
        if (-not $firstPage.nextCursor) { throw 'Expected next page cursor' }
        $nextUrl = $baseUrl + '/api/v1/events?limit=1&cursor=' +
            [uri]::EscapeDataString($firstPage.nextCursor)
        $secondPage = Invoke-RestMethod $nextUrl -Headers $headers -TimeoutSec 4
        if ($secondPage.returned -ne 1 -or
            $firstPage.items[0].eventId -eq $secondPage.items[0].eventId) {
            throw 'Cursor pagination did not advance'
        }
        $providerValue = [uri]::EscapeDataString($firstPage.items[0].provider)
        $filtered = Invoke-RestMethod ($baseUrl + '/api/v1/events?provider=' + $providerValue + '&limit=1') -Headers $headers -TimeoutSec 4
        if ($filtered.returned -ne 1 -or $filtered.items[0].provider -ne $firstPage.items[0].provider) {
            throw 'Provider filter returned incorrect data'
        }
    }
    $gaps = @(Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/gaps') -Headers $headers -TimeoutSec 4)
    if ($coverage.recordedGaps -gt 0 -and ($gaps.Count -eq 0 -or -not $gaps[0].reason)) {
        throw 'Recorded coverage gaps must be inspectable through the read-only API'
    }
    $firstRecordId = $coverage.lastRecordId
    $firstGapCount = $coverage.recordedGaps
    if ($firstRecordId -eq $null) { throw 'Expected Windows System events for restart test' }

    Stop-TestApi
    # Reuse the same temporary SQLite DB to validate a native bookmark after restart.
    $resumed = Start-TestApi
    if ($resumed.lastRecordId -lt $firstRecordId) { throw 'Checkpoint regressed across restart' }
    if ($resumed.recordedGaps -ne $firstGapCount) {
        throw 'Native bookmark restart unexpectedly recorded a coverage gap'
    }
    Write-Host ('PASS: Windows event bootstrap/resume, durable bookmark, 401, 400, no control endpoint; returned=' + $events.returned + ', gaps=' + $resumed.recordedGaps)
}
finally {
    Stop-TestApi
    Remove-Item Env:SYSWITNESS_API_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_DATA_DIRECTORY -ErrorAction SilentlyContinue
    Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
}
