# Build relocatable, Windows self-contained ZIP without local logs, DBs or secrets.
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$Version = '0.1.0-preview.1'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts'
}
$outDir = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir ("SysWitness-" + $Version + "-win-x64-portable.zip")
$stageParent = Join-Path $outDir ('_stage-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageParent 'SysWitness'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    $proj = Join-Path $root 'src\SysWitness.Api\SysWitness.Api.csproj'
    & dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false --nologo -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $exe = Join-Path $stage 'SysWitness.Api.exe'
    if (-not (Test-Path $exe)) { throw 'Self-contained portable executable missing.' }
    foreach ($file in @('Start-SysWitness.cmd', 'Start-SysWitness.ps1',
        'settings.default.json', 'README-PORTABLE-TH.md')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $root 'portable') $file) -Destination (Join-Path $stage $file) -ErrorAction Stop
    }
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE') -ErrorAction Stop
    Copy-Item (Join-Path $root 'docs\THIRD_PARTY_NOTICES.md') (Join-Path $stage 'THIRD_PARTY_NOTICES.md') -ErrorAction Stop
    Copy-Item (Join-Path $root 'docs\licenses\Apache-2.0.txt') (Join-Path $stage 'APACHE-2.0-LICENSE.txt') -ErrorAction Stop

    # Include upstream .NET runtime distribution terms with this self-contained binary.
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    foreach ($licenseFile in @(
        @{ src='LICENSE.txt'; dst='DOTNET_RUNTIME_LICENSE.txt' },
        @{ src='ThirdPartyNotices.txt'; dst='DOTNET_RUNTIME_THIRD_PARTY_NOTICES.txt' }
    )) {
        $upstream = Join-Path $dotnetRoot $licenseFile.src
        if (-not (Test-Path -LiteralPath $upstream)) {
            throw ('Missing upstream .NET redistributable notice: ' + $upstream)
        }
        Copy-Item -LiteralPath $upstream -Destination (Join-Path $stage $licenseFile.dst) -ErrorAction Stop
    }

    # Copy notice files from the WMI packages resolved by this publish.
    $assetsPath = Join-Path (Split-Path -Parent $proj) 'obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($package in @('System.Management', 'System.CodeDom')) {
        $library = $assets.libraries.PSObject.Properties |
            Where-Object { $_.Name.Split('/')[0] -eq $package } | Select-Object -First 1
        if (-not $library) { throw ('Missing published dependency in assets: ' + $package) }
        $notice = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path (Join-Path $folder $library.Value.path) 'THIRD-PARTY-NOTICES.TXT'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $notice = $candidate
                break
            }
        }
        if (-not $notice) { throw ('Missing upstream package notice: ' + $package) }
        Copy-Item -LiteralPath $notice -Destination (Join-Path $stage ($package + '-THIRD-PARTY-NOTICES.txt'))
    }

    # Check package staging cannot contain a user's runtime database/token.
    foreach ($privateName in @('config', 'data', 'logs', 'runtime',
        'owner-token.txt', 'client-keys.json', 'settings.json')) {
        if (Test-Path (Join-Path $stage $privateName)) {
            throw ('Private runtime file/folder appeared in build staging: ' + $privateName)
        }
    }
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
    if (-not (Test-Path $zip)) { throw 'Portable ZIP was not produced.' }
    Write-Output ('PORTABLE_ZIP=' + $zip)
    Write-Output ('PORTABLE_BYTES=' + (Get-Item $zip).Length)
} finally {
    Remove-Item -LiteralPath $stageParent -Recurse -Force -ErrorAction SilentlyContinue
}
