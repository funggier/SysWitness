# SysWitness portable launcher. No installer, registry, Windows service, or global environment edits.
# All paths are resolved against this script's directory, not the current working directory.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-RandomToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return (-join ($bytes | ForEach-Object { $_.ToString('x2') }))
}

function Get-Sha256([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hashed = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value)) }
    finally { $sha.Dispose() }
    return (-join ($hashed | ForEach-Object { $_.ToString('x2') }))
}

function Resolve-PortablePath([string]$root, [string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or
        $relative.Contains(':')) {
        throw 'The dataDirectory setting must be a relative folder inside the portable root.'
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $prefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'dataDirectory cannot escape the SysWitness portable folder.'
    }
    return $resolved
}

function Restrict-PrivateDirectory([string]$directory) {
    # Best effort for NTFS. A FAT/exFAT or shared folder may not support ACLs.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    & icacls.exe $directory '/inheritance:r' '/grant:r' "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Could not restrict ACLs for config/. Use a trusted, private Windows folder; the owner-token file is sensitive.'
    }
}

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$exe = Join-Path $root 'SysWitness.Api.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Missing SysWitness.Api.exe. Use the complete self-contained portable ZIP.'
}
$defaults = Join-Path $root 'settings.default.json'
$configDir = Join-Path $root 'config'
$settingFile = Join-Path $configDir 'settings.json'
$clientFile = Join-Path $configDir 'client-keys.json'
$ownerFile = Join-Path $configDir 'owner-token.txt'

if (-not (Test-Path -LiteralPath $configDir -PathType Container)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}
Restrict-PrivateDirectory $configDir
if (-not (Test-Path -LiteralPath $settingFile -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $defaults -PathType Leaf)) {
        throw 'Missing settings.default.json from the portable package.'
    }
    Copy-Item -LiteralPath $defaults -Destination $settingFile -ErrorAction Stop
}

# Only provision when neither credential file exists. An inconsistent/partial
# state fails closed instead of silently generating a new credential.
if (-not (Test-Path -LiteralPath $clientFile -PathType Leaf)) {
    if (Test-Path -LiteralPath $ownerFile -PathType Leaf) {
        throw 'owner-token.txt exists but client-keys.json is missing; repair the local config manually.'
    }
    $ownerToken = New-RandomToken
    $clientKeys = @{
        clients = @(@{
            id = 'portable-owner'
            tokenSha256 = (Get-Sha256 $ownerToken)
            scopes = @('events.read', 'coverage.read', 'storage.read', 'processes.read')
        })
    }
    $clientKeys | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $clientFile -Encoding UTF8
    $ownerToken | Set-Content -LiteralPath $ownerFile -Encoding ASCII
    Write-Host 'Created a local owner API token at config/owner-token.txt.'
    Write-Warning 'Keep config/owner-token.txt private. Anyone with this token can read sensitive system events.'
}

$settings = Get-Content -LiteralPath $settingFile -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
foreach ($required in @('port', 'eventChannels', 'dataDirectory',
    'storageSoftLimitMB', 'minFreeDiskMB', 'retentionDays')) {
    if (-not ($settings.PSObject.Properties.Name -contains $required)) {
        throw ('Missing portable setting: ' + $required)
    }
}

$data = Resolve-PortablePath $root ([string]$settings.dataDirectory)
if (-not (Test-Path -LiteralPath $data)) {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
}

$port = [int]$settings.port
if ($port -lt 1 -or $port -gt 65535) { throw 'port must be 1..65535.' }
$env:SYSWITNESS_PORT = [string]$port
$env:SYSWITNESS_EVENT_CHANNELS = [string]$settings.eventChannels
if ($settings.PSObject.Properties.Name -contains 'processMode') {
    $env:SYSWITNESS_PROCESS_MODE = [string]$settings.processMode
}
$env:SYSWITNESS_STORAGE_SOFT_LIMIT_MB = [string]$settings.storageSoftLimitMB
$env:SYSWITNESS_MIN_FREE_DISK_MB = [string]$settings.minFreeDiskMB
if ($null -eq $settings.retentionDays) {
    Remove-Item Env:SYSWITNESS_RETENTION_DAYS -ErrorAction SilentlyContinue
} else {
    $env:SYSWITNESS_RETENTION_DAYS = [string]$settings.retentionDays
}
$env:SYSWITNESS_DATA_DIRECTORY = $data
$env:SYSWITNESS_CLIENT_KEYS_FILE = $clientFile
# This launcher intentionally does not write permanent environment variables or
# elevate its process, and does not attempt to configure Windows Audit Policy.

Write-Host ('SysWitness portable running from: ' + $root)
Write-Host ('Local API: http://127.0.0.1:' + $port + '/api/v1/health')
Write-Host 'Press Ctrl+C to stop.'
& $exe
exit $LASTEXITCODE
