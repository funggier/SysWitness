# Windows-only integration test: unpack self-contained ZIP, start, move folder, restart.
# No user database or credentials are included in the ZIP.
[CmdletBinding()]
param([string]$ZipPath = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ZipPath) {
    $ZipPath = Join-Path $root 'artifacts\SysWitness-0.1.0-preview.3-win-x64-portable.zip'
}
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$temporary = Join-Path $root ('artifacts\_relocation-test-' + [guid]::NewGuid().ToString('N'))
$phaseA = Join-Path $temporary 'phase A'
$phaseB = Join-Path $temporary 'phase B'
$appA = Join-Path $phaseA 'SysWitness'
$appB = Join-Path $phaseB 'Renamed System Witness'
$port = Get-Random -Minimum 30000 -Maximum 45000
$baseUrl = 'http://127.0.0.1:' + $port
$script:launcher = $null
$originalProcessMode = [Environment]::GetEnvironmentVariable('SYSWITNESS_PROCESS_MODE')

function Stop-PortableProcess {
    if ($script:launcher) {
        $launcherId = $script:launcher.Id
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$launcherId" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'SysWitness.Api.exe' })
        foreach ($child in $children) {
            $ownedChild = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($ownedChild) {
                try {
                    Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
                    if (-not $ownedChild.WaitForExit(5000)) { throw 'Portable test child did not exit.' }
                } finally { $ownedChild.Dispose() }
            }
        }
        Stop-Process -Id $launcherId -Force -ErrorAction SilentlyContinue
        try { $script:launcher.WaitForExit(3000) | Out-Null } catch {}
        $script:launcher = $null
    }
}

function Start-PortableProcess([string]$app, [int]$runIndex) {
    $stdout = Join-Path $temporary ('launcher-' + $runIndex + '.out')
    $stderr = Join-Path $temporary ('launcher-' + $runIndex + '.err')
    $script:launcher = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        ('"' + (Join-Path $app 'Start-SysWitness.ps1') + '"')
    ) -WorkingDirectory $env:TEMP -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $ready = $false
    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $startupDeadline) {
        Start-Sleep -Milliseconds 100
        if ($script:launcher.HasExited) { break }
        try {
            $health = Invoke-RestMethod ($baseUrl + '/api/v1/health') -TimeoutSec 2
            if ($health.status -eq 'ok') {
                $startupTokenPath = Join-Path $app 'config\owner-token.txt'
                if (Test-Path -LiteralPath $startupTokenPath) {
                    $startupHeaders = @{ 'X-SysWitness-Token' = (Get-Content -LiteralPath $startupTokenPath -Raw).Trim() }
                    $startupCoverage = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage') -Headers $startupHeaders -TimeoutSec 2
                    $requiredChannels = @($startupCoverage.channels | Where-Object { $_.channel -in @('System','Application') })
                    $pendingChannels = @($requiredChannels | Where-Object { $_.status -ne 'collecting' -or $null -eq $_.lastRecordId })
                    if ($requiredChannels.Count -eq 2 -and $pendingChannels.Count -eq 0) {
                        $ready = $true
                        break
                    }
                }
            }
        } catch {}
    }
    if (-not $ready) {
        Get-Content $stderr -Tail 20 -ErrorAction SilentlyContinue
        Get-Content $stdout -Tail 20 -ErrorAction SilentlyContinue
        throw ('Relocated portable API did not start (run ' + $runIndex + ').')
    }
}

