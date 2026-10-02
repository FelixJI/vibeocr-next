<#
.SYNOPSIS
Regenerates .NET package locks for the application and internal projects.

.DESCRIPTION
Uses direct project references for internal code and regenerates all
Next package locks with an isolated NuGet cache, then proves the committed
graph restores in locked mode from a second empty cache.

With -Upgrade, central package versions in Directory.Packages.props are first
moved to the latest stable release published on nuget.org (the only configured
source; prerelease versions are never selected), so the committed central
versions and package locks stay updated through this single entrypoint.
#>
[CmdletBinding()]
param(
    [switch]$InternalOnly,
    [switch]$Upgrade,
    [string[]]$Add
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

function Update-CentralPackageVersions {
    $centralProps = Join-Path $repo 'Directory.Packages.props'
    $content = Get-Content -LiteralPath $centralProps -Raw
    $changes = [System.Collections.Generic.List[string]]::new()
    $pattern = '(?s)<PackageVersion Include="(?<id>[^"]+)" Version="(?<version>[^"]+)" />'
    $content = [regex]::Replace($content, $pattern, {
        param($match)
        $packageId = $match.Groups['id'].Value
        $currentVersion = $match.Groups['version'].Value
        $versionsUri = "https://api.nuget.org/v3-flatcontainer/$($packageId.ToLowerInvariant())/index.json"
        $stableVersions = @((Invoke-RestMethod -Uri $versionsUri).versions |
            Where-Object { $_ -notmatch '-' })
        if ($stableVersions.Count -eq 0) {
            throw "nuget.org published no stable version for $packageId"
        }
        $latest = $stableVersions[-1]
        if ($latest -ne $currentVersion) {
            $changes.Add("$packageId : $currentVersion -> $latest")
            return ('<PackageVersion Include="{0}" Version="{1}" />' -f $packageId, $latest)
        }
        return $match.Value
    })
    $changes | ForEach-Object { Write-Host $_ }
    Set-Content -LiteralPath $centralProps -Value $content -NoNewline
    Write-Host "Central package versions checked against nuget.org: $($changes.Count) updated."
}

function Add-CentralPackageVersions {
    param([string[]]$Specs)
    # -File invocation flattens arrays; accept ';' or ',' separated specs too.
    $specs = @($Specs | ForEach-Object { $_ -split '[;,]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $centralProps = Join-Path $repo 'Directory.Packages.props'
    $content = Get-Content -LiteralPath $centralProps -Raw
    foreach ($spec in $specs) {
        $id, $version = $spec -split '=', 2
        if (-not $id -or -not $version -or $id -notmatch '^[A-Za-z0-9._-]+$' -or $version -notmatch '^[A-Za-z0-9._-]+$') {
            throw "invalid -Add entry '$spec' (expected <PackageId>=<version>)"
        }
        $entry = '<PackageVersion Include="{0}" Version="{1}" />' -f $id, $version
        if ($content -match [regex]::Escape($entry)) {
            continue
        }
        $existing = '<PackageVersion Include="{0}" Version="[^"]+" />' -f [regex]::Escape($id)
        if ($content -match $existing) {
            throw "$id is already pinned centrally; use -Upgrade to move it to the latest stable"
        }
        $itemGroupEnd = $content.IndexOf('  </ItemGroup>')
        if ($itemGroupEnd -lt 0) {
            throw 'Directory.Packages.props has no ItemGroup for central versions'
        }
        $content = $content.Insert($itemGroupEnd, "    $entry`n")
        Write-Host "$id : added at $version"
    }
    Set-Content -LiteralPath $centralProps -Value $content -NoNewline
}

if ($Add) {
    Add-CentralPackageVersions $Add
}

if ($Upgrade) {
    Update-CentralPackageVersions
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

# The isolated verify cache above is deleted, which would leave obj assets
# pointing at missing packages (and break MTP test-project classification).
# Rehydrate every project from the default user cache so the tree stays
# immediately buildable after the script finishes.
foreach ($project in $projects) {
    & $dotnet restore (Join-Path $repo $project) --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "default-cache rehydrate failed: $project"
    }
}

Write-Host 'Next .NET package locks regenerated and verified.'
