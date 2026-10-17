# Uses the workspace SDK when present; otherwise uses the SDK on PATH.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $workspace 'tools/dotnet/dotnet.exe'
$trash = Join-Path $workspace 'trash'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = Join-Path $trash 'nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $trash 'nuget/http-cache'
$env:NUGET_SCRATCH = Join-Path $trash 'nuget/scratch'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $trash 'nuget/plugins-cache'
if (Test-Path -LiteralPath $localDotnet) {
    & $localDotnet @args
} else {
    & dotnet @args
}
exit $LASTEXITCODE
