function New-ProductSmokeCandidate {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Work,
        [string]$Prepared
    )

    # Callers have resolved both paths, checked disjoint roots and rejected source state.
    $inputRoot = $Source
    if ($Prepared) {
        $inputRoot = (Resolve-Path -LiteralPath $Prepared).Path.TrimEnd('\')
        $parent = Split-Path -Parent $inputRoot
        if ((Split-Path -Leaf $inputRoot) -ne 'candidate' -or
            (Split-Path -Leaf $parent) -cnotmatch '^(ve-[0-9a-f]{12}|vibeocr-managed-e2e-[0-9a-f]{32})$' -or
            -not $parent.StartsWith($Work + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath (Join-Path $inputRoot 'state') -PathType Container)) {
            throw 'Prepared candidate must be a retained isolated smoke candidate inside WorkRoot'
        }
        foreach ($marker in @('app\VibeOCR.WinUI.exe', 'runtime\backend\runtime-manifest.json',
                              'runtime\env\runtime-env\python.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path $inputRoot $marker) -PathType Leaf)) {
                throw "Prepared candidate marker missing: $marker"
            }
        }
        $identity = Get-Content -LiteralPath (Join-Path $Source 'app\metadata\component-identities.json') -Raw | ConvertFrom-Json
        foreach ($field in @('component', 'repository', 'version', 'source_sha')) {
            if (-not $identity.project.$field) { throw "Source product binding missing: $field" }
        }
        foreach ($name in @('product-layout.json', 'component-identities.json')) {
            $metadata = "app\metadata\$name"
            if ([IO.File]::ReadAllText((Join-Path $Source $metadata)) -cne
                [IO.File]::ReadAllText((Join-Path $inputRoot $metadata))) {
                throw "Prepared candidate does not match source $name"
            }
        }
    }
    $smokeRoot = Join-Path $Work "ve-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
    $candidate = Join-Path $smokeRoot 'candidate'
    New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
    Get-ChildItem -LiteralPath $inputRoot -Force | Copy-Item -Destination $candidate -Recurse -ErrorAction Stop
    return $candidate
}
