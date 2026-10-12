# Builds the Release exe and packs pc-privacy-cleaner.zip at the repository root.
# Run on Windows with the .NET SDK installed:  powershell -ExecutionPolicy Bypass -File build.ps1
#
# The zip holds one folder, pc-privacy-cleaner\, with the exe and the user guide (README.txt),
# so "압축 풀기" gives the user a tidy folder. Entry names use "/" so the zip also opens cleanly on macOS.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\PcPrivacyCleaner\PcPrivacyCleaner.csproj'

dotnet build $proj -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$exe = Join-Path $root 'src\PcPrivacyCleaner\bin\Release\net462\PcPrivacyCleaner.exe'
$guide = Join-Path $root 'README.txt'
$zip = Join-Path $root 'pc-privacy-cleaner.zip'

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $exe, 'pc-privacy-cleaner/PcPrivacyCleaner.exe')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $guide, 'pc-privacy-cleaner/README.txt')
}
finally {
    $archive.Dispose()
}
Get-Item $exe, $zip | Select-Object Name, Length
