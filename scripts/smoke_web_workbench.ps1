[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [int]$TimeoutSeconds = 30,
    [string]$DiagnosticsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '.test-results/web-workbench')
)

$ErrorActionPreference = 'Stop'
function Save-SmokeFailureDiagnostics {
    param([string]$FailureKind)

    $diagnostics = [ordered]@{
        schema_version = 1
        failure_kind = $FailureKind
        deadline_seconds = $TimeoutSeconds
        process_started = $null -ne $process
        process_exited = $null -ne $process -and $process.HasExited
        exit_code = if ($null -ne $process -and $process.HasExited) { $process.ExitCode } else { $null }
        state = 'no-health-signal'
        stage = 'before-app-signal'
        error_type = $null
        error_code = $null
        app_signals = @()
    }
    if (Test-Path -LiteralPath $healthFile -PathType Leaf) {
        try {
            $health = Get-Content -LiteralPath $healthFile -Raw | ConvertFrom-Json
            foreach ($field in @('state', 'stage', 'error_type')) {
                $value = [string]$health.$field
                if ($value -match '^[A-Za-z0-9_.:-]{1,100}$') { $diagnostics[$field] = $value }
            }
            if ($health.error_code -is [int] -or $health.error_code -is [long]) {
                $diagnostics.error_code = $health.error_code
            }
        } catch {
            $diagnostics.state = 'invalid-health-json'
        }
    }
    # Only fixed lifecycle signals are retained. Do not copy complete app logs,
    # exception stacks, launch envelopes, URLs or process environment variables.
    $logRoot = Join-Path $isolatedRoot 'state/logs'
    if (Test-Path -LiteralPath $logRoot -PathType Container) {
        $diagnostics.app_signals = @(Get-ChildItem -LiteralPath $logRoot -Filter 'winui-dev-*.log' -File |
            ForEach-Object { Get-Content -LiteralPath $_.FullName } |
            Where-Object {
                $_ -match '^\d{2}:\d{2}:\d{2}\.\d{3} \[(INFO |ERROR|WARN )\] (Web workbench: [A-Za-z0-9_.:-]+|OnLaunched: profile=production shellOnly=True|Web workbench (initialization|resource smoke) failed:|Web workbench smoke health unavailable: [A-Za-z0-9]+)$'
            } | Select-Object -Last 24)
    }
    $destination = Join-Path ([System.IO.Path]::GetFullPath($DiagnosticsRoot)) ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $diagnostics | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $destination 'failure.json') -Encoding utf8
    Write-Host "Web workbench failure: kind=$FailureKind state=$($diagnostics.state) stage=$($diagnostics.stage) error=$($diagnostics.error_type) code=$($diagnostics.error_code)"
    foreach ($signal in $diagnostics.app_signals) { Write-Host $signal }
    Write-Host "Web workbench diagnostic evidence: $destination"
}

