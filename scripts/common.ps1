$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Resolve-WindowRailDotnet([string]$Requested = '') {
    if ($Requested) { return (Get-Command $Requested -ErrorAction Stop).Source }
    $localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { return $localSdk }
    return (Get-Command dotnet -ErrorAction Stop).Source
}

function Initialize-WindowRailBuild {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'work\dotnet-home' }
    if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $projectRoot 'work\nuget' }
    New-Item -ItemType Directory -Path (Join-Path $projectRoot 'work') -Force | Out-Null
}

function Resolve-WindowRailPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}
