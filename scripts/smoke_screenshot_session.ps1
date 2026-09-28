[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [string]$PreparedCandidateRoot,
    [int]$TimeoutMinutes = 45
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
if (-not $PreparedCandidateRoot) {
    throw 'Standalone screenshot smoke requires a prepared isolated candidate. Run scripts/smoke_managed_environments.ps1 -ProductRoot <candidate> -WorkRoot <isolated-work-root> first, then pass its retained candidate with -PreparedCandidateRoot to this script.'
}
$candidate = (Resolve-Path -LiteralPath $PreparedCandidateRoot).Path.TrimEnd('\')
$smokeRoot = Split-Path -Parent $candidate
if ((Split-Path -Leaf $candidate) -ne 'candidate' -or
    -not (Split-Path -Leaf $smokeRoot).StartsWith('vibeocr-managed-e2e-', [StringComparison]::Ordinal) -or
    -not $smokeRoot.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $candidate 'state') -PathType Container)) {
    throw 'PreparedCandidateRoot must be the retained candidate from the isolated managed-environment smoke under WorkRoot'
}
foreach ($marker in @('app\VibeOCR.WinUI.exe', 'app\metadata\product-layout.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $marker) -PathType Leaf)) {
        throw "Prepared candidate marker missing: $marker"
    }
}
if ((Get-Content -LiteralPath (Join-Path $source 'app\metadata\product-layout.json') -Raw) -ne
    (Get-Content -LiteralPath (Join-Path $candidate 'app\metadata\product-layout.json') -Raw)) {
    throw 'Prepared candidate does not match ProductRoot layout'
}
$sourceIdentity = Get-Content -LiteralPath (Join-Path $source 'app\metadata\component-identities.json') -Raw | ConvertFrom-Json
$candidateIdentity = Get-Content -LiteralPath (Join-Path $candidate 'app\metadata\component-identities.json') -Raw | ConvertFrom-Json
foreach ($field in @('component', 'repository', 'version', 'source_sha')) {
    if (-not $sourceIdentity.project.$field -or
        $sourceIdentity.project.$field -ne $candidateIdentity.project.$field) {
        throw "Prepared candidate product binding differs: $field"
    }
}
$healthPath = Join-Path $smokeRoot 'screenshot-e2e-health.json'
$webViewData = Join-Path $smokeRoot 'screenshot-webview2'

$previousSmoke = $env:VIBEOCR_SELF_TEST_SMOKE
$previousInstance = $env:VIBEOCR_SELF_TEST_INSTANCE
$previousHealth = $env:VIBEOCR_SCREENSHOT_E2E_HEALTH
$previousWebViewData = $env:WEBVIEW2_USER_DATA_FOLDER
$process = $null
try {
    $env:VIBEOCR_SELF_TEST_SMOKE = 'screenshot-e2e'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_SCREENSHOT_E2E_HEALTH = $healthPath
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewData
    $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
    $process = Start-Process -FilePath $executable `
        -ArgumentList "--profile production --install-root `"$candidate`"" `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
        $process.Kill($true)
        if (-not $process.WaitForExit(5000)) {
            throw 'Screenshot smoke process tree did not exit after forced termination'
        }
        throw "Screenshot smoke timed out after $TimeoutMinutes minutes"
    }
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "Screenshot smoke exited without health evidence (exit $($process.ExitCode))"
    }
    $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
    if ($health.schema_version -ne 1 -or $health.state -ne 'passed') {
        throw "Screenshot smoke failed: $($health.error)"
    }
    if ($process.ExitCode -ne 0 -or
        $health.capture.Width -le 0 -or $health.capture.Height -le 0 -or
        $health.capture.WhitePixels -le 10000 -or $health.capture.DarkPixels -le 100 -or
        $health.canvas.orange_after -le ($health.canvas.orange_before + 50) -or
        -not $health.session_id -or $health.revision -lt 1 -or
        -not $health.task_id -or -not $health.ocr_visible -or
        $health.submit_attempts_after_capture -ne 0 -or
        $health.submit_attempts_after_recognition -ne 1 -or
        $health.startup_ensure_attempts_after_capture -ne
            $health.startup_ensure_attempts_before_capture -or
        $health.startup_ensure_attempts_after_capture -ne
            $health.startup_ensure_attempts_after_recognition) {
        throw 'Screenshot smoke health evidence is incomplete or inconsistent'
    }
    Write-Host "Screenshot E2E passed: $($health.capture.Width)x$($health.capture.Height), session=$($health.session_id), revision=$($health.revision), task=$($health.task_id)."
    Write-Host "Isolated evidence retained at: $smokeRoot"
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
    $env:VIBEOCR_SCREENSHOT_E2E_HEALTH = $previousHealth
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
}