$sourceRoot = (Resolve-Path -LiteralPath $ProductRoot).Path
$layoutDescriptor = Join-Path $sourceRoot 'app\metadata\product-layout.json'
$installedLayout = Test-Path -LiteralPath $layoutDescriptor -PathType Leaf
if ($installedLayout) {
    $scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    & uv run --no-project python (Join-Path $scriptsRoot 'product_layout.py') inspect `
        --product-root $sourceRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Web workbench smoke product layout is invalid'
    }
    $layout = Get-Content -LiteralPath $layoutDescriptor -Raw | ConvertFrom-Json
    $relativeExecutable = [string]$layout.app.entry
} else {
    # build-release smoke also accepts the explicit dotnet publish output.
    $relativeExecutable = 'VibeOCR.WinUI.exe'
}
$sourceExecutable = Join-Path $sourceRoot $relativeExecutable
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Web workbench smoke executable is missing: $sourceExecutable"
}
if ($TimeoutSeconds -le 0) {
    throw 'Web workbench smoke timeout must be positive'
}

$healthFile = Join-Path ([System.IO.Path]::GetTempPath()) `
    "vibeocr-web-ready-$([guid]::NewGuid().ToString('N')).json"
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$isolatedRoot = Join-Path $tempRoot `
    "vibeocr-web-smoke-$([guid]::NewGuid().ToString('N'))"
$webViewDataRoot = Join-Path $tempRoot `
    "vibeocr-webview-smoke-$([guid]::NewGuid().ToString('N'))"
if (-not ([System.IO.Path]::GetFullPath($isolatedRoot).StartsWith(
    $tempRoot,
    [System.StringComparison]::OrdinalIgnoreCase))) {
    throw 'Web workbench smoke isolation path escaped the temporary directory'
}
$previousSmoke = $env:VIBEOCR_SELF_TEST_SMOKE
$previousInstance = $env:VIBEOCR_SELF_TEST_INSTANCE
$previousHealth = $env:VIBEOCR_WEB_READY_FILE
$previousWebViewData = $env:WEBVIEW2_USER_DATA_FOLDER
$process = $null
$failureKind = 'process-or-health-failure'
try {
    New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
    Get-ChildItem -LiteralPath $sourceRoot -Force |
        Copy-Item -Destination $isolatedRoot -Recurse -Force
    $executable = Join-Path $isolatedRoot $relativeExecutable
    $env:VIBEOCR_SELF_TEST_SMOKE = 'web-ready'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_WEB_READY_FILE = $healthFile
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewDataRoot
    $arguments = @('--shell-only', '--profile', 'production')
    if ($installedLayout) {
        $arguments += @('--install-root', $isolatedRoot)
    }
    $process = Start-Process -FilePath $executable `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $failureKind = 'deadline-exceeded'
        $process.Kill($true)
        if (-not $process.WaitForExit(5000)) {
            throw 'Web workbench process tree did not exit after forced termination'
        }
        throw "Web workbench did not complete startup, resource/layout verification and exit within $TimeoutSeconds seconds"
    }
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $healthFile -PathType Leaf) {
            $failedHealth = Get-Content -LiteralPath $healthFile -Raw | ConvertFrom-Json
            "state=$($failedHealth.state) stage=$($failedHealth.stage) error=$($failedHealth.error_type) code=$($failedHealth.error_code)"
        } else { 'no health signal' }
        throw "Web workbench smoke exited with code $($process.ExitCode): $detail"
    }
    if (-not (Test-Path -LiteralPath $healthFile -PathType Leaf)) {
        throw 'Web workbench did not write its bridge-ready health signal'
    }
    $health = Get-Content -LiteralPath $healthFile -Raw | ConvertFrom-Json
    if ($health.schema_version -ne 1 -or $health.state -ne 'bridge-ready' -or
        $health.resources -ne 'verified' -or $health.layout_sizes_verified -ne 2 -or
        $health.owner_restore -ne 'verified') {
        throw 'Web workbench health signal is invalid'
    }
    Write-Host 'Web workbench smoke verified: bridge-ready, resource GET and annotation POST, visible/minimized/hidden owner-window restoration.'
} catch {
    # Capture before cleanup removes the health file and portable state/logs.
    $originalFailure = $_
    try {
        Save-SmokeFailureDiagnostics -FailureKind $failureKind
    } catch {
        Write-Warning "Web workbench diagnostic capture failed: $($_.Exception.GetType().Name)"
    }
    throw $originalFailure
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
    $env:VIBEOCR_WEB_READY_FILE = $previousHealth
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
    foreach ($signalFile in @($healthFile, "$healthFile.writing")) {
        if (Test-Path -LiteralPath $signalFile) {
            Remove-Item -LiteralPath $signalFile -Force
        }
    }
    if (Test-Path -LiteralPath $isolatedRoot) {
        $resolvedIsolation = [System.IO.Path]::GetFullPath($isolatedRoot)
        if (-not $resolvedIsolation.StartsWith(
            $tempRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove smoke isolation outside the temporary directory'
        }
        # 便携 state 可能携带 WebView2 user-data;先终止引用隔离根的浏览器
        # 进程,避免删除竞态。
        Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" |
            Where-Object { $_.CommandLine -like "*$resolvedIsolation*" } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
        $removed = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedIsolation -Recurse -Force
                $removed = $true
                break
            } catch {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
        if (-not $removed) {
            throw 'Web workbench smoke isolation cleanup did not complete'
        }
    }
    Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like "*$webViewDataRoot*" } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    if (Test-Path -LiteralPath $webViewDataRoot) {
        $resolvedWebViewData = [System.IO.Path]::GetFullPath($webViewDataRoot)
        if (-not $resolvedWebViewData.StartsWith(
            $tempRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove WebView2 smoke data outside the temporary directory'
        }
        $removed = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedWebViewData -Recurse -Force
                $removed = $true
                break
            } catch {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
        if (-not $removed) {
            throw 'WebView2 smoke data cleanup did not complete'
        }
    }
}
