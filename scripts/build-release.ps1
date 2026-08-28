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
$product = Join-Path $build 'VibeOCR'
Write-CiStage 'product-layout'
uv run --no-sync python (Join-Path $root 'scripts/product_layout.py') stage `
  --product-root $product `
  --app-publish-root $appPublish `
  --bootstrapper-executable (Join-Path $bootstrapperPublish 'VibeOCR.Bootstrapper.exe') `
  --component-lock $lock --component-identities $identity `
  --backend-release-dir $backend `
  --license-file (Join-Path $root 'LICENSE') `
  --changelog-file (Join-Path $root 'CHANGELOG.md')
if ($LASTEXITCODE -ne 0) { throw 'VibeOCR product layout staging failed' }
Write-CiStage 'product-finalize'
uv run --no-sync python (Join-Path $root 'scripts/finalize_product_release.py') `
  --product-root $product --frontend next `
  --frontend-version $Version `
  --source-commit (git -C $root rev-parse HEAD).Trim() `
  --component-lock $lock --protocol-release-dir $protocol `
  --backend-release-dir $backend
if ($LASTEXITCODE -ne 0) { throw 'Next product binding failed' }
uv run --no-sync python (Join-Path $root 'scripts/product_layout.py') verify `
  --product-root $product
if ($LASTEXITCODE -ne 0) { throw 'Next product release closure verification failed' }
Write-CiStage 'velopack-package'
$dotnet = if ($env:DOTNET_HOST_PATH) { $env:DOTNET_HOST_PATH } else { 'dotnet' }
& $dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Velopack tool restore failed' }
$velopackOutput = Join-Path $build 'velopack-output'
$deltaPlanFile = Join-Path $build 'velopack-delta-plan.json'
$deltaPrepareArgs = @(
  '--repository', 'FelixJI/vibeocr-next', '--pack-id', 'VibeOCRNext',
  '--target-version', $Version, '--output-dir', $velopackOutput,
  '--plan-file', $deltaPlanFile
)
if ($env:AUTOMATION_SOURCE_SHA) {
    $releaseTagCommit = (& git -C $root rev-list -n 1 "v$Version" 2>$null)
    if ($LASTEXITCODE -eq 0 -and $releaseTagCommit.Trim() -eq $env:AUTOMATION_SOURCE_SHA) {
        $deltaPrepareArgs += '--reproduce-published-delta'
    }
}
uv run --no-sync python (Join-Path $root 'scripts/prepare_velopack_delta.py') @deltaPrepareArgs
if ($LASTEXITCODE -ne 0) { throw 'Velopack delta base preparation failed' }
$deltaPlan = Get-Content -LiteralPath $deltaPlanFile -Raw | ConvertFrom-Json
$deltaMode = [string]$deltaPlan.delta_mode
& $dotnet tool run vpk pack `
  --packId VibeOCRNext --packVersion $Version --packDir $product `
  --mainExe VibeOCR.exe --outputDir $velopackOutput --channel win `
  --runtime win-x64 --delta $deltaMode --packTitle VibeOCR --packAuthors FelixJI `
  --noInst `
  --icon (Join-Path $root 'assets/brand/generated/vibeocr.ico')
