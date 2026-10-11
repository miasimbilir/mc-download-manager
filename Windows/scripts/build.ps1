# Builds the Windows app on Windows itself:  powershell -ExecutionPolicy Bypass -File Windows\scripts\build.ps1
# Needs the .NET 10 SDK. yt-dlp.exe and ffmpeg.exe must be in Windows\Tools (copy them from the "tools"
# folder of an existing build, or run fetch-tools.sh in Git Bash). Keep this file ASCII-only: Windows
# PowerShell 5.1 reads scripts without a BOM in the ANSI code page.
$ErrorActionPreference = 'Stop'
$win = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $win
$tools = Join-Path $win 'Tools'
$out = Join-Path $win 'build'
$stage = Join-Path $out 'MC Download Manager'

[xml]$proj = Get-Content (Join-Path $win 'src\MCDM.App\MCDM.App.csproj')
$version = $proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1

foreach ($tool in 'yt-dlp.exe', 'ffmpeg.exe') {
    if (-not (Test-Path (Join-Path $tools $tool))) { throw "Missing $tool in $tools" }
}

foreach ($dir in (Join-Path $out 'publish'), $stage) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

dotnet publish (Join-Path $win 'src\MCDM.App\MCDM.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false `
    -o (Join-Path $out 'publish') -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null
Copy-Item (Join-Path $out 'publish\MCDownloadManager.exe') $stage
Copy-Item (Join-Path $tools 'yt-dlp.exe'), (Join-Path $tools 'ffmpeg.exe') (Join-Path $stage 'tools')
if (Test-Path (Join-Path $tools 'ffmpeg-LICENSE.txt')) { Copy-Item (Join-Path $tools 'ffmpeg-LICENSE.txt') (Join-Path $stage 'tools') }
# The same unpacked extension the macOS app ships for Chrome
Copy-Item -Recurse (Join-Path $root 'Extension') (Join-Path $stage 'ChromeExtension')
# The Firefox add-on has to be the copy signed by Mozilla: Contents\Resources\FirefoxExtension.xpi of the
# macOS release with the same extension version. Without it the wizard says the add-on is not included.
$xpi = Join-Path $tools 'FirefoxExtension.xpi'
if (Test-Path $xpi) { Copy-Item $xpi $stage } else { Write-Warning "No FirefoxExtension.xpi in $tools - the build will have no Firefox add-on" }
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $win 'packaging\OKU-BENI.txt'), (Join-Path $win 'packaging\READ-ME.txt') $stage

$zip = Join-Path $out "MC-Download-Manager-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Entries are named by hand: under Windows PowerShell 5.1 both Compress-Archive and ZipFile.CreateFromDirectory
# write backslashes, which unzip on macOS and Linux turns into files called "MC Download Manager\tools\ffmpeg.exe".
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $name = (Split-Path -Leaf $stage) + '/' + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}
Write-Host "OK $zip"
