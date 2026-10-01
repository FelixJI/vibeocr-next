<#
.SYNOPSIS
Write-only synthetic clipboard image setter for the codes workbench smoke.

.DESCRIPTION
Feeds one synthetic PNG into the clipboard for the real App's paste command.
Permissions are intentionally narrow: the image must live inside the current
smoke root, be a bounded PNG file, and nothing here ever reads clipboard
content (the only clipboard query is the ContainsImage presence check of the
image this script itself just set), user files, registry, or global settings.
Run under `pwsh -STA` because System.Windows.Forms clipboard requires an STA
thread. The setter confirms success (SetImage + ContainsImage) before
the harness lets the App paste.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SmokeRoot,
    [Parameter(Mandatory = $true)][string]$ImagePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$root = [IO.Path]::GetFullPath($SmokeRoot).TrimEnd('\')
$image = [IO.Path]::GetFullPath($ImagePath)
if (-not $image.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Clipboard fixture image is outside this smoke root: $image"
}
$item = Get-Item -LiteralPath $image -ErrorAction Stop
if ($item.PSIsContainer) {
    throw "Clipboard fixture image is not a file: $image"
}
if ($item.Length -le 0 -or $item.Length -gt 8MB) {
    throw "Clipboard fixture image size is out of bounds: $($item.Length)"
}
$stream = [IO.File]::OpenRead($image)
try {
    $signature = New-Object byte[] 8
    $read = $stream.Read($signature, 0, 8)
} finally {
    $stream.Dispose()
}
# PNG signature 89 50 4E 47 0D 0A 1A 0A; synthetic PNG input only.
if ($read -ne 8 -or $signature[0] -ne 0x89 -or $signature[1] -ne 0x50 -or
    $signature[2] -ne 0x4E -or $signature[3] -ne 0x47 -or
    $signature[4] -ne 0x0D -or $signature[5] -ne 0x0A -or $signature[6] -ne 0x1A -or
    $signature[7] -ne 0x0A) {
    throw "Clipboard fixture accepts synthetic PNG input only: $image"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::FromFile($image)
try {
    [System.Windows.Forms.Clipboard]::SetImage($bitmap)
    # SetImage uses SetDataObject(copy: true), so formats survive this process.
    if (-not [System.Windows.Forms.Clipboard]::ContainsImage()) {
        throw 'Clipboard image setter did not take effect.'
    }
} finally {
    $bitmap.Dispose()
}
Write-Output 'clipboard-image-set'
