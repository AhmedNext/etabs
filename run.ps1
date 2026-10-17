param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $AppArgs
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $root 'tools/dotnet.ps1'
$project = Join-Path $root 'src/Inspector.Cli'

if ($AppArgs.Count -eq 0) {
    $report = Join-Path $root ("docs/reports/inspection_{0}.html" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
    & $dotnet run --project $project -- report --format html --submission --output $report
} else {
    & $dotnet run --project $project -- @AppArgs
}
exit $LASTEXITCODE
