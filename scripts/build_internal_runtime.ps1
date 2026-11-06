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


$baseLock = Join-Path $runtimeConfig 'win-x64-base/requirements-win-x64-base.lock'
# 预构建运行时树（PR2）：构建期把 CPython 归档安全解包，统一闭包
# （base + DirectML 统一推理闭包，含内部 runtime wheel）用构建机 uv 严格
# 离线预装进树的 site-packages，并执行 import 冒烟与确定性树清单。产品
# 直接携带展开树；运行时零 venv、零 uv、零安装。同名依赖在单树环境必须
# 唯一版本（构建期 fail closed），wheel 池按字节去重只作为预装输入。
$dmlOrtLock = Join-Path $runtimeConfig 'win-x64-paddle-ort-directml/requirements-win-x64-paddle-ort-directml.lock'
$runtimeEnvDir = Join-Path $work 'runtime-env'
$runtimeEnvManifest = Join-Path $work 'runtime-env-manifest.json'
uv run --frozen --group build python (Join-Path $root 'scripts/build_runtime_pack.py') `
    --profile prebuilt-runtime-env `
    --pool-lock "win-x64-base=$baseLock" `
    --pool-lock "win-x64-paddle-ort-directml=$dmlOrtLock" `
    --python-archive $pythonArchive `
    --python-sha256 ([string]$pythonLock.sha256) `
    --python-version ([string]$pythonLock.version) `
    --runtime-wheel $wheels[0].FullName `
    --runtime-version $Version `
    --env-manifest $runtimeEnvManifest `
    --work-dir (Join-Path $work 'runtime-env-work') `
    --output $runtimeEnvDir
if ($LASTEXITCODE -ne 0) { throw 'Prebuilt runtime env build failed' }
if (-not (Test-Path -LiteralPath (Join-Path $runtimeEnvDir 'python.exe') -PathType Leaf)) {
    throw 'Prebuilt runtime env is missing python.exe'
}
if (-not (Test-Path -LiteralPath $runtimeEnvManifest -PathType Leaf)) {
    throw 'Prebuilt runtime env manifest is missing'
}

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
    '--base-lock', $baseLock,
    '--paddle-ort-directml-lock', $dmlOrtLock,
    '--paddle-model-pack', $paddleModelPack,
    '--runtime-env-tree', $runtimeEnvDir,
    '--runtime-env-manifest', $runtimeEnvManifest,
    '--python-version', [string]$pythonLock.version,
    '--python-source-url', [string]$pythonLock.source_url,
    '--version', $Version,
    '--source-commit', $sourceSha,
    '--build-workflow', 'github.com/FelixJI/vibeocr-next/.github/workflows/ci.yml',
    '--output-dir', $output
)
uv run --frozen --group build python @manifestArgs
if ($LASTEXITCODE -ne 0) { throw 'Next Runtime manifest build failed' }
Write-Output $output
