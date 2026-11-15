<#
.SYNOPSIS
Expand the locked WebView2 Fixed Version runtime CAB into a product tree.

.DESCRIPTION
Consumes config/runtime/webview2-runtime.lock.json (the only version source;
no latest-version fallback) and materializes the complete vendor tree as
<Destination>\WebView2Runtime with msedgewebview2.exe at its root.

The CAB cache lives in .release-input/webview2 inside the checkout. Set
VIBEOCR_WEBVIEW2_CAB to point at an already verified absolute CAB path
(same contract as VIBEOCR_PYTHON_ARCHIVE); the file is still hash-checked
against the lock. A cache miss is only filled by downloading the exact
locked source_url; any sha256/size/expansion mismatch fails closed.

Expansion happens into a dedicated staging directory; the CAB's single
vendor root directory is renamed into place so the original hierarchy
(including same-named LICENSE files in different subdirectories) is kept
verbatim. No file is filtered or trimmed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPublishRoot,
    [string]$LockFile,
    [string]$CacheRoot
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $LockFile) {
    $LockFile = Join-Path $root 'config/runtime/webview2-runtime.lock.json'
}
$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
foreach ($field in @('version', 'architecture', 'vendor_root', 'source_url', 'sha256', 'size_bytes', 'file_count')) {
    if (-not $lock.$field) {
        throw "WebView2 runtime lock is missing '$field'"
    }
}
$expectedName = [IO.Path]::GetFileName(([uri]$lock.source_url).AbsolutePath)
if ($expectedName -ne "Microsoft.WebView2.FixedVersionRuntime.$($lock.version).$($lock.architecture).cab") {
    throw 'WebView2 runtime lock URL does not match the locked version/architecture'
}

if (-not $CacheRoot) {
    $CacheRoot = Join-Path $root '.release-input/webview2'
}
$expandTool = Join-Path $env:SystemRoot 'System32\expand.exe'
if (-not (Test-Path -LiteralPath $expandTool -PathType Leaf)) {
    throw 'Windows CAB expansion utility is unavailable'
}

$cab = if ($env:VIBEOCR_WEBVIEW2_CAB) {
    [IO.Path]::GetFullPath($env:VIBEOCR_WEBVIEW2_CAB)
} else {
    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    Join-Path $CacheRoot $expectedName
}
if (-not (Test-Path -LiteralPath $cab -PathType Leaf)) {
    if ($env:VIBEOCR_WEBVIEW2_CAB) {
        throw 'Configured WebView2 runtime CAB is missing'
    }
    $partial = "$cab.partial"
    Invoke-WebRequest -Uri $lock.source_url -OutFile $partial -TimeoutSec 300
    if ((Get-Item -LiteralPath $partial).Length -ne [long]$lock.size_bytes -or
        (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw 'Downloaded WebView2 runtime CAB does not match webview2-runtime.lock.json'
    }
    Move-Item -LiteralPath $partial -Destination $cab
}
if ((Get-Item -LiteralPath $cab).Length -ne [long]$lock.size_bytes) {
    throw 'WebView2 runtime CAB size does not match webview2-runtime.lock.json'
}
if ((Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) {
    throw 'WebView2 runtime CAB does not match webview2-runtime.lock.json'
}

$publishRoot = (Resolve-Path -LiteralPath $AppPublishRoot).Path
if ((Get-Item -LiteralPath $publishRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'WebView2 runtime destination must not be behind a reparse point'
}
$destination = Join-Path $publishRoot 'WebView2Runtime'
if (Test-Path -LiteralPath $destination) {
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw 'WebView2 runtime destination already exists and is not empty'
    }
    Remove-Item -LiteralPath $destination -Force
}

$staging = Join-Path $publishRoot ".webview2-expand-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    & $expandTool -F:* $cab $staging | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'WebView2 runtime CAB expansion failed'
    }
    # The CAB carries one vendor root directory; its contents become
    # WebView2Runtime verbatim (original hierarchy, no flattening/trim).
    $children = @(Get-ChildItem -LiteralPath $staging -Force)
    if ($children.Count -ne 1 -or
        -not ($children[0].PSIsContainer) -or
        $children[0].Name -cne [string]$lock.vendor_root) {
        throw 'WebView2 runtime CAB did not expand to the locked vendor root'
    }
    $vendorRoot = $children[0].FullName
    foreach ($marker in @('msedgewebview2.exe', 'icudtl.dat')) {
        if (-not (Test-Path -LiteralPath (Join-Path $vendorRoot $marker) -PathType Leaf)) {
            throw "WebView2 runtime vendor tree is missing $marker"
        }
    }
    $files = @(Get-ChildItem -LiteralPath $vendorRoot -Recurse -Force -File)
    if ($files.Count -ne [int]$lock.file_count) {
        throw "WebView2 runtime vendor tree has $($files.Count) files; lock declares $($lock.file_count)"
    }
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "WebView2 runtime vendor tree contains a reparse point: $($file.Name)"
        }
    }
    # Rename the vendor root into the product tree: one atomic directory
    # move; the original hierarchy (including same-named LICENSE files in
    # different subdirectories) is kept verbatim.
    [IO.Directory]::Move($vendorRoot, $destination)
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -eq 0) {
        throw 'WebView2 runtime assembly produced an empty destination'
    }
} finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $destination 'msedgewebview2.exe') -PathType Leaf)) {
    throw 'Assembled WebView2Runtime is missing msedgewebview2.exe at its root'
}
Write-Output $destination
