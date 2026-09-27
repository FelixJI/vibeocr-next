<#
.SYNOPSIS
Runtime candidate AC6 smoke: isolated base install + real OCR round trip.

.DESCRIPTION
Copies a built product candidate (ProductRoot, e.g. .release-build/VibeOCR of
this checkout) into a fresh unique work root under the caller-provided
WorkRoot (CI may point this at RUNNER_TEMP or any scratch directory),
generates a synthetic "VibeOCR 123" PNG with the repository's locked Pillow
dependency, and runs the RuntimeCandidateSmokeTests fact through the Platform
test project with a precise filter. The TRX must prove the case actually
executed and passed; a zero-test green run fails closed. The smoke root is
brand new, never nests with the source candidate, and never reuses a
candidate that already carries a state directory, so no pre-existing state
is read. Evidence under the smoke root is retained for root acceptance:
nothing is cleaned or recursively deleted, and only the child processes of
this run are disposed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [int]$BlameHangTimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
if ($BlameHangTimeoutMinutes -le 0) {
    throw 'Runtime candidate smoke blame-hang timeout must be positive'
}
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
foreach ($tool in @('dotnet', 'uv')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Runtime candidate smoke requires '$tool' on PATH"
    }
}
if ($env:VIBEOCR_RUNTIME_INSTALLER) {
    throw 'VIBEOCR_RUNTIME_INSTALLER is set; refusing to smoke against a redirected runtime installer'
}

$sourceRoot = (Resolve-Path -LiteralPath $ProductRoot).Path
foreach ($marker in @(
        'app\metadata\product-layout.json',
        'app\metadata\component-lock.json',
        'app\VibeOCR.WinUI.exe',
        'runtime\backend\runtime-manifest.json',
        'runtime\installer\vibeocr-runtime-installer.exe'
    )) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $marker) -PathType Leaf)) {
        throw "Runtime candidate is missing required marker '$marker': $sourceRoot"
    }
}
if (Test-Path -LiteralPath (Join-Path $sourceRoot 'state')) {
    throw "Runtime candidate already carries a state directory: $sourceRoot"
}

# 隔离只依赖精确路径规则:WorkRoot 为目录、不与源候选嵌套,
# 隔离根全新创建,因此不读取任何既有 state。
$workRootItem = Get-Item -LiteralPath $WorkRoot -ErrorAction Stop
if (-not $workRootItem.PSIsContainer) {
    throw "Runtime candidate smoke WorkRoot is not a directory: $WorkRoot"
}
$workRootPath = $workRootItem.FullName.TrimEnd('\')
if ($sourceRoot.Equals($workRootPath, [System.StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($workRootPath + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
    $workRootPath.StartsWith($sourceRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Runtime candidate smoke ProductRoot and WorkRoot must not nest'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$smokeRoot = Join-Path $workRootPath `
    "vibeocr-runtime-candidate-smoke-$stamp-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
if (Test-Path -LiteralPath $smokeRoot) {
    throw "Runtime candidate smoke root already exists: $smokeRoot"
}
$candidateRoot = Join-Path $smokeRoot 'candidate'
$inputDirectory = Join-Path $smokeRoot 'input'
$resultsDirectory = Join-Path $smokeRoot 'test-results'
New-Item -ItemType Directory -Path $candidateRoot, $inputDirectory, $resultsDirectory | Out-Null

# 隔离副本承载全部安装与 OCR 证据;源候选保持只读。
Get-ChildItem -LiteralPath $sourceRoot -Force |
    Copy-Item -Destination $candidateRoot -Recurse -Force

$imagePath = Join-Path $inputDirectory 'vibeocr-123.png'
$generateImage = @'
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

target = Path(sys.argv[1])
image = Image.new("RGB", (960, 240), "white")
draw = ImageDraw.Draw(image)
try:
    font = ImageFont.truetype("arial.ttf", 96)
except OSError:
    font = ImageFont.load_default(size=96)
draw.text((48, 64), "VibeOCR 123", fill="black", font=font)
target.parent.mkdir(parents=True, exist_ok=True)
image.save(target, format="PNG")
'@
Push-Location $repositoryRoot
try {
    $generateImage | uv run --frozen python - $imagePath
    if ($LASTEXITCODE -ne 0) {
        throw 'Runtime candidate smoke synthetic image generation failed'
    }
} finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
    throw "Runtime candidate smoke synthetic image is missing: $imagePath"
}

$parameters = [ordered]@{
    started_at                  = (Get-Date).ToUniversalTime().ToString('o')
    product_root                = $sourceRoot
    candidate_root              = $candidateRoot
    smoke_root                  = $smokeRoot
    image                       = $imagePath
    blame_hang_timeout_minutes  = $BlameHangTimeoutMinutes
}
$parameters | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $smokeRoot 'smoke-parameters.json') -Encoding utf8

$project = Join-Path $repositoryRoot 'tests\dotnet\VibeOCR.Platform.Tests\VibeOCR.Platform.Tests.csproj'
$previousSmokeRoot = $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT
$previousSmokeImage = $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_IMAGE
try {
    # 唯一隔离根与合成图片只对本次 dotnet test 子进程可见。
    $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT = $smokeRoot
    $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_IMAGE = $imagePath

    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw 'Runtime candidate smoke restore failed'
    }
    $testArguments = @(
        'test', $project,
        '-c', 'Release',
        '--no-restore',
        '--filter', 'FullyQualifiedName~VibeOCR.Platform.Tests.RuntimeCandidateSmokeTests',
        '--logger', 'trx;LogFileName=runtime-candidate-smoke.trx',
        '--logger', 'console;verbosity=normal',
        '--results-directory', $resultsDirectory,
        '--blame-hang',
        '--blame-hang-timeout', "$BlameHangTimeoutMinutes`m",
        '--blame-hang-dump-type', 'none'
    )
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime candidate smoke test run failed with exit $LASTEXITCODE"
    }
} finally {
    $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_ROOT = $previousSmokeRoot
    $env:VIBEOCR_RUNTIME_CANDIDATE_SMOKE_IMAGE = $previousSmokeImage
}

$trxPath = Join-Path $resultsDirectory 'runtime-candidate-smoke.trx'
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Runtime candidate smoke produced no TRX at $trxPath"
}
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$namespace = New-Object System.Xml.XmlNamespaceManager((New-Object System.Xml.NameTable))
$namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$matched = @($trx.SelectNodes('//t:UnitTestResult', $namespace) |
    Where-Object { $_.testName -like '*RuntimeCandidateSmokeTests*' })
if ($matched.Count -eq 0) {
    throw 'Runtime candidate smoke executed zero RuntimeCandidateSmokeTests cases (zero-test green is rejected)'
}
$notPassed = @($matched | Where-Object { $_.outcome -ne 'Passed' })
if ($notPassed.Count -gt 0) {
    $summary = ($notPassed | ForEach-Object { "$($_.testName)=$($_.outcome)" }) -join '; '
    throw "Runtime candidate smoke did not pass: $summary"
}
$counters = $trx.TestRun.ResultSummary.Counters
if ([int]$counters.executed -lt 1 -or [int]$counters.passed -lt 1) {
    throw 'Runtime candidate smoke TRX counters do not prove an executed passed case'
}
Write-Host "Runtime candidate smoke verified: executed=$($counters.executed) passed=$($counters.passed)."
Write-Host "Isolated evidence retained at: $smokeRoot"
