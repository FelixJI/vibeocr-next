[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [int]$TimeoutSeconds = 30,
    [string]$DiagnosticsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '.test-results/web-workbench'),
    # 仅供 release_smoke 使用:复用它已完整解包、校验过的 current 树,
    # 避免对预构建 Runtime 做第二次整树复制。默认(直接 publish 输出等)
    # 仍走完整复制隔离分支。
    [switch]$ReuseUnpackedProduct
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

function Assert-ReusedUnpackedProductRoot {
    param([string]$SourceRoot)

    # 仅接受 release_smoke 独占 TemporaryDirectory 下的 current 解包树:
    # <当前有效TEMP>\vibeocr-release-smoke-<suffix>\current。逐层精确
    # 比较(不用宽松 StartsWith),拒绝 reparse 与任何既有产品数据,防止
    # 把外部目录当作一次性解包树启动或删除。父目录 state/logs 是
    # Bootstrapper 的合法写入,不在此拒绝。
    $normalized = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
    $tempRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath()).TrimEnd('\')
    if ([System.IO.Path]::GetFileName($normalized) -cne 'current') {
        throw "Web workbench reuse requires the unpacked product root to be named 'current': $normalized"
    }
    $holder = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $normalized)).TrimEnd('\')
    if ([System.IO.Path]::GetFileName($holder) -cnotmatch '^vibeocr-release-smoke-[a-z0-9_]+$') {
        throw "Web workbench reuse requires a dedicated vibeocr-release-smoke- temporary holder: $holder"
    }
    $tempLayer = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $holder)).TrimEnd('\')
    if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals($tempLayer, $tempRoot)) {
        throw "Web workbench reuse holder must be a direct child of the current TEMP: $holder"
    }
    foreach ($layer in @($holder, $normalized)) {
        if ((Get-Item -LiteralPath $layer -Force).Attributes -band
            [System.IO.FileAttributes]::ReparsePoint) {
            throw "Web workbench reuse rejects reparse points in the unpacked product path: $layer"
        }
    }
    # state/portable-layout.json 可能承载真实用户数据;用目录项枚举而非
    # Test-Path,连 dangling link 也一并拒绝。
    $existing = @(Get-ChildItem -LiteralPath $normalized -Force |
        Where-Object { $_.Name -in @('state', 'portable-layout.json') })
    if ($existing.Count -gt 0) {
        throw "Web workbench reuse requires a pristine unpacked product, found existing data: $(($existing.Name) -join ', ')"
    }
}

function Stop-OwnedWebView2 {
    param([string]$ExpectedExecutable)

    $timeoutMs = 5000
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = $timeoutMs - $clock.ElapsedMilliseconds
        if ($remaining -le 0) { throw 'WebView2 cleanup deadline expired before confirming an empty owned process set' }
        $instances = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" `
            -Property ProcessId, ExecutablePath, CreationDate -OperationTimeoutSec ([int][Math]::Ceiling($remaining / 1000.0)))
        if ($clock.ElapsedMilliseconds -ge $timeoutMs) { throw 'WebView2 cleanup deadline expired during process census' }
        $rescan = $false
        foreach ($row in $instances) {
            $path = [string]$row.ExecutablePath
            if ($path -and -not $path.Equals($ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) { continue }

            try {
                $browser = Get-Process -Id $row.ProcessId -ErrorAction Stop
            } catch {
                if (-not $path) { continue }
                if (($_.FullyQualifiedErrorId -split ',', 2)[0] -ne 'NoProcessFoundForGivenId') { throw }
                $rescan = $true
                continue
            }
            try {
                if (-not $path) {
                    # An unreadable system-wide row does not establish ownership of this isolated runtime.
                    try { $path = [string]$browser.Path } catch { continue }
                    if (-not $path -or -not $path.Equals($ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) { continue }
                }
                $identityFailure = $null
                try {
                    # Hold the process's kernel handle across creation-time verification, termination and exit wait.
                    $handle = $browser.SafeHandle
                    if ($null -eq $handle -or $handle.IsClosed -ne $false -or $handle.IsInvalid -ne $false) {
                        throw 'unable to pin a valid process handle'
                    }
                    $start = $browser.StartTime
                    if ($start -isnot [datetime] -or $row.CreationDate -isnot [datetime]) {
                        throw 'process creation time is unavailable'
                    }
                    # CIM reports microseconds; a different creation time means the PID was reused.
                    if ([Math]::Abs($start.ToUniversalTime().ToFileTimeUtc() -
                            $row.CreationDate.ToUniversalTime().ToFileTimeUtc()) -ge 10) {
                        $rescan = $true
                        continue
                    }
                } catch {
                    $identityFailure = $_.Exception.Message
                }
                if ($null -eq $identityFailure) {
                    if ($clock.ElapsedMilliseconds -ge $timeoutMs) {
                        throw "WebView2 cleanup deadline expired before stopping PID $($row.ProcessId)"
                    }
                    try { $browser.Kill() }
                    catch [System.InvalidOperationException], [System.ComponentModel.Win32Exception] {
                        if (-not $browser.HasExited) { throw }
                    }
                }
                $rescan = $true
                $remaining = $timeoutMs - $clock.ElapsedMilliseconds
                try {
                    $exited = $remaining -gt 0 -and $browser.WaitForExit([int]$remaining)
                } catch {
                    throw "Cannot confirm exit of WebView2 PID $($row.ProcessId) ($identityFailure): $($_.Exception.Message)"
                }
                if (-not $exited) {
                    if ($null -ne $identityFailure) {
                        throw "Cannot establish identity or confirm exit of WebView2 PID $($row.ProcessId): $identityFailure"
                    }
                    throw "Owned WebView2 PID $($row.ProcessId) did not exit before the cleanup deadline"
                }
            } finally {
                $browser.Dispose()
            }
        }
    } while ($rescan)
}

