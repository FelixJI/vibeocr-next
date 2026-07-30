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
    Get-AppxPackage |
      Where-Object Name -Like 'Microsoft.WindowsAppRuntime.2.2*'
)
if ($runtimePackages.Count -eq 0) {
    throw 'Windows App Runtime 2.2 package was not registered'
}

Write-Host (
    'Windows App Runtime 2.2 installed: ' +
    (($runtimePackages | Select-Object -ExpandProperty PackageFullName) -join ', ')
)
