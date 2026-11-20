# Windows-only local integration smoke test. Never commits or prints host event content.
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
$outFile = Join-Path $testDir 'api.out'
$errFile = Join-Path $testDir 'api.err'
$process = $null
try {
    & dotnet build (Join-Path $repo 'SysWitness.sln') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $process = Start-Process -FilePath dotnet -ArgumentList @($dll) -PassThru -WindowStyle Hidden -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $baseUrl = 'http://127.0.0.1:' + $port
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { break }
        try {
            $health = Invoke-RestMethod ($baseUrl + '/api/v1/health') -TimeoutSec 2
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch {}
    }
    if (-not $ready) { throw 'API did not start' }
    $unauthorized = $false
    try {
        Invoke-WebRequest ($baseUrl + '/api/v1/events') -TimeoutSec 3 | Out-Null
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $unauthorized = $true }
    }
    if (-not $unauthorized) { throw 'Missing token must produce 401' }
    $headers = @{ 'X-SysWitness-Token' = $token }
    $coverage = Invoke-RestMethod ($baseUrl + '/api/v1/monitoring/coverage') -Headers $headers -TimeoutSec 4
    $events = Invoke-RestMethod ($baseUrl + '/api/v1/events?limit=5') -Headers $headers -TimeoutSec 4
    if ($coverage.status -ne 'sampling') { throw ('Expected sampling status, got ' + $coverage.status) }
    if ($events.returned -gt 5) { throw 'Result limit ignored' }
    if ($events.returned -gt 0 -and ($events.items[0].PSObject.Properties.Name -contains 'rawXml')) {
        throw 'Raw evidence leaked into summary endpoint'
    }
    $actionNotFound = $false
    try {
        Invoke-WebRequest ($baseUrl + '/api/v1/firewall/block') -Method Post -Headers $headers -TimeoutSec 3 | Out-Null
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $actionNotFound = $true }
    }
    if (-not $actionNotFound) { throw 'Forbidden action endpoint must not exist' }
    Write-Host ('PASS: live System collector, localhost API, 401 auth, read-only endpoints; events sampled=' + $events.returned)
}
finally {
    if ($process) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:SYSWITNESS_API_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:SYSWITNESS_DATA_DIRECTORY -ErrorAction SilentlyContinue
    # Unique temporary test directory; not the user's persistent SysWitness database.
    Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
}
