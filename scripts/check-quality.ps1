$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    python -m ruff check scripts tests/runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    python -m ruff format --check scripts tests/runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    python -m pytest tests/runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    npm test --prefix src/dotnet/VibeOCR.App/WebAssets
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
