<#
.SYNOPSIS
使用产品候选的完整应用联调入口。

.DESCRIPTION
一条命令把源码改动刷新进本地产品候选并启动完整应用链（真实
Supervisor、识别、WebView2 workbench），避免"旧 dist / 旧 exe /
缺 runtime 树"误判。与 smoke_paddle_modes.ps1 等脚本的候选启动模式
一致：运行候选自带的 app\VibeOCR.WinUI.exe，Supervisor 由候选预构建
runtime 树（runtime\env\runtime-env\python.exe）承载。

步骤：

1. WebAssets dist 增量重建：WebAssets/src、入口 index.html 或根配置比 dist/index.html
   新时 npm run build（-ForceRebuild 强制）；
2. dotnet publish App（默认 Release，locked restore）增量输出到候选
   app\（metadata/runtime 等非 publish 产物不动，布局保持）；
3. 校验候选标记（product-layout 与预构建 runtime）；候选不存在时报错
   提示先运行 scripts/build-release.ps1 ——本入口不内置重型装配；
4. 启动候选 exe（--profile production --install-root <候选>），输出
   PID 后返回；应用由联调者自行关闭。

注意：WebView2 与正式发布一致使用系统 Evergreen 运行库（不再随包分发）；
旧候选若自带 WebView2Runtime 目录仍会被优先使用（兼容）。
本脚本只写 .release-build 候选与 bin，不修改仓库源码。
#>
[CmdletBinding()]
param(
    [string]$ProductRoot = "",
    [string]$Configuration = "Release",
    [switch]$ForceRebuild,
    [switch]$SkipPublish,
    [switch]$SkipDistSync
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$webAssets = Join-Path $root 'src/dotnet/VibeOCR.App/WebAssets'
$project = Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj'
if (-not $ProductRoot) {
    $ProductRoot = Join-Path $root '.release-build/VibeOCR'
}

# --- 1. WebAssets dist 增量重建 -------------------------------------------
$distMarker = Join-Path $webAssets 'dist/index.html'
$sourceFiles = @(
    Get-Item -LiteralPath (Join-Path $webAssets 'index.html')
    Get-ChildItem -Recurse -File (Join-Path $webAssets 'src')
    Get-ChildItem -File (Join-Path $webAssets '*.json')
    Get-ChildItem -File (Join-Path $webAssets '*.config.*') -ErrorAction SilentlyContinue
)
$sourceNewest = ($sourceFiles | Measure-Object LastWriteTime -Maximum).Maximum
$needsBuild = $ForceRebuild -or -not (Test-Path $distMarker) -or
    ($sourceNewest -gt (Get-Item $distMarker).LastWriteTime)
if ($needsBuild) {
    Write-Host "[dev_runtime] rebuilding WebAssets dist (source newer than dist)..." `
        -ForegroundColor Cyan
    Push-Location $webAssets
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'WebAssets npm run build failed' }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "[dev_runtime] WebAssets dist is up to date; skipping build" `
        -ForegroundColor DarkGray
}

# --- 2. 校验候选 -----------------------------------------------------------
$layoutManifest = Join-Path $ProductRoot 'app/metadata/product-layout.json'
$runtimePython = Join-Path $ProductRoot 'runtime/env/runtime-env/python.exe'
foreach ($marker in @($layoutManifest, $runtimePython)) {
    if (-not (Test-Path $marker)) {
        throw @"
Product candidate marker missing: $marker
ProductRoot: $ProductRoot
先运行 scripts/build-release.ps1 -Version <repository.json 版本> 装配候选
（或用 -ProductRoot 指定已装配候选）。
"@
    }
}

# --- 3. 增量刷新源码构建结果进候选 ----------------------------------------
if (-not $SkipDistSync) {
    Write-Host "[dev_runtime] syncing WebAssets dist into candidate..." `
        -ForegroundColor Cyan
    $candidateAssets = Join-Path $ProductRoot 'app/WebAssets'
    Copy-Item -Recurse -Force (Join-Path $webAssets 'dist/*') $candidateAssets
}
if (-not $SkipPublish) {
    Write-Host "[dev_runtime] dotnet publish ($Configuration) into candidate..." `
        -ForegroundColor Cyan
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed (locked-mode)' }
    dotnet publish $project -c $Configuration --no-restore -nologo -v:q `
        -p:SelfContained=true -p:WindowsAppSdkSelfContained=true `
        -o (Join-Path $ProductRoot 'app')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($Configuration)" }
}

# --- 4. 启动 ---------------------------------------------------------------
$exe = Join-Path $ProductRoot 'app/VibeOCR.WinUI.exe'
if (-not (Test-Path $exe)) { throw "candidate app executable not found: $exe" }

Write-Host "[dev_runtime] launching: $exe" -ForegroundColor Cyan
Write-Host "[dev_runtime]   --profile production --install-root `"$ProductRoot`"" `
    -ForegroundColor DarkGray
$process = Start-Process -FilePath $exe `
    -ArgumentList "--profile production", "--install-root `"$ProductRoot`"" `
    -WorkingDirectory (Split-Path -Parent $exe) -PassThru
Write-Host "[dev_runtime] started pid=$($process.Id); close the app when done" `
    -ForegroundColor Green
