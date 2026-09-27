<#
.SYNOPSIS
Regenerates .NET package locks for the application and internal projects.

.DESCRIPTION
Uses direct project references for internal code and regenerates all
Next package locks with an isolated NuGet cache, then proves the committed
graph restores in locked mode from a second empty cache.
#>
[CmdletBinding()]
param(
    [switch]$InternalOnly
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = if ($env:DOTNET_ROOT) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} else {
    Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw '64-bit dotnet SDK is required'
}

$internalProjects = @(
    'tests\dotnet\VibeOCR.Contracts.Tests\VibeOCR.Contracts.Tests.csproj',
    'tests\dotnet\VibeOCR.Runtime.Client.Tests\VibeOCR.Runtime.Client.Tests.csproj'
)

if ($InternalOnly) {
    foreach ($project in $internalProjects) {
        & $dotnet restore (Join-Path $repo $project) -p:UpdatePackageLocks=true
        if ($LASTEXITCODE -ne 0) {
            throw "internal lock regeneration failed: $project"
        }
        & $dotnet restore (Join-Path $repo $project) --locked-mode
        if ($LASTEXITCODE -ne 0) {
            throw "internal locked restore failed: $project"
        }
    }
    Write-Host 'Internal .NET package locks regenerated and verified.'
    return
}

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
) + $internalProjects

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

Write-Host 'Next .NET package locks regenerated and verified.'
