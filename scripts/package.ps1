param(
    [string]$PublishDirectory = 'dist\WindowRail',
    [string]$ArtifactsDirectory = 'artifacts'
)
. (Join-Path $PSScriptRoot 'common.ps1')
$publish = Resolve-WindowRailPath $PublishDirectory
$artifacts = Resolve-WindowRailPath $ArtifactsDirectory
foreach ($required in @('WindowRail.exe', 'WindowRail.dll', 'runtime', '使用说明.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Missing build output: $required. Run scripts/build.ps1 first." }
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = Join-Path $artifacts 'WindowRail-win-x64.zip'
$stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $publish -File -Recurse)) {
        $relative = $file.FullName.Substring($publish.TrimEnd('\').Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)data/' -or $file.Name -like '*.log' -or $file.Name -like '*.tmp') { continue }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, 'WindowRail/' + $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $artifacts 'SHA256SUMS.txt'), "$hash  WindowRail-win-x64.zip`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Package: $archivePath"
