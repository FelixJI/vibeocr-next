[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
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
    $env:VIBEOCR_SELF_TEST_INSTANCE = $previousInstance
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
