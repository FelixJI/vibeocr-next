[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$BuildRoot
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $root
$expectedBuildRoot = [IO.Path]::GetFullPath((Join-Path $root '.release-build'))
$requestedBuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if (-not [string]::Equals($requestedBuildRoot, $expectedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Runtime build output must be the checkout .release-build directory'
}
if ((Test-Path -LiteralPath $expectedBuildRoot) -and
    ((Get-Item -LiteralPath $expectedBuildRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Runtime build output must not be a reparse point'
}
$BuildRoot = $expectedBuildRoot
$versionRecord = Get-Content -LiteralPath (Join-Path $root 'repository.json') -Raw | ConvertFrom-Json
if ($Version -ne [string]$versionRecord.version) {
    throw 'Runtime build version must match repository.json'
}

$runtimeConfig = Join-Path $root 'config/runtime'
$pythonLock = Get-Content -LiteralPath (Join-Path $runtimeConfig 'python-runtime.lock.json') -Raw | ConvertFrom-Json
$pythonName = [IO.Path]::GetFileName(([uri]$pythonLock.source_url).AbsolutePath)
$pythonCache = Join-Path $root '.release-input/python'
New-Item -ItemType Directory -Path $pythonCache -Force | Out-Null
$pythonArchive = if ($env:VIBEOCR_PYTHON_ARCHIVE) {
    [IO.Path]::GetFullPath($env:VIBEOCR_PYTHON_ARCHIVE)
} else {
    Join-Path $pythonCache $pythonName
}
if (-not (Test-Path -LiteralPath $pythonArchive -PathType Leaf)) {
    if ($env:VIBEOCR_PYTHON_ARCHIVE) {
        throw 'Configured Python archive is missing'
    }
    $partial = "$pythonArchive.partial"
    Invoke-WebRequest -Uri $pythonLock.source_url -OutFile $partial -TimeoutSec 300
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonLock.sha256) {
        throw 'Downloaded Python archive does not match python-runtime.lock.json'
    }
    Move-Item -LiteralPath $partial -Destination $pythonArchive
}
if ((Get-FileHash -LiteralPath $pythonArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonLock.sha256) {
    throw 'Python archive does not match python-runtime.lock.json'
}

$work = Join-Path $BuildRoot 'runtime-source'
$output = Join-Path $BuildRoot 'runtime-bundle'
New-Item -ItemType Directory -Path $work, $output -Force | Out-Null
uv sync --frozen --group build
if ($LASTEXITCODE -ne 0) { throw 'Runtime builder dependency sync failed' }
uv build --wheel --out-dir $work
if ($LASTEXITCODE -ne 0) { throw 'Next Runtime wheel build failed' }
$wheels = @(Get-ChildItem -LiteralPath $work -Filter "vibeocr_next_runtime-$Version-*.whl" -File)
if ($wheels.Count -ne 1) { throw 'Expected exactly one Next Runtime wheel' }

uv run --frozen --group build python (Join-Path $root 'scripts/build_runtime_installer.py') `
    --output-dir $work --work-dir (Join-Path $work 'installer-work') --version $Version
if ($LASTEXITCODE -ne 0) { throw 'Runtime Installer build failed' }
$installer = Join-Path $work "vibeocr-runtime-installer-v$Version-win-x64.zip"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'Frozen Runtime Installer archive is missing'
}

$baseLock = Join-Path $runtimeConfig 'win-x64-base/requirements-win-x64-base.lock'
uv run --frozen --group build python (Join-Path $root 'scripts/build_runtime_pack.py') `
    --lock $baseLock --profile win-x64-base `
    --work-dir (Join-Path $work 'runtime-pack-work-base') `
    --max-part-bytes 1825361100 `
    --output (Join-Path $work "vibeocr-runtime-pack-win-x64-base-$Version.zip")
if ($LASTEXITCODE -ne 0) { throw 'Offline base Runtime pack build failed' }
$basePacks = @(Get-ChildItem -LiteralPath $work -Filter "vibeocr-runtime-pack-win-x64-base-$Version.part*.zip" -File | Sort-Object Name)
if ($basePacks.Count -lt 1) { throw 'Offline base Runtime pack is missing' }
$packArgs = @()
foreach ($pack in $basePacks) { $packArgs += @('--base-runtime-pack', $pack.FullName) }
$sourceSha = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve Next source commit' }
$manifestArgs = @(
    (Join-Path $root 'scripts/build_runtime_manifest.py'),
    '--runtime-wheel', $wheels[0].FullName,
    '--base-lock', $baseLock,
    '--cpu-lock', (Join-Path $runtimeConfig 'win-x64-cpu/requirements-win-x64-cpu.lock'),
    '--cu126-lock', (Join-Path $runtimeConfig 'win-x64-cu126/requirements-win-x64-cu126.lock'),
    '--cu126-gpu-lock', (Join-Path $runtimeConfig 'win-x64-cu126-gpu/requirements-win-x64-cu126-gpu.lock'),
    '--paddle-cpu-lock', (Join-Path $runtimeConfig 'win-x64-paddle-cpu/requirements-win-x64-paddle-cpu.lock'),
    '--paddle-cu126-lock', (Join-Path $runtimeConfig 'win-x64-paddle-cu126/requirements-win-x64-paddle-cu126.lock'),
    '--mineru-cpu-lock', (Join-Path $runtimeConfig 'win-x64-mineru-cpu/requirements-win-x64-mineru-cpu.lock'),
    '--python-archive', $pythonArchive,
    '--python-version', [string]$pythonLock.version,
    '--python-source-url', [string]$pythonLock.source_url,
    '--installer-archive', $installer,
    '--version', $Version,
    '--source-commit', $sourceSha,
    '--build-workflow', 'github.com/FelixJI/vibeocr-next/.github/workflows/ci.yml',
    '--output-dir', $output
) + $packArgs
uv run --frozen --group build python @manifestArgs
if ($LASTEXITCODE -ne 0) { throw 'Next Runtime manifest build failed' }
Write-Output $output
