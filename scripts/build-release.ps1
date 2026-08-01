[CmdletBinding()]
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
$artifacts = Join-Path $root 'artifacts'
$build = Join-Path $root '.release-build'
$inputs = Join-Path $root '.release-input'
foreach ($path in @($artifacts, $build, $inputs)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
$protocol = Join-Path $inputs 'protocol'
$backend = Join-Path $inputs 'backend'
New-Item -ItemType Directory -Path $protocol, $backend -Force | Out-Null
gh release download v2.0.0 --repo FelixJI/vibeocr-protocol --dir $protocol
if ($LASTEXITCODE -ne 0) { throw 'Protocol release download failed' }
gh release download v0.7.0 --repo FelixJI/vibeocr-backend --dir $backend
if ($LASTEXITCODE -ne 0) { throw 'Backend release download failed' }
foreach ($item in @(
    @{ path = $protocol; repo = 'FelixJI/vibeocr-protocol' },
    @{ path = $backend; repo = 'FelixJI/vibeocr-backend' }
)) {
    Get-ChildItem -LiteralPath $item.path -File |
      Where-Object Name -ne 'SHA256SUMS' |
      ForEach-Object {
        gh attestation verify $_.FullName --repo $item.repo
        if ($LASTEXITCODE -ne 0) { throw "attestation failed: $($_.Name)" }
      }
}
$lock = Join-Path $root 'component-lock.json'
if (-not (Test-Path -LiteralPath $lock -PathType Leaf)) {
    throw 'component-lock.json is required'
}
python -m pip install pyinstaller==6.21.0
if ($LASTEXITCODE -ne 0) { throw 'updater build dependency install failed' }
dotnet restore (Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Next restore failed' }
dotnet restore (Join-Path $root 'src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Next bootstrapper restore failed' }
$product = Join-Path $build 'VibeOCR.Next'
dotnet publish (Join-Path $root 'src/dotnet/VibeOCR.App/VibeOCR.App.csproj') `
  -c Release -r win-x64 --self-contained false --no-restore -o $product
if ($LASTEXITCODE -ne 0) { throw 'Next publish failed' }
dotnet publish (Join-Path $root 'src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj') `
  -c Release --self-contained false --no-restore -o $product
if ($LASTEXITCODE -ne 0) { throw 'Next bootstrapper publish failed' }
python -m PyInstaller --noconfirm --clean --onefile --windowed `
  --name updater --distpath (Join-Path $build 'updater-dist') `
  --workpath (Join-Path $build 'updater-work') `
  --specpath (Join-Path $build 'updater-spec') `
  (Join-Path $root 'scripts/updater_main.py')
if ($LASTEXITCODE -ne 0) { throw 'Next updater build failed' }
Copy-Item -LiteralPath (Join-Path $build 'updater-dist/updater.exe') `
  -Destination $product
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $product
Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $product
$zip = Join-Path $artifacts "VibeOCR-Next-v$Version-win64.zip"
python (Join-Path $root 'scripts/package_product_release.py') `
  --product-root $product --frontend next `
  --frontend-version $Version `
  --source-commit (git -C $root rev-parse HEAD).Trim() `
  --component-lock $lock --protocol-release-dir $protocol `
  --backend-release-dir $backend --output $zip
if ($LASTEXITCODE -ne 0) { throw 'Next product binding failed' }
& (Join-Path $root 'scripts/verify_winui_artifact.ps1') -Artifact $zip
if ($LASTEXITCODE -ne 0) { throw 'Next artifact verification failed' }
Copy-Item -LiteralPath $lock -Destination $artifacts
python (Join-Path $root 'scripts/build_release_checksums.py') $artifacts `
  --sidecar-for $zip
if ($LASTEXITCODE -ne 0) { throw 'sidecar checksum build failed' }
Remove-Item -LiteralPath (Join-Path $artifacts 'SHA256SUMS') -Force
python (Join-Path $root 'scripts/build_spdx_sbom.py') --artifacts-dir $artifacts `
  --repository-name FelixJI/vibeocr-next --version $Version
if ($LASTEXITCODE -ne 0) { throw 'SBOM build failed' }
python (Join-Path $root 'scripts/build_release_checksums.py') $artifacts
if ($LASTEXITCODE -ne 0) { throw 'checksum build failed' }
