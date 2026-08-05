[CmdletBinding()]
param(
    [string]$Filter,
    [int]$ExpectedPassed = 73
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'tests/dotnet/VibeOCR.App.Tests/VibeOCR.App.Tests.csproj'
$results = Join-Path $root '.test-results/app'

if (Test-Path -LiteralPath $results) {
    Remove-Item -LiteralPath $results -Recurse -Force
}
New-Item -ItemType Directory -Path $results -Force | Out-Null

$arguments = @(
    'test',
    $project,
    '-c', 'Release',
    '--no-restore',
    '--blame-hang',
    '--blame-hang-timeout', '2m',
    '--blame-hang-dump-type', 'none',
    '--logger', 'console;verbosity=detailed',
    '--logger', 'trx;LogFileName=app-tests.trx',
    '--results-directory', $results
)
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $arguments += @('--filter', $Filter)
}

dotnet @arguments
$testExitCode = $LASTEXITCODE

$trxPath = Join-Path $results 'app-tests.trx'
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw 'App test run did not produce app-tests.trx'
}

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$summary = $trx.TestRun.ResultSummary
$counters = $summary.Counters
if ($testExitCode -ne 0) {
    throw "App test command failed with exit code $testExitCode"
}
if ($summary.outcome -ne 'Completed') {
    throw "App test run outcome was $($summary.outcome), expected Completed"
}
if ([int]$counters.total -ne $ExpectedPassed -or
    [int]$counters.passed -ne $ExpectedPassed -or
    [int]$counters.failed -ne 0) {
    throw (
        'App test result count mismatch: ' +
        "total=$($counters.total), passed=$($counters.passed), " +
        "failed=$($counters.failed); expected $ExpectedPassed/$ExpectedPassed/0"
    )
}

Write-Host (
    "App test result verified: $ExpectedPassed/$ExpectedPassed passed " +
    'with Completed outcome.'
)
