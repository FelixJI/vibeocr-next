[CmdletBinding()]
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'
function Write-CiStage {
    param([Parameter(Mandatory = $true)][string]$Name)
    Write-Host "::notice title=Release build stage::$Name"
}

$root = if ($env:AUTOMATION_PROJECT_ROOT) { (Resolve-Path $env:AUTOMATION_PROJECT_ROOT).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$projectFile = Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
if (-not $Version) {
    $Version = $projectVersion
} else {
    $Version = $Version.TrimStart('v')
}
if ($Version -ne $projectVersion) {
    throw "Release version '$Version' does not match project version '$projectVersion'"
}
$artifacts = if ($env:AUTOMATION_ARTIFACTS_DIR) { $env:AUTOMATION_ARTIFACTS_DIR } else { Join-Path $root 'artifacts' }
$build = Join-Path $root '.release-build'
foreach ($path in @($build)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
$inputs = Join-Path $root '.release-input'
$protocol = Join-Path $inputs 'protocol'
$backend = Join-Path $inputs 'backend'
$lock = Join-Path $artifacts 'component-lock.json'
$identity = Join-Path $artifacts 'component-identities.json'
if (-not (Test-Path -LiteralPath $protocol -PathType Container) -or -not (Test-Path -LiteralPath $backend -PathType Container) -or -not (Test-Path -LiteralPath $lock -PathType Leaf) -or -not (Test-Path -LiteralPath $identity -PathType Leaf)) { throw 'resolved Backend/Protocol identities are required before build' }
$webAssets = Join-Path $root 'src/dotnet/VibeOCR.App/WebAssets'
Write-CiStage 'brand-assets'
uv run --no-project python (Join-Path $root 'scripts/generate_brand_assets.py') --check
if ($LASTEXITCODE -ne 0) { throw 'Brand asset consistency verification failed' }
Write-CiStage 'web-assets'
npm ci --prefix $webAssets
if ($LASTEXITCODE -ne 0) { throw 'WebAssets locked install failed' }
npm run build --prefix $webAssets
if ($LASTEXITCODE -ne 0) { throw 'WebAssets production build failed' }
uv run --no-sync python (Join-Path $root 'scripts/verify_web_assets.py') `
  (Join-Path $webAssets 'dist')
if ($LASTEXITCODE -ne 0) { throw 'WebAssets production closure verification failed' }
dotnet restore (Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Next restore failed' }
dotnet restore (Join-Path $root 'src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Next bootstrapper restore failed' }
$appPublish = Join-Path $build 'app-publish'
Write-CiStage 'app-publish'
dotnet publish (Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj') `
  -c Release -r win-x64 --self-contained false --no-restore -o $appPublish
if ($LASTEXITCODE -ne 0) { throw 'Next publish failed' }
Write-CiStage 'app-webview-smoke'
& (Join-Path $root 'scripts/smoke_web_workbench.ps1') -ProductRoot $appPublish
if ($LASTEXITCODE -ne 0) { throw 'Web workbench bridge-ready smoke failed' }
$bootstrapperPublish = Join-Path $build 'bootstrapper-publish'
Write-CiStage 'bootstrapper-publish'
dotnet publish (Join-Path $root 'src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj') `
  -c Release --self-contained false --no-restore -o $bootstrapperPublish
if ($LASTEXITCODE -ne 0) { throw 'Next bootstrapper publish failed' }
Write-CiStage 'updater-pyinstaller'
uv run --no-sync --with pyinstaller==6.21.0 python -m PyInstaller `
  --noconfirm --clean --onefile --windowed `
  --icon (Join-Path $root 'assets/brand/generated/vibeocr.ico') `
  --name updater --distpath (Join-Path $build 'updater-dist') `
  --workpath (Join-Path $build 'updater-work') `
  --specpath (Join-Path $build 'updater-spec') `
  (Join-Path $root 'scripts/updater_main.py')
if ($LASTEXITCODE -ne 0) { throw 'Next updater build failed' }
$product = Join-Path $build 'VibeOCR'
Write-CiStage 'product-layout'
uv run --no-sync python (Join-Path $root 'scripts/product_layout.py') stage `
  --product-root $product `
  --app-publish-root $appPublish `
  --bootstrapper-executable (Join-Path $bootstrapperPublish 'VibeOCR.Bootstrapper.exe') `
  --updater-executable (Join-Path $build 'updater-dist/updater.exe') `
  --component-lock $lock --component-identities $identity `
  --backend-release-dir $backend `
  --license-file (Join-Path $root 'LICENSE') `
  --changelog-file (Join-Path $root 'CHANGELOG.md')
if ($LASTEXITCODE -ne 0) { throw 'VibeOCR product layout staging failed' }
$zip = Join-Path $artifacts "VibeOCR-v$Version-win64.zip"
Write-CiStage 'product-package'
uv run --no-sync python (Join-Path $root 'scripts/package_product_release.py') `
  --product-root $product --frontend next `
  --frontend-version $Version `
  --source-commit (git -C $root rev-parse HEAD).Trim() `
  --component-lock $lock --protocol-release-dir $protocol `
  --backend-release-dir $backend --output $zip
if ($LASTEXITCODE -ne 0) { throw 'Next product binding failed' }
uv run --no-sync python (Join-Path $root 'scripts/product_layout.py') verify `
  --product-root $product
if ($LASTEXITCODE -ne 0) { throw 'Next product release closure verification failed' }
Write-CiStage 'artifact-verify'
& (Join-Path $root 'scripts/verify_winui_artifact.ps1') -Artifact $zip
if ($LASTEXITCODE -ne 0) { throw 'Next artifact verification failed' }
uv run --no-sync python (Join-Path $root 'scripts/build_release_checksums.py') $artifacts `
  --sidecar-for $zip
if ($LASTEXITCODE -ne 0) { throw 'sidecar checksum build failed' }
Remove-Item -LiteralPath (Join-Path $artifacts 'SHA256SUMS') -Force
uv run --no-sync python (Join-Path $root 'scripts/build_spdx_sbom.py') --artifacts-dir $artifacts `
  --repository-name FelixJI/vibeocr-next --version $Version
if ($LASTEXITCODE -ne 0) { throw 'SBOM build failed' }