$sourceRoot = (Resolve-Path -LiteralPath $ProductRoot).Path
if ($ReuseUnpackedProduct) {
    Assert-ReusedUnpackedProductRoot -SourceRoot $sourceRoot
}
$layoutDescriptor = Join-Path $sourceRoot 'app\metadata\product-layout.json'
$installedLayout = Test-Path -LiteralPath $layoutDescriptor -PathType Leaf
if ($ReuseUnpackedProduct -and -not $installedLayout) {
    throw 'Web workbench reuse requires an installed product layout'
}
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
if ($ReuseUnpackedProduct) {
    # 复用解包树作为运行根;该树的唯一删除者是 release_smoke 的
    # TemporaryDirectory,此处不复制、不删除。
    $isolatedRoot = $sourceRoot
} else {
    $isolatedRoot = Join-Path $tempRoot `
        "vibeocr-web-smoke-$([guid]::NewGuid().ToString('N'))"
}
# 产品运行库为系统 Evergreen（不再随包分发）；userData 落在隔离树内
# 的 state/webview2。浏览器镜像必须来自产品树外的系统安装位置。
if (-not $ReuseUnpackedProduct -and -not ([System.IO.Path]::GetFullPath($isolatedRoot).StartsWith(
    $tempRoot,
    [System.StringComparison]::OrdinalIgnoreCase))) {
    throw 'Web workbench smoke isolation path escaped the temporary directory'
}
$previousSmoke = $env:VIBEOCR_SELF_TEST_SMOKE
$previousInstance = $env:VIBEOCR_SELF_TEST_INSTANCE
$previousHealth = $env:VIBEOCR_WEB_READY_FILE
$process = $null
$failureKind = 'process-or-health-failure'
try {
    if (-not $ReuseUnpackedProduct) {
        New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
        Get-ChildItem -LiteralPath $sourceRoot -Force |
            Copy-Item -Destination $isolatedRoot -Recurse -Force
    }
    $executable = Join-Path $isolatedRoot $relativeExecutable
    $expectedUserData = Join-Path $isolatedRoot 'state\webview2'
    $env:VIBEOCR_SELF_TEST_SMOKE = 'web-ready'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_WEB_READY_FILE = $healthFile
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
    # Evergreen 证据：浏览器进程镜像必须来自产品树外的系统安装位置，
    # 版本为非空四段，userData 是隔离树的 state/webview2。
    if ($null -eq $health.webview2 -or $null -eq $health.webview2.browser_process_id) {
        throw 'Web workbench health signal lacks the WebView2 runtime binding'
    }
    $browserPid = [int]$health.webview2.browser_process_id
    if ($browserPid -le 0) {
        throw "Invalid WebView2 browser process id: $browserPid"
    }
    $browserPath = [string]$health.webview2.browser_executable_path
    $isolatedFull = [System.IO.Path]::GetFullPath($isolatedRoot)
    if (-not $browserPath -or $browserPath.StartsWith($isolatedFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "WebView2 browser executable must come from the system Evergreen runtime, not the product tree: '$browserPath'"
    }
    $browserVersion = [string]$health.webview2.browser_version
    if ($browserVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "WebView2 Evergreen browser version is missing or malformed: '$browserVersion'"
    }
    if (-not [string]::Equals(
            [string]$health.webview2.user_data_folder,
            [System.IO.Path]::GetFullPath($expectedUserData),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "WebView2 user data folder is not the expected state/webview2 directory: health='$($health.webview2.user_data_folder)' expected='$([System.IO.Path]::GetFullPath($expectedUserData))'"
    }
    if (-not (Test-Path -LiteralPath $expectedUserData -PathType Container)) {
        throw "WebView2 user data directory was not created: $expectedUserData"
    }
    Write-Host "Web workbench smoke verified: bridge-ready, resource GET and annotation POST, visible/minimized/hidden owner-window restoration, system Evergreen WebView2 $browserVersion (pid $browserPid), user data under state/webview2."
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
    $resolvedIsolation = [System.IO.Path]::GetFullPath($isolatedRoot)
    # 先从 health 信号取本次浏览器镜像（信号文件即将删除），再确认 Evergreen
    # WebView2 进程退出，避免删除竞态。复用解包树时不删除解包树本身,该树由
    # release_smoke 的 TemporaryDirectory 统一删除。
    $ownedBrowserExe = ''
    if (Test-Path -LiteralPath $healthFile) {
        try {
            $ownedBrowserExe = [string]((Get-Content -LiteralPath $healthFile -Raw | ConvertFrom-Json).webview2.browser_executable_path)
        } catch { $ownedBrowserExe = '' }
    }
    Stop-OwnedWebView2 -ExpectedExecutable $ownedBrowserExe
    foreach ($signalFile in @($healthFile, "$healthFile.writing")) {
        # 信号文件无需重试语义；用 .NET 直删避免与宿主 Remove-Item 覆盖交互。
        if (Test-Path -LiteralPath $signalFile) {
            [System.IO.File]::Delete($signalFile)
        }
    }
    if (-not $ReuseUnpackedProduct -and (Test-Path -LiteralPath $isolatedRoot)) {
        if (-not $resolvedIsolation.StartsWith(
            $tempRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove smoke isolation outside the temporary directory'
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
    # #321：版本化 Fixed userData 位于隔离树内，随隔离树/解包树统一删除。
}
