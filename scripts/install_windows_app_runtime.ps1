[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:RUNNER_TEMP 'WindowsAppRuntimeInstall-x64-2.2.0.exe'
$uri = (
    'https://aka.ms/windowsappsdk/2.2/2.2.0/' +
    'windowsappruntimeinstall-x64.exe'
)

Invoke-WebRequest -Uri $uri -OutFile $installer
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

$runtimePackages = @(
    Get-AppxPackage -AllUsers |
      Where-Object Name -Like '*WindowsAppRuntime*'
)
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
