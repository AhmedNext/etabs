# Install the pinned official SDK into this workspace, without changing system PATH.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $workspace '.tools'
$installDir = Join-Path $toolsDir 'dotnet'
$sdkExe = Join-Path $installDir 'dotnet.exe'
if (Test-Path -LiteralPath $sdkExe) {
    $version = & $sdkExe --version
    if ($LASTEXITCODE -eq 0 -and $version -eq '8.0.426') { Write-Output "SDK $version is ready."; exit 0 }
    throw 'An existing local SDK differs from the pinned version. Review .tools/dotnet before replacing it.'
}
$archive = Join-Path $toolsDir 'dotnet-sdk.zip'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.426/dotnet-sdk-8.0.426-win-x64.zip'
$expected = '10261657109f1c62126f106a9ac966739b025b5f6b4a9f88ab7c6e73cacce9aec771895daa3267b8da933a83c32ee91bf458dc6704e1ddcd9bcf0d5edd036abb'
& curl.exe --fail --location --silent --show-error --output $archive $url
if ($LASTEXITCODE -ne 0) { throw 'SDK download failed.' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) { throw 'SDK SHA-512 checksum mismatch.' }
& tar.exe -xf $archive -C $installDir
if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed.' }
& $sdkExe --version
if ($LASTEXITCODE -ne 0) { throw 'The local SDK did not start.' }
