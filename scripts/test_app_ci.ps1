[CmdletBinding()]
param(
    [string]$Filter,
    [Nullable[int]]$ExpectedPassed
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
if ([int]$counters.total -le 0 -or
    [int]$counters.passed -ne [int]$counters.total -or
    [int]$counters.failed -ne 0 -or
    [int]$counters.notExecuted -ne 0) {
    throw (
        'App test result is incomplete: ' +
        "total=$($counters.total), passed=$($counters.passed), " +
        "failed=$($counters.failed), notExecuted=$($counters.notExecuted); " +
        'expected a nonempty all-passed run'
    )
}

if ($PSBoundParameters.ContainsKey('ExpectedPassed') -and
    [int]$counters.total -ne $ExpectedPassed) {
    throw (
        'App test result count mismatch: ' +
        "total=$($counters.total); expected=$ExpectedPassed"
    )
}

Write-Host "App test result verified: $($counters.passed)/$($counters.total) passed with Completed outcome."
