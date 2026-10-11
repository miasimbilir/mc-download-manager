#!/bin/zsh
# Builds the Windows app and packs it as build/MC-Download-Manager-<version>-win-x64.zip.
# Runs on macOS, Linux or Windows (Git Bash); needs the .NET 10 SDK. Nothing has to be compiled on the target PC.
set -euo pipefail
WIN="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(cd "$WIN/.." && pwd)"
RID="${RID:-win-x64}"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$WIN/src/MCDM.App/MCDM.App.csproj")"
NAME="MC Download Manager"
OUT="$WIN/build"
STAGE="$OUT/$NAME"

[[ -f "$WIN/Tools/yt-dlp.exe" && -f "$WIN/Tools/ffmpeg.exe" ]] || "$WIN/scripts/fetch-tools.sh"

rm -rf "$OUT/publish" "$STAGE"
dotnet publish "$WIN/src/MCDM.App/MCDM.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false \
  -o "$OUT/publish" -nologo -v q

mkdir -p "$STAGE/tools"
cp "$OUT/publish/MCDownloadManager.exe" "$STAGE/"
cp "$WIN/Tools/yt-dlp.exe" "$WIN/Tools/ffmpeg.exe" "$STAGE/tools/"
cp "$WIN/Tools/ffmpeg-LICENSE.txt" "$STAGE/tools/" 2>/dev/null || true
# The same unpacked extension the macOS app ships for Chrome
cp -R "$ROOT/Extension" "$STAGE/ChromeExtension"
find "$STAGE/ChromeExtension" -name .DS_Store -delete
# The Firefox add-on has to be the copy signed by Mozilla (Contents/Resources/FirefoxExtension.xpi of the macOS
# release with the same extension version). Without it the wizard says the add-on is not included.
if [[ -f "$WIN/Tools/FirefoxExtension.xpi" ]]; then cp "$WIN/Tools/FirefoxExtension.xpi" "$STAGE/"; else echo "  (no Tools/FirefoxExtension.xpi: the build will have no Firefox add-on)"; fi
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"
cp "$WIN/packaging/OKU-BENI.txt" "$WIN/packaging/READ-ME.txt" "$STAGE/"

ZIP="$OUT/MC-Download-Manager-$VERSION-$RID.zip"
rm -f "$ZIP"
(cd "$OUT" && zip -qr -X "$ZIP" "$NAME")
echo "✓ $ZIP"
ls -lh "$ZIP"
