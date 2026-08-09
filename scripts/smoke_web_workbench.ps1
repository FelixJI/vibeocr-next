[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $ProductRoot).Path
$sourceExecutable = Join-Path $sourceRoot 'VibeOCR.WinUI.exe'
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
$previousHealth = $env:VIBEOCR_WEB_READY_FILE
$previousWebViewData = $env:WEBVIEW2_USER_DATA_FOLDER
$process = $null
try {
    New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
    Get-ChildItem -LiteralPath $sourceRoot -Force |
        Copy-Item -Destination $isolatedRoot -Recurse -Force
    $executable = Join-Path $isolatedRoot 'VibeOCR.WinUI.exe'
    $env:VIBEOCR_SELF_TEST_SMOKE = 'web-ready'
    $env:VIBEOCR_WEB_READY_FILE = $healthFile
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewDataRoot
    $process = Start-Process -FilePath $executable `
        -ArgumentList @('--shell-only', '--profile', 'production') `
        -WorkingDirectory $isolatedRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Web workbench did not reach bridge-ready within $TimeoutSeconds seconds"
    }
    if ($process.ExitCode -ne 0) {
        throw "Web workbench smoke exited with code $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $healthFile -PathType Leaf)) {
        throw 'Web workbench did not write its bridge-ready health signal'
    }
    $health = Get-Content -LiteralPath $healthFile -Raw | ConvertFrom-Json
    if ($health.schema_version -ne 1 -or $health.state -ne 'bridge-ready') {
        throw 'Web workbench health signal is invalid'
    }
    Write-Host 'Web workbench smoke verified: packaged WebView2 reached bridge-ready.'
} finally {
    $env:VIBEOCR_SELF_TEST_SMOKE = $previousSmoke
    $env:VIBEOCR_WEB_READY_FILE = $previousHealth
    $env:WEBVIEW2_USER_DATA_FOLDER = $previousWebViewData
    if (Test-Path -LiteralPath $healthFile) {
        Remove-Item -LiteralPath $healthFile -Force
    }
    if (Test-Path -LiteralPath $isolatedRoot) {
        $resolvedIsolation = [System.IO.Path]::GetFullPath($isolatedRoot)
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
        Remove-Item -LiteralPath $resolvedWebViewData -Recurse -Force
    }
}