New-Item -ItemType Directory -Path $phaseA -Force | Out-Null
try {
    [Environment]::SetEnvironmentVariable('SYSWITNESS_PROCESS_MODE', $null)
    Expand-Archive -LiteralPath $zip -DestinationPath $phaseA
    if (-not (Test-Path (Join-Path $appA 'SysWitness.Api.exe'))) {
        throw 'Portable ZIP missing executable.'
    }
    foreach ($name in @('config','data','logs','runtime')) {
        if (Test-Path (Join-Path $appA $name)) {
            throw ('Portable ZIP improperly contained local private data folder: ' + $name)
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $appA 'config') | Out-Null
    $settingsPath = Join-Path $appA 'config\settings.json'
    $settings = Get-Content (Join-Path $appA 'settings.default.json') -Raw | ConvertFrom-Json
    $settings.port = $port
    $settings.PSObject.Properties.Remove('processMode') # Exercise a configuration written by the previous version.
    $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

    Start-PortableProcess $appA 1
    $tokenPathA = Join-Path $appA 'config\owner-token.txt'
    if (-not (Test-Path $tokenPathA)) { throw 'First-run portable owner token missing.' }
    $tokenBefore = (Get-Content -LiteralPath $tokenPathA -Raw).Trim()
    if ($tokenBefore.Length -ne 64) { throw 'Portable owner token must be 32-byte random hex.' }
    $headers = @{ 'X-SysWitness-Token' = $tokenBefore }
    $processesBefore = Invoke-RestMethod ($baseUrl + '/api/v1/processes/events?limit=1') -Headers $headers -TimeoutSec 4
    if ($processesBefore.limit -ne 1) { throw 'New portable owner cannot read process events.' }
    $networkBefore = Invoke-RestMethod ($baseUrl + '/api/v1/network/connections?limit=1') -Headers $headers -TimeoutSec 4
    if ($networkBefore.limit -ne 1) { throw 'New portable owner cannot read network events.' }
    $activityRoute = '/api/v1/processes/aaaaaaaa-2222-4333-8444-555555555555/activity?computer=portable-fixture-host&limit=1'
    $activityBefore = Invoke-RestMethod ($baseUrl + $activityRoute) -Headers $headers -TimeoutSec 4
    if ($activityBefore.limit -ne 1 -or $activityBefore.quality -ne 'derived') {
        throw 'Portable owner cannot read the combined activity route.'
    }
    $treeRoute = '/api/v1/processes/cccccccc-2222-4333-8444-555555555555/tree?computer=portable-fixture-host&depth=1&limit=5'
    $treeBefore = Invoke-RestMethod ($baseUrl + $treeRoute) -Headers $headers -TimeoutSec 4
    if ($treeBefore.quality -ne 'derived' -or $treeBefore.nodes.Count -ne 1) {
        throw 'Portable owner cannot read process tree reference data.'
    }
    $eventsBefore = Invoke-RestMethod ($baseUrl + '/api/v1/events?limit=3') -Headers $headers -TimeoutSec 4
    $coverageBefore = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage') -Headers $headers -TimeoutSec 4
    if ($eventsBefore.returned -lt 1 -or $coverageBefore.status -notin @('collecting','degraded')) {
        throw 'First run did not collect queryable Windows events.'
    }
    if ($coverageBefore.processCollector.mode -ne 'auto') { throw 'Existing settings must default to auto.' }
    if (-not (Test-Path (Join-Path $appA 'data\events.sqlite'))) {
        throw 'Portable data not created under the application folder.'
    }
    $idBefore = $eventsBefore.items[0].eventId
    Stop-PortableProcess
    New-Item -ItemType Directory -Path $phaseB -Force | Out-Null
    Move-Item -LiteralPath $appA -Destination $appB -ErrorAction Stop

    $movedSettings = Join-Path $appB 'config\settings.json'
    $settings | Add-Member -NotePropertyName processMode -NotePropertyValue 'snapshot'
    $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $movedSettings -Encoding UTF8
    Start-PortableProcess $appB 2
    $coverageAfter = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage') -Headers $headers -TimeoutSec 4
    $tokenAfter = (Get-Content -LiteralPath (Join-Path $appB 'config\owner-token.txt') -Raw).Trim()
    if ($tokenAfter -ne $tokenBefore) { throw 'Portable owner token changed after relocation.' }
    $processesAfter = Invoke-RestMethod ($baseUrl + '/api/v1/processes/events?limit=1') -Headers $headers -TimeoutSec 4
    if ($processesAfter.limit -ne 1) { throw 'Relocated owner cannot read process events.' }
    $dnsAfter = Invoke-RestMethod ($baseUrl + '/api/v1/network/dns?limit=1') -Headers $headers -TimeoutSec 4
    if ($dnsAfter.limit -ne 1) { throw 'Relocated portable owner cannot read DNS events.' }
    $activityAfter = Invoke-RestMethod ($baseUrl + $activityRoute) -Headers $headers -TimeoutSec 4
    if ($activityAfter.limit -ne 1 -or $activityAfter.correlationMethod -ne $activityBefore.correlationMethod) {
        throw 'Relocated portable owner cannot read the combined activity route.'
    }
    $treeAfter = Invoke-RestMethod ($baseUrl + $treeRoute) -Headers $headers -TimeoutSec 4
    if ($treeAfter.method -ne $treeBefore.method -or $treeAfter.rootProcessGuid -ne $treeBefore.rootProcessGuid) {
        throw 'Relocated portable owner cannot read process tree data.'
    }
    $byId = Invoke-RestMethod ($baseUrl + '/api/v1/events/' + $idBefore) -Headers $headers -TimeoutSec 4
    if ($byId.eventId -ne $idBefore) {
        throw 'An event stored before relocation disappeared from the moved SQLite DB.'
    }
    # Compare after collector readiness; a genuinely regressed committed record still fails below.
    # Avoid printing private channel positions on a successful run.
    if ($coverageAfter.processCollector.mode -ne 'snapshot') { throw 'Moved portable settings did not select snapshot mode.' }
    foreach ($before in $coverageBefore.channels) {
        $after = @($coverageAfter.channels | Where-Object channel -eq $before.channel)
        if ($after.Count -ne 1 -or $after[0].lastRecordId -lt $before.lastRecordId) {
            throw ('Collector checkpoint not ready/regressed: channel=' + $before.channel + '; before=' + $before.lastRecordId + '; after=' + $after[0].lastRecordId + '; status=' + $after[0].status)
        }
    }
    Write-Output ('PASS: self-contained ZIP, first-run token, SQLite, bookmarked events and activity and process tree APIs continue after folder relocation; port=' + $port)
} finally {
    Stop-PortableProcess
    [Environment]::SetEnvironmentVariable('SYSWITNESS_PROCESS_MODE', $originalProcessMode)
    try { Remove-Item -LiteralPath $temporary -Force -Recurse -ErrorAction Stop }
    catch {
        Write-Warning ('Portable relocation test cleanup incomplete: ' + $temporary)
    }
}
