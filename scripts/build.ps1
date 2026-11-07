param(
    [string]$Dotnet = '',
    [string]$OutputDirectory = 'dist\WindowRail'
)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-WindowRailBuild
$dotnetExecutable = Resolve-WindowRailDotnet $Dotnet
$output = Resolve-WindowRailPath $OutputDirectory
$runtimeRoot = Split-Path -Parent $dotnetExecutable

function Find-Version10([string]$Directory) {
    $item = Get-ChildItem -LiteralPath $Directory -Directory | Where-Object { $_.Name -match '^10\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $item) { throw "Missing .NET 10 in $Directory. Use a Windows x64 .NET 10 SDK installation." }
    return $item
}

$coreRuntime = Find-Version10 (Join-Path $runtimeRoot 'shared\Microsoft.NETCore.App')
$desktopRuntime = Find-Version10 (Join-Path $runtimeRoot 'shared\Microsoft.WindowsDesktop.App')
$fxrRuntime = Find-Version10 (Join-Path $runtimeRoot 'host\fxr')
Push-Location $projectRoot
try {
    & $dotnetExecutable publish 'src\WindowRail\WindowRail.csproj' -c Release --self-contained false -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=runtime -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WindowRail build failed.' }
} finally { Pop-Location }

foreach ($pair in @(
    @($coreRuntime.FullName, 'runtime\shared\Microsoft.NETCore.App'),
    @($desktopRuntime.FullName, 'runtime\shared\Microsoft.WindowsDesktop.App'),
    @($fxrRuntime.FullName, 'runtime\host\fxr')
)) {
    $destination = Join-Path $output $pair[1]
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $pair[0] -Destination $destination -Recurse -Force
}
foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $noticePath = Join-Path $runtimeRoot $notice
    if (-not (Test-Path -LiteralPath $noticePath)) { throw "Missing runtime notice: $noticePath" }
    Copy-Item -LiteralPath $noticePath -Destination (Join-Path $output 'runtime') -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\USAGE.txt') -Destination (Join-Path $output '使用说明.txt') -Force
Write-Output "Ready: $(Join-Path $output 'WindowRail.exe')"
