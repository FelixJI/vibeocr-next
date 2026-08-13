<#
.SYNOPSIS
Regenerates .NET package locks from the published Protocol release.

.DESCRIPTION
Reads the exact Protocol SDK pin from Directory.Packages.props, downloads and
verifies that immutable release's NUPKGs, regenerates all
Next package locks with an isolated NuGet cache, then proves the committed
graph restores in locked mode from a second empty cache.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$feed = Join-Path $repo '.release-input\protocol-sdk'
$packagePropsPath = Join-Path $repo 'Directory.Packages.props'
$dotnet = if ($env:DOTNET_ROOT) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} else {
    Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw '64-bit dotnet SDK is required'
}

[xml]$packageProps = Get-Content -LiteralPath $packagePropsPath -Raw
$protocolPackageNames = @(
    'VibeOCR.Runtime.Contracts',
    'VibeOCR.Runtime.Client'
)
$protocolPins = @(
    $packageProps.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -in $protocolPackageNames }
)
if ($protocolPins.Count -ne $protocolPackageNames.Count) {
    throw 'Directory.Packages.props must pin both Protocol SDK packages'
}
$protocolVersions = @(
    $protocolPins | ForEach-Object {
        $match = [regex]::Match([string]$_.Version, '^\[(\d+\.\d+\.\d+)\]$')
        if (-not $match.Success) {
            throw "Protocol SDK package must use an exact stable pin: $($_.Include)"
        }
        $match.Groups[1].Value
    } | Select-Object -Unique
)
if ($protocolVersions.Count -ne 1) {
    throw 'Protocol SDK packages must use the same exact version'
}
$protocolVersion = $protocolVersions[0]
$protocolPackagePattern = "VibeOCR.Runtime.*.$protocolVersion.nupkg"

$tempRoot = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) 'vibeocr-next-lock-update')
)
$systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
if (-not $tempRoot.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'unsafe NuGet cache path'
}

$projects = @(
    'tests\dotnet\VibeOCR.Platform.Tests\VibeOCR.Platform.Tests.csproj',
    'tests\dotnet\VibeOCR.App.Tests\VibeOCR.App.Tests.csproj',
    'src\dotnet\VibeOCR.App\VibeOCR.App.csproj',
    'src\dotnet\VibeOCR.Bootstrapper\VibeOCR.Bootstrapper.csproj'
)

if (Test-Path -LiteralPath $feed) {
    Remove-Item -LiteralPath $feed -Recurse -Force
}
New-Item -ItemType Directory -Path $feed | Out-Null

gh release download "v$protocolVersion" `
    --repo FelixJI/vibeocr-protocol `
    --pattern $protocolPackagePattern `
    --dir $feed
if ($LASTEXITCODE -ne 0) {
    throw 'Protocol NUPKG download failed'
}

$packages = @(
    Get-ChildItem -LiteralPath $feed -Filter $protocolPackagePattern
)
if ($packages.Count -ne 2) {
    throw "expected two Protocol NUPKGs, found $($packages.Count)"
}
foreach ($package in $packages) {
    gh attestation verify $package.FullName --repo FelixJI/vibeocr-protocol
    if ($LASTEXITCODE -ne 0) {
        throw "Protocol attestation verification failed: $($package.Name)"
    }
}

try {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
    $regenCache = Join-Path $tempRoot 'regenerate'
    New-Item -ItemType Directory -Path $regenCache | Out-Null
    $env:NUGET_PACKAGES = $regenCache
    foreach ($project in $projects) {
        & $dotnet restore (Join-Path $repo $project) `
            --force-evaluate `
            --no-cache `
            -p:UpdatePackageLocks=true
        if ($LASTEXITCODE -ne 0) {
            throw "lock regeneration failed: $project"
        }
    }

    $verifyCache = Join-Path $tempRoot 'verify'
    New-Item -ItemType Directory -Path $verifyCache | Out-Null
    $env:NUGET_PACKAGES = $verifyCache
    foreach ($project in $projects) {
        & $dotnet restore (Join-Path $repo $project) --locked-mode --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "locked restore verification failed: $project"
        }
    }
}
finally {
    Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "Next .NET package locks regenerated and verified from Protocol v$protocolVersion."