if ($LASTEXITCODE -ne 0) { throw 'Velopack package build failed' }
$normalizeFeedArgs = @(
  '--feed', (Join-Path $velopackOutput 'releases.win.json'),
  '--pack-id', 'VibeOCRNext', '--target-version', $Version
)
if ($deltaPlan.base_version) {
    $normalizeFeedArgs += @('--expected-base-version', [string]$deltaPlan.base_version)
}
uv run --no-sync python (Join-Path $root 'scripts/normalize_velopack_feed.py') @normalizeFeedArgs
if ($LASTEXITCODE -ne 0) { throw 'Velopack feed normalization failed' }
$full = @(Get-ChildItem -LiteralPath $velopackOutput -Filter "VibeOCRNext-$Version-full.nupkg")
$delta = @(Get-ChildItem -LiteralPath $velopackOutput -Filter "VibeOCRNext-$Version-delta.nupkg")
$portable = @(Get-ChildItem -LiteralPath $velopackOutput -Filter '*-Portable.zip')
$feed = @(Get-ChildItem -LiteralPath $velopackOutput -Filter 'releases.win.json')
if ($full.Count -ne 1 -or $delta.Count -gt 1 -or $portable.Count -ne 1 -or $feed.Count -ne 1) {
    throw 'Velopack output set is incomplete or ambiguous'
}
if (@(Get-ChildItem -LiteralPath $velopackOutput -Filter '*-Setup.exe').Count -ne 0) {
    throw 'Velopack produced a Setup installer although portable-only is configured'
}
$fullOldOutput = Join-Path $build 'velopack-full-e2e-old'
& $dotnet tool run vpk pack `
  --packId VibeOCRNext --packVersion 0.0.1 `
  --packDir $product --mainExe VibeOCR.exe --outputDir $fullOldOutput `
  --channel win --runtime win-x64 --delta None `
  --packTitle VibeOCR --packAuthors FelixJI --noInst `
  --icon (Join-Path $root 'assets/brand/generated/vibeocr.ico')
if ($LASTEXITCODE -ne 0) { throw 'Velopack full E2E old Portable build failed' }
uv run --no-sync python `
  (Join-Path $root 'scripts/verify_velopack_portable_delta_e2e.py') `
  --old-portable (Join-Path $fullOldOutput 'VibeOCRNext-win-Portable.zip') `
  --new-feed $velopackOutput --target-version $Version `
  --require-package-type full `
  --legacy-state-layout `
  --work-dir (Join-Path $build 'velopack-portable-full-e2e') --timeout 1200
if ($LASTEXITCODE -ne 0) { throw 'Velopack Portable full fallback E2E failed' }
if ($deltaPlan.base_package) {
    $deltaOldOutput = Join-Path $build 'velopack-delta-e2e-old'
    & $dotnet tool run vpk pack `
      --packId VibeOCRNext --packVersion ([string]$deltaPlan.base_version) `
      --packDir $product --mainExe VibeOCR.exe --outputDir $deltaOldOutput `
      --channel win --runtime win-x64 --delta None `
      --packTitle VibeOCR --packAuthors FelixJI --noInst `
      --icon (Join-Path $root 'assets/brand/generated/vibeocr.ico')
    if ($LASTEXITCODE -ne 0) { throw 'Velopack delta E2E old Portable build failed' }
    uv run --no-sync python `
      (Join-Path $root 'scripts/verify_velopack_portable_delta_e2e.py') `
      --old-portable (Join-Path $deltaOldOutput 'VibeOCRNext-win-Portable.zip') `
      --old-package (Join-Path $velopackOutput ([string]$deltaPlan.base_package)) `
      --new-feed $velopackOutput --target-version $Version `
      --require-package-type delta `
      --work-dir (Join-Path $build 'velopack-portable-delta-e2e') --timeout 1200
    if ($LASTEXITCODE -ne 0) { throw 'Velopack Portable delta E2E failed' }
}
Copy-Item -LiteralPath $full[0].FullName -Destination (Join-Path $artifacts "VibeOCRNext-$Version-full.nupkg")
if ($delta.Count -eq 1) {
    Copy-Item -LiteralPath $delta[0].FullName `
      -Destination (Join-Path $artifacts "VibeOCRNext-$Version-delta.nupkg")
}
Copy-Item -LiteralPath $portable[0].FullName `
  -Destination (Join-Path $artifacts "VibeOCRNext-v$Version-win-x64.zip")
Copy-Item -LiteralPath $feed[0].FullName -Destination (Join-Path $artifacts 'releases.win.json')
Write-CiStage 'artifact-verify'
uv run --no-sync python (Join-Path $root 'scripts/build_release_checksums.py') $artifacts
if ($LASTEXITCODE -ne 0) { throw 'Release checksum build failed' }
Remove-Item -LiteralPath (Join-Path $artifacts 'SHA256SUMS') -Force
uv run --no-sync python (Join-Path $root 'scripts/build_spdx_sbom.py') --artifacts-dir $artifacts `
  --repository-name FelixJI/vibeocr-next --version $Version
if ($LASTEXITCODE -ne 0) { throw 'SBOM build failed' }
