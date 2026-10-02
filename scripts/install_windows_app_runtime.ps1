<#
.SYNOPSIS
Installs the Windows App Runtime that matches the pinned Windows App SDK.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# The runtime version tracks the centrally pinned Microsoft.WindowsAppSDK
# version (Directory.Packages.props stays the single pin), so the installed
# framework-dependent runtime always matches the SDK the app builds against.
$centralProps = Join-Path $repo 'Directory.Packages.props'
[xml]$central = Get-Content -LiteralPath $centralProps -Raw
$sdkVersion = @(
    $central.Project.ItemGroup.PackageVersion |
        Where-Object Include -eq 'Microsoft.WindowsAppSDK' |
        Select-Object -ExpandProperty Version
)[0]
if (-not $sdkVersion) {
    throw 'Microsoft.WindowsAppSDK central version was not found in Directory.Packages.props'
}
$band = ($sdkVersion.Split('.') | Select-Object -First 2) -join '.'
$uri = "https://aka.ms/windowsappsdk/$band/$sdkVersion/windowsappruntimeinstall-x64.exe"

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else {
    [System.IO.Path]::GetTempPath()
}
$installer = Join-Path $tempRoot "WindowsAppRuntimeInstall-x64-$sdkVersion.exe"

Invoke-WebRequest -Uri $uri -OutFile $installer -TimeoutSec 300
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    throw (
        'Windows App Runtime installer signature is not valid Microsoft code: ' +
        "$($signature.Status), $($signature.SignerCertificate.Subject)"
    )
}

& $installer --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Windows App Runtime installer failed with exit code $LASTEXITCODE"
}

$runtimePackages = @()
try {
    $runtimePackages = @(
        Get-AppxPackage -AllUsers |
          Where-Object Name -Like '*WindowsAppRuntime*'
    )
} catch {
    # The enumeration is an advisory report only; it requires elevation for
    # -AllUsers, and the App testhost stays the authoritative runtime probe.
    Write-Warning (
        'Windows App Runtime package enumeration failed: ' + $_.Exception.Message
    )
}
if ($runtimePackages.Count -eq 0) {
    Write-Warning (
        'The installer succeeded, but Get-AppxPackage -AllUsers did not ' +
        'enumerate a Windows App Runtime package. The App testhost is the ' +
        'authoritative runtime probe.'
    )
} else {
    Write-Host (
        'Windows App Runtime packages installed: ' +
        (($runtimePackages | Select-Object -ExpandProperty PackageFullName) -join ', ')
    )
}
