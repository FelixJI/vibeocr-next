[CmdletBinding()]
param(
  # 无用户/冒烟 state 的发布产品根（解包 VibeOCRNext-v<version>-win-x64.zip）。
  [Parameter(Mandatory = $true)][string]$ProductRoot,
  [Parameter(Mandatory = $true)][string]$WorkRoot,
  # maintenance = selftest 注入维护互斥早退（仅需 stateless 候选）。
  # fail       = 副本内破坏 component-lock，真实进入连接故障终态（需 -PreparedProductRoot）。
  # crash      = supervisor ready 后真实崩溃 + 一次自动恢复（需 -PreparedProductRoot）。
  [ValidateSet('maintenance', 'fail', 'crash')][string]$Mode = 'maintenance',
  # smoke_managed_environments.ps1 打印的 "Isolated evidence retained at: <root>" 下的
  # candidate 目录（含冒烟安装的活动环境与 state）。绝不指向真实安装或含用户数据的目录。
  [string]$PreparedProductRoot = '',
  [int]$TimeoutMinutes = 20
)

# #134 CP2 supervisor 自测：GUID 单实例 + 独立 state 的隔离候选上，复用既有
# t6 + crash-soak seam 验证连接终态（维护互斥早退 / 连接失败 / 崩溃恢复）。
# 不使用测试 stub，不触碰真实数据；全部证据落在 WorkRoot 下的 GUID 目录。
$ErrorActionPreference = 'Stop'
if ($TimeoutMinutes -le 0) { throw 'TimeoutMinutes must be positive' }
$source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
$work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
  throw 'ProductRoot and WorkRoot must not nest'
}
$markers = @(
  'app\VibeOCR.WinUI.exe',
  'app\metadata\product-layout.json',
  'runtime\backend\runtime-manifest.json',
  'runtime\installer\vibeocr-runtime-installer.exe'
)
foreach ($marker in $markers) {
  if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
    throw "Candidate marker missing: $marker"
  }
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) {
  throw 'ProductRoot must be stateless (no state directory); use -PreparedProductRoot for installed smoke state'
}

# fail/crash 需要活动已安装环境：只接受冒烟保留的含 state 候选。
$prepared = $null
if ($Mode -ne 'maintenance') {
  if ([string]::IsNullOrWhiteSpace($PreparedProductRoot)) {
    throw "Mode $Mode requires -PreparedProductRoot (run scripts/smoke_managed_environments.ps1 first and pass its retained candidate)"
  }
  $prepared = (Resolve-Path -LiteralPath $PreparedProductRoot).Path.TrimEnd('\')
  if ($prepared.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
      $prepared.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PreparedProductRoot must not be the stateless ProductRoot or inside it'
  }
  foreach ($marker in $markers) {
    if (-not (Test-Path -LiteralPath (Join-Path $prepared $marker) -PathType Leaf)) {
      throw "Prepared candidate marker missing: $marker"
    }
  }
  if (-not (Test-Path -LiteralPath (Join-Path $prepared 'state') -PathType Container)) {
    throw 'PreparedProductRoot has no smoke state directory'
  }
}

$smokeRoot = Join-Path $work "soak-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$webViewData = Join-Path $smokeRoot 'webview2'
New-Item -ItemType Directory -Path $candidate | Out-Null
if ($Mode -eq 'crash') {
  # 崩溃恢复必须启动已安装环境的真实 Python：state 内是绝对路径，只能
  # 就地运行冒烟保留候选（其本身就是 GUID 隔离产物，绝不复用真实安装）。
  $runRoot = $prepared
} else {
  # maintenance/fail 在独立 GUID 副本上运行；fail 在副本内破坏锁，
  # 真实进入连接故障路径而不是让 OnLaunched 失败。
  $runRoot = $candidate
  if ($Mode -eq 'fail') {
    Copy-Item -Path (Join-Path $prepared '*') -Destination $candidate -Recurse -Force
  } else {
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $candidate -Recurse -Force
  }
}

$previous = @{}
foreach ($name in @(
  'VIBEOCR_SELF_TEST_SMOKE', 'VIBEOCR_SELF_TEST_INSTANCE',
  'VIBEOCR_SELF_TEST_MAINTENANCE_EARLY_EXIT', 'VIBEOCR_SOAK_INJECT_CRASH',
  'VIBEOCR_SOAK_RESULT', 'VIBEOCR_STARTUP_TRACE',
  'VIBEOCR_SUPERVISOR_HEALTH_TRACE', 'WEBVIEW2_USER_DATA_FOLDER'
)) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }

function Restore-SoakEnvironment {
  foreach ($entry in $previous.GetEnumerator()) {
    if ($null -eq $entry.Value) {
      Remove-Item -Path ('Env:' + $entry.Key) -ErrorAction SilentlyContinue
    } else {
      Set-Item -Path ('Env:' + $entry.Key) -Value $entry.Value
    }
  }
}

