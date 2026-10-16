$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'dist/EasyShare')).Path
$diagnosticsRoot = Join-Path $releaseRoot 'selftest'
$destination = Join-Path $projectRoot 'dist/EasyShare-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression
$fileStream = [System.IO.File]::Create($destination)
$archive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File) {
        if ($file.FullName.StartsWith($diagnosticsRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $relative = $file.FullName.Substring($releaseRoot.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry('EasyShare/' + $relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $input = $file.OpenRead()
        $output = $entry.Open()
        try {
            $input.CopyTo($output)
        } finally {
            $input.Dispose()
            $output.Dispose()
        }
        $count++
    }
} finally {
    $archive.Dispose()
    $fileStream.Dispose()
}
Write-Output "Packaged $count files; local selftest diagnostics excluded."
