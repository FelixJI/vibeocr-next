<#
.SYNOPSIS
Compatibility entrypoint for the canonical VibeOCR Next release build.

.DESCRIPTION
The split repository has one release implementation: build-release.ps1.
This wrapper is retained for callers that used the former monorepo script
name. It does not read or build Classic, Backend, or Protocol source trees.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$canonical = Join-Path $PSScriptRoot 'build-release.ps1'
if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) {
    throw 'canonical Next release build script is missing'
}

& $canonical
if ($LASTEXITCODE -ne 0) {
    throw "Next release build failed with exit $LASTEXITCODE"
}