$healthPath = Join-Path $smokeRoot 'supervisor-health.jsonl'
$tracePath = Join-Path $smokeRoot 'trace.jsonl'
$soakPath = Join-Path $smokeRoot 'soak.json'
try {
  if ($Mode -eq 'fail') {
    # 只破坏激活期才读取的 component-lock：OnLaunched 不读它（ReadProfileDescriptor
    # 吞掉 manifest 解析错误），ListEnvironments/激活任一失败都会进入
    # ConnectSupervisorCoreAsync 的 Faulted 终态并 FailSoakRun。
    Set-Content -LiteralPath (Join-Path $candidate 'app\metadata\component-lock.json') `
      -Value '{ corrupted-by-soak' -NoNewline -Encoding ascii
  }

  $env:VIBEOCR_SELF_TEST_SMOKE = 't6'
  $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
  $env:VIBEOCR_STARTUP_TRACE = $tracePath
  $env:VIBEOCR_SUPERVISOR_HEALTH_TRACE = $healthPath
  $env:WEBVIEW2_USER_DATA_FOLDER = $webViewData
  if ($Mode -eq 'maintenance') { $env:VIBEOCR_SELF_TEST_MAINTENANCE_EARLY_EXIT = '1' }
  if ($Mode -eq 'crash') {
    $env:VIBEOCR_SOAK_INJECT_CRASH = '1'
    $env:VIBEOCR_SOAK_RESULT = $soakPath
  }
  if ($Mode -eq 'fail') { $env:VIBEOCR_SOAK_RESULT = $soakPath }

  $executable = Join-Path $runRoot 'app\VibeOCR.WinUI.exe'
  $process = Start-Process -FilePath $executable `
    -ArgumentList "--profile production --install-root `"$runRoot`"" `
    -WorkingDirectory (Split-Path -Parent $executable) `
    -WindowStyle Hidden -PassThru
  if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
    $process.Kill($true)
    if (-not $process.WaitForExit(5000)) {
      throw "Soak $Mode process tree did not exit after forced termination"
    }
    throw "Soak $Mode timed out after $TimeoutMinutes minutes"
  }
  $exit = $process.ExitCode

  if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf)) {
    throw "Soak $Mode exited ($exit) without a startup trace"
  }
  $health = @()
  if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
    $health = Get-Content -LiteralPath $healthPath | ForEach-Object {
      $_ | ConvertFrom-Json -ErrorAction Stop }
  }
  $milestones = (Get-Content -LiteralPath $tracePath | Select-Object -Last 1 | ConvertFrom-Json)

  switch ($Mode) {
    'maintenance' {
      if ($exit -ne 0) { throw "Maintenance early-exit smoke failed: exit $exit" }
      if (-not $milestones.T6) { throw 'Maintenance early-exit smoke missing T6 terminal milestone' }
      $terminal = $health | Select-Object -Last 1
      if ($terminal.state -ne 'NotReady' -or
          -not ([string]$terminal.detail).Contains('维护')) {
        throw "Maintenance early-exit terminal state invalid: $($terminal | ConvertTo-Json -Compress)"
      }
      Write-Host 'Maintenance early-exit smoke passed: NotReady + maintenance reason + T6 self-exit.'
    }
    'fail' {
      if ($exit -ne 1) { throw "Connection-failure smoke expected exit 1, got $exit" }
      if (-not (Test-Path -LiteralPath $soakPath -PathType Leaf)) {
        throw 'Connection-failure smoke exited without soak evidence'
      }
      $soak = Get-Content -LiteralPath $soakPath -Raw | ConvertFrom-Json
      if ($soak.crash_requested -or $soak.recovered -or [string]::IsNullOrWhiteSpace($soak.error)) {
        throw "Connection-failure soak evidence invalid: $($soak | ConvertTo-Json -Compress)"
      }
      if (-not ($health | Where-Object { $_.state -eq 'Faulted' })) {
        throw 'Connection-failure smoke never reached the Faulted terminal'
      }
      Write-Host "Connection-failure smoke passed: Faulted terminal, exit 1, error=$($soak.error)"
    }
    'crash' {
      if ($exit -ne 0) { throw "Crash-recovery smoke failed: exit $exit" }
      if (-not (Test-Path -LiteralPath $soakPath -PathType Leaf)) {
        throw 'Crash-recovery smoke exited without soak evidence'
      }
      $soak = Get-Content -LiteralPath $soakPath -Raw | ConvertFrom-Json
      if (-not $soak.crash_requested -or -not $soak.recovered) {
        throw "Crash-recovery soak evidence invalid: $($soak | ConvertTo-Json -Compress)"
      }
      if ([string]::IsNullOrWhiteSpace($soak.instance_before) -or
          [string]::IsNullOrWhiteSpace($soak.instance_after) -or
          $soak.instance_before -eq $soak.instance_after) {
        throw "Crash-recovery did not prove a new supervisor instance: $($soak | ConvertTo-Json -Compress)"
      }
      if (-not ($health | Where-Object { $_.state -eq 'Faulted' -and
            ([string]$_.detail).Contains('自动恢复') })) {
        throw 'Crash-recovery health trace lacks the auto-recovery Faulted record'
      }
      $last = $health | Select-Object -Last 1
      if ($last.state -ne 'Ready' -or $last.instance_id -ne $soak.instance_after) {
        throw "Crash-recovery final state is not the new Ready instance: $($last | ConvertTo-Json -Compress)"
      }
      if (-not $milestones.T6) { throw 'Crash-recovery smoke missing T6 milestone' }
      Write-Host "Crash-recovery smoke passed: $($soak.instance_before) -> $($soak.instance_after), Ready restored."
    }
  }
  Write-Host "Isolated evidence retained at: $smokeRoot"
} finally {
  Restore-SoakEnvironment
}
