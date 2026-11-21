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
$env:SYSWITNESS_EVENT_CHANNELS = 'System,Application'

# Separate read-only clients; only token hashes are stored in this temporary file.
$eventsClientToken = 'event-' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$processClientToken = 'process-' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$storageClientToken = 'store-' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
function TokenHash([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))
        return -join ($hash | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
}
$clientFile = Join-Path $testDir 'client-keys.json'
@{ clients = @(
    @{ id = 'events-client'; tokenSha256 = (TokenHash $eventsClientToken); scopes = @('events.read') },
    @{ id = 'process-client'; tokenSha256 = (TokenHash $processClientToken); scopes = @('processes.read') },
    @{ id = 'storage-client'; tokenSha256 = (TokenHash $storageClientToken); scopes = @('storage.read') }
)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $clientFile -Encoding UTF8
$env:SYSWITNESS_CLIENT_KEYS_FILE = $clientFile
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
            $systemStatus = @($coverage.channels | Where-Object { $_.channel -eq 'System' })
            $applicationStatus = @($coverage.channels | Where-Object { $_.channel -eq 'Application' })
            $notReady = @($coverage.channels | Where-Object {
                $_.status -ne 'collecting' -and $_.status -ne 'degraded'
            })
            if ($health.status -eq 'ok' -and $systemStatus.Count -eq 1 -and
                $applicationStatus.Count -eq 1 -and
                $systemStatus[0].status -eq 'collecting' -and
                $applicationStatus[0].status -eq 'collecting' -and $notReady.Count -eq 0) {
                $ready = $true
                break
            }
        } catch {}
    }
    if (-not $ready) {
        Get-Content $errFile -Tail 20 -ErrorAction SilentlyContinue
        throw 'API did not start or collector did not initialize'
    }
    $channels = @($coverage.channels)
    if ($channels.Count -lt 2 -or
        -not (@($channels.channel) -contains 'System') -or
        -not (@($channels.channel) -contains 'Application')) {
        throw 'Expected independently configured System and Application collectors'
    }
    foreach ($channel in $channels) {
        if ($channel.channel -in @('System', 'Application') -and
            ($channel.status -ne 'collecting' -or $null -eq $channel.lastRecordId)) {
            throw ('Required channel collector did not initialize: ' + $channel.channel)
        }
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

function Expect-ScopedStatus([string]$credential, [string]$path, [int]$expected) {
    $found = -1
    try {
        Invoke-WebRequest ($baseUrl + $path) -Headers @{
            'X-SysWitness-Token' = $credential
        } -TimeoutSec 4 -UseBasicParsing | Out-Null
        $found = 200
    } catch {
        if ($_.Exception.Response) { $found = [int]$_.Exception.Response.StatusCode }
    }
    if ($found -ne $expected) {
        throw ('Scoped access expected HTTP ' + $expected + ', got ' + $found + ' for ' + $path)
    }
}

try {
    & dotnet build (Join-Path $repo 'SysWitness.sln') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    $fixtureProject = Join-Path $repo 'tests\SysWitness.SmokeTests'
    & dotnet run --project $fixtureProject --no-build -- --seed-process-fixtures (Join-Path $testDir 'events.sqlite')
    if ($LASTEXITCODE -ne 0) { throw 'Process fixture setup failed' }

    $coverage = Start-TestApi
    $processPath = '/api/v1/processes/events'
    $timelinePath = '/api/v1/processes/11111111-2222-4333-8444-555555555555/timeline'
    Expect-Status $processPath 'GET' 401 $false
    Expect-Status $processPath 'GET' 403 $true
    Expect-Status $processPath 'POST' 404 $true
    Expect-ScopedStatus $eventsClientToken $processPath 403
    Expect-ScopedStatus $storageClientToken $timelinePath 403
    Expect-ScopedStatus $processClientToken '/api/v1/events' 403
    Expect-ScopedStatus $processClientToken ($processPath + '?category=windows.event') 400
    Expect-ScopedStatus $processClientToken ($timelinePath + '?limit=501') 400
    Expect-ScopedStatus $processClientToken '/api/v1/processes/78/timeline' 404
    $processHeaders = @{ 'X-SysWitness-Token' = $processClientToken }
    $processFeed = Invoke-RestMethod ($baseUrl + $processPath + '?channel=Security') -Headers $processHeaders -TimeoutSec 4
    if ($processFeed.returned -ne 2 -or $processFeed.items[0].kind -ne 'exit' -or
        $processFeed.items[1].parent.processId -ne 40 -or $null -ne $processFeed.items[1].processGuid) {
        throw 'Audit process event projection is incorrect'
    }
    $timeline = Invoke-RestMethod ($baseUrl + $timelinePath) -Headers $processHeaders -TimeoutSec 4
    if ($timeline.returned -ne 2 -or $timeline.items[0].kind -ne 'exit' -or
        $timeline.items[1].commandLine -ne '"test.exe" a&b' -or
        $timeline.items[1].source.provider -ne 'Microsoft-Windows-Sysmon' -or
        ($timeline.items[1].PSObject.Properties.Name -contains 'rawXml')) {
        throw 'GUID timeline did not return the scoped, source-backed process fields'
    }
    $evidence = Invoke-RestMethod ($baseUrl + '/api/v1/events/' + $timeline.items[1].eventId) -Headers $headers -TimeoutSec 4
    if ($evidence.category -ne 'windows.event' -or ($evidence.PSObject.Properties.Name -contains 'commandLine')) {
        throw 'General event summary must preserve its category and hide command-line data'
    }
    Expect-Status '/api/v1/events' 'GET' 401 $false
    Expect-ScopedStatus $eventsClientToken '/api/v1/events?limit=1' 200
    Expect-ScopedStatus $eventsClientToken '/api/v1/monitoring/coverage' 403
    Expect-ScopedStatus $eventsClientToken '/api/v1/storage/status' 403
    Expect-ScopedStatus $storageClientToken '/api/v1/storage/status' 200
    Expect-ScopedStatus $storageClientToken '/api/v1/events' 403
    Expect-ScopedStatus $storageClientToken '/api/v1/monitoring/gaps' 403
    Expect-ScopedStatus $eventsClientToken '/api/v1/events/delete' 404
    Expect-ScopedStatus ('invalid-' + [guid]::NewGuid().ToString('N')) '/api/v1/events' 401
    Expect-Status '/api/v1/storage/status' 'GET' 401 $false
    Expect-Status '/api/v1/storage/retention' 'GET' 401 $false
    Expect-Status '/api/v1/monitoring/gaps' 'GET' 401 $false
    Expect-Status '/api/v1/monitoring/coverage?channel=Application' 'GET' 401 $false
    Expect-Status '/api/v1/monitoring/coverage?channel=Unknown' 'GET' 404 $true
    Expect-Status '/api/v1/monitoring/coverage?ignored=1' 'GET' 400 $true
    Expect-Status '/api/v1/monitoring/gaps?channel=Unknown' 'GET' 404 $true
    Expect-Status '/api/v1/monitoring/gaps?limit=1000' 'GET' 400 $true
    Expect-Status '/api/v1/storage/retention?limit=1000' 'GET' 400 $true
    Expect-Status '/api/v1/storage/retention?invalid=1' 'GET' 400 $true
    Expect-Status '/api/v1/storage/prune' 'POST' 404 $true
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

    $storage = Invoke-RestMethod ($baseUrl + '/api/v1/storage/status') -Headers $headers -TimeoutSec 4
    if ($storage.softLimitBytes -ne 4294967296 -or
        $storage.retentionDays -ne $null -or $storage.storedEventCount -lt 1 -or
        $storage.minFreeDiskBytes -ne 1073741824 -or
        $null -eq $storage.availableFreeDiskBytes -or
        $storage.availableFreeDiskBytes -le 0) {
        throw 'Storage status does not expose accurate volume-space and local policy'
    }
    if ($storage.availableFreeDiskBytes -gt $storage.minFreeDiskBytes -and
        $storage.softLimitReached -eq $false -and $storage.canAcceptNewEvidence -ne $true) {
        throw 'Storage status should allow writes when the free-space guard permits them'
    }
    $retentionResponse = Invoke-WebRequest ($baseUrl + '/api/v1/storage/retention') -Headers $headers -TimeoutSec 4 -UseBasicParsing
    if ($retentionResponse.Content.Trim() -ne '[]') {
        throw 'Default storage policy must not delete evidence'
    }

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
    $application = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage?channel=Application') -Headers $headers -TimeoutSec 4
    if ($application.channel -ne 'Application' -or $application.status -ne 'collecting') {
        throw 'Application collector status missing from per-channel API'
    }
    $appEvents = Invoke-RestMethod ($baseUrl + '/api/v1/events?channel=Application&limit=2') -Headers $headers -TimeoutSec 4
    if ($appEvents.returned -lt 1 -or (@($appEvents.items | Where-Object channel -ne 'Application').Count -gt 0)) {
        throw 'Application events are not queryable independently'
    }
    $appGaps = @(Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/gaps?channel=Application') -Headers $headers -TimeoutSec 4)
    $gaps = @(Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/gaps?channel=System') -Headers $headers -TimeoutSec 4)
    if ($coverage.recordedGaps -gt 0 -and ($gaps.Count -eq 0 -or -not $gaps[0].reason)) {
        throw 'Recorded coverage gaps must be inspectable through the read-only API'
    }
    $firstRecordId = $coverage.lastRecordId
    $firstGapCount = $coverage.recordedGaps
    $appFirstRecordId = $application.lastRecordId
    $appFirstGapCount = $application.recordedGaps
    if ($firstRecordId -eq $null) { throw 'Expected Windows System events for restart test' }

    Stop-TestApi
    # Reuse the same temporary SQLite DB to validate a native bookmark after restart.
    $resumed = Start-TestApi
    if ($resumed.lastRecordId -lt $firstRecordId) { throw 'Checkpoint regressed across restart' }
    if ($resumed.recordedGaps -ne $firstGapCount) {
        throw 'Native System bookmark restart unexpectedly recorded a coverage gap'
    }
    $appResumed = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage?channel=Application') -Headers $headers -TimeoutSec 4
    if ($appResumed.lastRecordId -lt $appFirstRecordId -or
        $appResumed.recordedGaps -ne $appFirstGapCount) {
        throw 'Application bookmark restart failed independently of System'
    }
    # An optional unavailable channel must appear as degraded without breaking the core channels.
    Stop-TestApi
    $env:SYSWITNESS_EVENT_CHANNELS = 'System,Application,Microsoft-Windows-Sysmon/Operational'
    $extended = Start-TestApi
    $optional = @($extended.channels | Where-Object {
        $_.channel -eq 'Microsoft-Windows-Sysmon/Operational'
    })
    if ($optional.Count -ne 1 -or $optional[0].status -notin @('collecting','degraded')) {
        throw 'Optional Sysmon channel did not report an independent status'
    }
    if ($optional[0].status -eq 'degraded' -and -not $optional[0].lastError) {
        throw 'Degraded optional channel must report a diagnostic error class'
    }
    if (@($extended.configuredCollectors).Count -ne 3) {
        throw 'Configured channels must appear even if a collector is unavailable'
    }
    if ($optional[0].status -eq 'degraded' -and
        @($extended.enabledCollectors) -contains 'windows-eventlog:Microsoft-Windows-Sysmon/Operational') {
        throw 'Unavailable collector must not be reported as enabled'
    }
    $systemAfterOptional = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage?channel=System') -Headers $headers -TimeoutSec 4
    if ($systemAfterOptional.status -ne 'collecting' -or
        $systemAfterOptional.lastRecordId -lt $firstRecordId) {
        throw 'Optional channel failure disrupted the System collector'
    }
    Write-Host ('PASS: independently resumed System/Application, optional Sysmon='+ $optional[0].status + ', disk free status, process events and GUID timeline, client scopes 200/401/403/404, default retention OFF; SystemGaps=' + $resumed.recordedGaps + ', ApplicationGaps=' + $appResumed.recordedGaps)
}
finally {
    Stop-TestApi
    Remove-Item Env:SYSWITNESS_API_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_CLIENT_KEYS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_DATA_DIRECTORY -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_EVENT_CHANNELS -ErrorAction SilentlyContinue
    Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
}
