[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [int]$TimeoutMinutes = 120
)

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

$smokeRoot = Join-Path $work "vibeocr-managed-e2e-$([guid]::NewGuid().ToString('N'))"
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
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "Managed environment $phase exited without health evidence (exit $($process.ExitCode))"
    }
    $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $health.schema_version -ne 1 -or
        $health.state -ne 'passed' -or $health.phase -ne $phase) {
        throw "Managed environment $phase failed: $($health.error)"
    }
    return $health
}

function Test-ManagedPython([string]$python, [bool]$expectEmpty) {
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        throw "Managed interpreter missing: $python"
    }
    $code = 'import importlib.util,json,sys; print(json.dumps({"prefix":sys.prefix,"executable":sys.executable,"packages":{p:importlib.util.find_spec(p) is not None for p in ("pip","fastapi","rapidocr","paddleocr","mineru","torch")}}))'
    $output = & $python -I -B -c $code
    if ($LASTEXITCODE -ne 0) { throw "Managed interpreter probe failed: $python" }
    $probe = $output | ConvertFrom-Json
    if (-not $probe.prefix -or -not $probe.executable) {
        throw "Managed interpreter probe incomplete: $python"
    }
    if ($expectEmpty) {
        foreach ($name in @('pip', 'fastapi', 'rapidocr', 'paddleocr', 'mineru', 'torch')) {
            if ($probe.packages.$name) { throw "Empty environment unexpectedly contains $name" }
        }
    }
    return $probe
}

try {
    $created = Invoke-ManagedPhase 'create'
    if ($created.install_attempts -ne 0 -or $created.evidence.active_id -or
        $created.evidence.environments.Count -ne 2) {
        throw 'Creation phase unexpectedly installed or activated an environment'
    }
    $emptyProbes = @()
    foreach ($environment in $created.evidence.environments) {
        if ($environment.status -ne 'empty' -or $environment.python_state -ne 'ready') {
            throw "Named environment is not empty: $($environment.name)"
        }
        $probe = Test-ManagedPython $environment.python $true
        if (-not $probe.prefix.Equals($environment.path, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Empty environment prefix mismatch: $($environment.name)"
        }
        $emptyProbes += $probe
    }

    $installed = Invoke-ManagedPhase 'install'
    if ($installed.install_attempts -ne 2 -or
        $installed.evidence.install_attempts_after_switch_back -ne 2 -or
        -not $installed.evidence.local_qr_ready -or
        $installed.evidence.capture.Width -le 0 -or
        $installed.evidence.capture.Height -le 0 -or
        $installed.evidence.capture.WhitePixels -le 10000 -or
        $installed.evidence.capture.DarkPixels -le 100 -or
        $installed.evidence.recognition.Count -ne 2) {
        throw 'Install and switch evidence is incomplete'
    }
    foreach ($environment in $installed.evidence.installed_environments) {
        if ($environment.status -ne 'installed' -or $environment.recipe -ne 'rapidocr-cpu') {
            throw "Expected publicly confirmed RapidOCR recipe for $($environment.name)"
        }
    }
    foreach ($task in $installed.evidence.recognition) {
        if (-not $task.task_id -or -not $task.environment_id -or
            $task.environment_revision -lt 1 -or -not $task.ocr_visible) {
            throw 'OCR task lacks managed environment evidence'
        }
        $probe = Test-ManagedPython $task.python $false
        $target = $installed.evidence.installed_environments |
            Where-Object { $_.id -eq $task.environment_id } |
            Select-Object -First 1
        if (-not $target -or
            $target.revision -ne $task.environment_revision -or
            $target.path -ne $task.environment_path -or
            -not $probe.prefix.Equals($target.path, [StringComparison]::OrdinalIgnoreCase)) {
            throw "OCR interpreter prefix mismatch: $($task.task_id)"
        }
    }
    if ($installed.evidence.switch_back.environment_id -ne $created.evidence.environments[0].id) {
        throw 'Switch back did not restore environment A'
    }

    $restarted = Invoke-ManagedPhase 'restart'
    if ($restarted.install_attempts -ne 0 -or
        $restarted.evidence.active_id -ne $created.evidence.environments[0].id -or
        $restarted.evidence.service_state -ne 'Ready') {
        throw 'Active environment A was not ready after restart without installation'
    }
    Write-Host "Managed environment E2E passed: A/B empty, two RapidOCR installs, two OCR tasks, switch-back and restart without installation."
    Write-Host "Isolated evidence retained at: $smokeRoot"
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH = $previousHealth
    $env:VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE = $previousPhase
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
}
