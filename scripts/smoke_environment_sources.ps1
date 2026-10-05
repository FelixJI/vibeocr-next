[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [int]$TimeoutMinutes = 45
)

# 下载来源 smoke：真实 Windows/WebView2 公开 UI，从两个空环境完成
# 统一来源保存 → 跟随配置预览 → 确认 → 取消（合成中断）→ 重新预览，
# 验证统一设置与失败终态来源证据持久；全程不完整安装、不启动 OCR。
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
    'runtime\installer\vibeocr-runtime-installer.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
        throw "Candidate marker missing: $marker"
    }
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) {
    throw 'Source candidate must not contain user or previous smoke state'
}

$smokeRoot = Join-Path $work "src-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
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

try {
    $healthPath = Join-Path $smokeRoot 'managed-environment-sources.json'
    $env:VIBEOCR_SELF_TEST_SMOKE = 'managed-environment-e2e'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH = $healthPath
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE = 'sources'
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewData
    $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
    $process = Start-Process -FilePath $executable `
        -ArgumentList "--profile production --shell-only --install-root `"$candidate`"" `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
        $process.Kill($true)
        if (-not $process.WaitForExit(5000)) {
            throw 'Source settings smoke process tree did not exit after forced termination'
        }
        throw "Source settings smoke timed out after $TimeoutMinutes minutes"
    }
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "Source settings smoke exited without health evidence (exit $($process.ExitCode))"
    }
    $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $health.schema_version -ne 1 -or
        $health.state -ne 'passed' -or $health.phase -ne 'sources') {
        throw "Source settings smoke failed: $($health.error)"
    }
    $evidence = $health.evidence

    if ($evidence.install_attempts -ne 1) {
        throw 'Expected exactly one real install attempt before cancellation'
    }
    if ($evidence.service_attached) {
        throw 'Source settings smoke must not start an OCR service'
    }
    if (@($evidence.default_source_ids).Count -ne 3 -or
        @($evidence.default_source_ids) -notcontains 'pypi' -or
        @($evidence.default_source_ids) -notcontains 'paddleocr-modelscope' -or
        @($evidence.default_source_ids) -notcontains 'mineru-modelscope') {
        throw 'Unified sources were not saved as PyPI and ModelScope'
    }
    if ($evidence.a.status -ne 'empty' -or $evidence.a.revision -ne 1 -or
        $evidence.a.resolved_package -ne 'pypi' -or
        $evidence.a.resolved_package_origin -ne 'global_default') {
        throw "Environment A did not inherit the global default: $($evidence.a | ConvertTo-Json -Compress)"
    }
    if ($evidence.b.status -ne 'empty' -or $evidence.b.revision -ne 1 -or
        $evidence.b.resolved_package -ne 'pypi' -or
        @($evidence.b.override_source_ids).Count -ne 0 -or
        $evidence.b.resolved_paddleocr_model -ne 'paddleocr-modelscope' -or
        $evidence.b.resolved_mineru_model -ne 'mineru-modelscope') {
        throw "Environment B did not follow unified sources: $($evidence.b | ConvertTo-Json -Compress)"
    }
    $failure = $evidence.a_failure
    if ($failure.phase -ne 'failed' -or $failure.reason_code -ne 'install_interrupted' -or
        $null -ne $failure.requested_source_ids -or
        @($failure.effective_source_ids).Count -ne 3 -or
        @($failure.effective_source_ids) -notcontains 'pypi' -or
        @($failure.effective_source_ids) -notcontains 'paddleocr-modelscope' -or
        @($failure.effective_source_ids) -notcontains 'mineru-modelscope') {
        throw "Cancelled install lost its frozen source evidence: $($failure | ConvertTo-Json -Compress)"
    }
    Write-Host 'Environment source settings E2E passed: unified sources, follow preview, cancel durability, unchanged peer installation.'
    Write-Host "Isolated evidence retained at: $smokeRoot"
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH = $previousHealth
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE = $previousPhase
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
}
