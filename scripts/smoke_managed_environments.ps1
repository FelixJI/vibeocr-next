[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [int]$TimeoutMinutes = 120
)

# 预构建运行时树冒烟（PR2）：解释器与依赖闭包随产品分发、运行时零安装。
# 桌面 fixture 只验证随包 paddleocr-ort-directml 产品环境（kind=product）
# 已注册、active 指向它且推理会话已附着；安装/进度/取消事务已退役。
$ErrorActionPreference = 'Stop'
if ($TimeoutMinutes -le 0) { throw 'TimeoutMinutes must be positive' }
$source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
$work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ProductRoot and WorkRoot must not nest'
}
foreach ($marker in @(
    'app\VibeOCR.WinUI.exe',
    'app\metadata\product-layout.json',
    'runtime\backend\runtime-manifest.json',
    'runtime\env\runtime-env\python.exe',
    'runtime\installer\vibeocr-runtime-installer.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
        throw "Candidate marker missing: $marker"
    }
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) {
    throw 'Source candidate must not contain user or previous smoke state'
}

$smokeRoot = Join-Path $work "ve-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$webViewData = Join-Path $smokeRoot 'webview2'
New-Item -ItemType Directory -Path $candidate | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Copy-Item -Destination $candidate -Recurse -Force

$previousSmoke = $env:VIBEOCR_SELF_TEST_SMOKE
$previousInstance = $env:VIBEOCR_SELF_TEST_INSTANCE
$previousHealth = $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH
$previousPhase = $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE
$previousWebViewData = $env:WEBVIEW2_USER_DATA_FOLDER

function Invoke-ManagedPhase([string]$phase) {
    $healthPath = Join-Path $smokeRoot "managed-environment-$phase.json"
    $env:VIBEOCR_SELF_TEST_SMOKE = 'managed-environment-e2e'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH = $healthPath
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE = $phase
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewData
    $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
    # restart 阶段语义保留生产启动路径（桌面按 phase 名决定后台启动）：
    # 首次启动即在后台 initialize_default 注册产品环境并启动 Supervisor，
    # fixture 只验证事实。
    $process = Start-Process -FilePath $executable `
        -ArgumentList "--profile production --install-root `"$candidate`"" `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
        $process.Kill($true)
        if (-not $process.WaitForExit(5000)) {
            throw "Managed environment $phase process tree did not exit after forced termination"
        }
        throw "Managed environment $phase timed out after $TimeoutMinutes minutes"
    }
    if ($process.ExitCode -ne 0) {
        throw "Managed environment $phase exited without health evidence (exit $($process.ExitCode))"
    }
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) { throw "Health evidence missing: $healthPath" }
    $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
    if ($health.state -ne 'passed' -or $health.phase -ne $phase) {
        throw "Managed environment $phase failed: $($health.error)"
    }
    return $health
}

try {
    $prebuilt = Invoke-ManagedPhase 'restart'
    $environment = $prebuilt.evidence.environment
    if ($environment.kind -ne 'product' -or
        $environment.recipe -ne 'paddleocr-ort-directml' -or
        $environment.status -ne 'installed' -or
        $environment.python_state -ne 'ready') {
        throw "Prebuilt product environment is not healthy: $($environment | ConvertTo-Json -Compress)"
    }
    if ($prebuilt.evidence.active_id -ne $environment.id -or
        $prebuilt.evidence.service_state -ne 'Ready') {
        throw 'Active environment does not point at the healthy prebuilt product environment'
    }
    Write-Host "Prebuilt managed environment smoke passed. Isolated evidence retained at: $smokeRoot"
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH = $previousHealth
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE = $previousPhase
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
}
