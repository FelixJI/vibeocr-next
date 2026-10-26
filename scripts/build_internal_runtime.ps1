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
    --output-dir $work --work-dir (Join-Path $work 'installer-work')
if ($LASTEXITCODE -ne 0) { throw 'Runtime Installer build failed' }
$installer = Join-Path $work "vibeocr-runtime-installer.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'Frozen Runtime Installer executable is missing'
}

$baseLock = Join-Path $runtimeConfig 'win-x64-base/requirements-win-x64-base.lock'
# 共享离线 wheel pool：base / Paddle ONNX CPU / DirectML 的闭包取并集，
# 每个相同 wheel 只随产品分发一次；同名不同版本（冲突 pin）同时保留。
# pool 只含去重 wheel；每个成员 lock 另产一个仅含 pack-requirements.txt
# 的小 zip，经既有可重复 runtime_pack 元组接口绑定（requirements zip 在
# 首位，各 scope 缓存目录独立），安装环境仍彼此隔离，不混装 CPU/DirectML
# ONNX namespace。CUDA 闭包不随包（按需在线安装），模型包一次构建由三种
# paddle-ort 环境共用。
$cpuOrtLock = Join-Path $runtimeConfig 'win-x64-paddle-ort-cpu/requirements-win-x64-paddle-ort-cpu.lock'
$dmlOrtLock = Join-Path $runtimeConfig 'win-x64-paddle-ort-directml/requirements-win-x64-paddle-ort-directml.lock'
$poolLockArgs = @(
    '--pool-lock', "win-x64-base=$baseLock",
    '--pool-lock', "win-x64-paddle-ort-cpu=$cpuOrtLock",
    '--pool-lock', "win-x64-paddle-ort-directml=$dmlOrtLock"
)
uv run --frozen --group build python (Join-Path $root 'scripts/build_runtime_pack.py') `
    --profile win-x64-shared-pool `
    @poolLockArgs `
    --work-dir (Join-Path $work 'runtime-pool-work') `
    --max-part-bytes 1825361100 `
    --output (Join-Path $work "vibeocr-runtime-pool-win-x64-$Version.zip")
if ($LASTEXITCODE -ne 0) { throw 'Offline shared wheel pool build failed' }
$poolStem = "vibeocr-runtime-pool-win-x64-$Version"
$poolParts = @(Get-ChildItem -LiteralPath $work -Filter "$poolStem.part*.zip" -File | Sort-Object Name)
if ($poolParts.Count -lt 1) { throw 'Offline shared wheel pool is missing' }
# 统一用完整路径字符串拼接：Get-ChildItem 返回 FileInfo，Join-Path 返回
# 字符串，混用 .FullName 会在字符串上取空。
$poolPartPaths = @($poolParts | ForEach-Object { $_.FullName })
$basePoolReq = Join-Path $work "$poolStem.requirements-win-x64-base.zip"
$cpuPoolReq = Join-Path $work "$poolStem.requirements-win-x64-paddle-ort-cpu.zip"
$dmlPoolReq = Join-Path $work "$poolStem.requirements-win-x64-paddle-ort-directml.zip"
foreach ($requirementsZip in @($basePoolReq, $cpuPoolReq, $dmlPoolReq)) {
    if (-not (Test-Path -LiteralPath $requirementsZip -PathType Leaf)) {
        throw "Offline shared wheel pool requirements manifest is missing: $requirementsZip"
    }
}
$packArgs = @()
foreach ($pack in @($basePoolReq) + $poolPartPaths) { $packArgs += @('--base-runtime-pack', $pack) }
$paddlePackArgs = @()
foreach ($pack in @($cpuPoolReq) + $poolPartPaths) { $paddlePackArgs += @('--paddle-ort-cpu-runtime-pack', $pack) }
foreach ($pack in @($dmlPoolReq) + $poolPartPaths) { $paddlePackArgs += @('--paddle-ort-directml-runtime-pack', $pack) }

uv run --frozen --group build python (Join-Path $root 'scripts/build_runtime_pack.py') `
    --profile paddle-onnx-models `
    --work-dir (Join-Path $work 'runtime-pack-work-paddle-models') `
    --output (Join-Path $work "vibeocr-paddle-models-$Version.zip")
if ($LASTEXITCODE -ne 0) { throw 'Offline Paddle ONNX model pack build failed' }
$paddleModelPack = Join-Path $work "vibeocr-paddle-models-$Version.zip"
if (-not (Test-Path -LiteralPath $paddleModelPack -PathType Leaf)) {
    throw 'Offline Paddle ONNX model pack is missing'
}
$sourceSha = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve Next source commit' }
$manifestArgs = @(
    (Join-Path $root 'scripts/build_runtime_manifest.py'),
    '--runtime-wheel', $wheels[0].FullName,
    '--base-lock', $baseLock,
    '--cpu-lock', (Join-Path $runtimeConfig 'win-x64-cpu/requirements-win-x64-cpu.lock'),
    '--cu126-lock', (Join-Path $runtimeConfig 'win-x64-cu126/requirements-win-x64-cu126.lock'),
    '--cu126-gpu-lock', (Join-Path $runtimeConfig 'win-x64-cu126-gpu/requirements-win-x64-cu126-gpu.lock'),
    '--paddle-ort-cpu-lock', (Join-Path $runtimeConfig 'win-x64-paddle-ort-cpu/requirements-win-x64-paddle-ort-cpu.lock'),
    '--paddle-ort-cuda-lock', (Join-Path $runtimeConfig 'win-x64-paddle-ort-cuda/requirements-win-x64-paddle-ort-cuda.lock'),
    '--paddle-ort-directml-lock', (Join-Path $runtimeConfig 'win-x64-paddle-ort-directml/requirements-win-x64-paddle-ort-directml.lock'),
    '--paddle-model-pack', $paddleModelPack,
    '--python-archive', $pythonArchive,
    '--python-version', [string]$pythonLock.version,
    '--python-source-url', [string]$pythonLock.source_url,
    '--installer-executable', $installer,
    '--version', $Version,
    '--source-commit', $sourceSha,
    '--build-workflow', 'github.com/FelixJI/vibeocr-next/.github/workflows/ci.yml',
    '--output-dir', $output
) + $packArgs + $paddlePackArgs
uv run --frozen --group build python @manifestArgs
if ($LASTEXITCODE -ne 0) { throw 'Next Runtime manifest build failed' }
Write-Output $output
