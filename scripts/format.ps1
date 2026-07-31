param(
    [switch]$SkipDotNet
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    python -m ruff check --fix scripts tests/runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    python -m ruff format scripts tests/runtime
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not $SkipDotNet) {
        $projects = @(
            "src/dotnet/VibeOCR.Platform/VibeOCR.Platform.csproj",
            "src/dotnet/VibeOCR.App/VibeOCR.App.csproj",
            "src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj"
        )
        foreach ($project in $projects) {
            dotnet format $project --no-restore
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }
}
finally {
    Pop-Location
}
