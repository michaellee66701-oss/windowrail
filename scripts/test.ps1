param(
    [string]$Dotnet = '',
    [string]$ResultsDirectory = 'work\test-results'
)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-WindowRailBuild
$dotnetExecutable = Resolve-WindowRailDotnet $Dotnet
$results = Resolve-WindowRailPath $ResultsDirectory
New-Item -ItemType Directory -Path $results -Force | Out-Null
Push-Location $projectRoot
try {
    & $dotnetExecutable run --project 'tests\WindowRail.Tests\WindowRail.Tests.csproj' -c Release -- $results | Tee-Object -FilePath (Join-Path $results 'latest-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'WindowRail checks failed.' }
} finally { Pop-Location }
