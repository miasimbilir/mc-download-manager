#!/bin/zsh
# Downloads the Windows yt-dlp and ffmpeg binaries that get bundled with the app (into Windows/Tools/).
# Sources:
#   yt-dlp : https://github.com/yt-dlp/yt-dlp/releases            (yt-dlp.exe, checked against SHA2-256SUMS)
#   ffmpeg : https://github.com/yt-dlp/FFmpeg-Builds/releases     (win64 GPL build, checked against checksums.sha256)
set -euo pipefail
TOOLS="$(cd "$(dirname "$0")/.." && pwd)/Tools"
mkdir -p "$TOOLS"
cd "$TOOLS"

YTDLP_BASE="${YTDLP_BASE:-https://github.com/yt-dlp/yt-dlp/releases/latest/download}"
FFMPEG_BASE="${FFMPEG_BASE:-https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest}"
FFMPEG_ZIP="${FFMPEG_ZIP:-ffmpeg-master-latest-win64-gpl.zip}"

sha() { shasum -a 256 "$1" | cut -d' ' -f1; }

if [[ ! -f yt-dlp.exe ]]; then
  echo "→ downloading yt-dlp.exe…"
  curl -fL --retry 3 -o yt-dlp.exe.tmp "$YTDLP_BASE/yt-dlp.exe"
  want="$(curl -fsSL --retry 3 "$YTDLP_BASE/SHA2-256SUMS" | awk '$2=="yt-dlp.exe"{print $1}')"
  [[ -n "$want" && "$(sha yt-dlp.exe.tmp)" == "$want" ]] || { echo "yt-dlp.exe: checksum mismatch" >&2; rm -f yt-dlp.exe.tmp; exit 1; }
  mv yt-dlp.exe.tmp yt-dlp.exe
fi

if [[ ! -f ffmpeg.exe ]]; then
  if [[ ! -f "$FFMPEG_ZIP" ]]; then
    echo "→ downloading $FFMPEG_ZIP…"
    curl -fL --retry 3 -o "$FFMPEG_ZIP.tmp" "$FFMPEG_BASE/$FFMPEG_ZIP"
    mv "$FFMPEG_ZIP.tmp" "$FFMPEG_ZIP"
  fi
  want="$(curl -fsSL --retry 3 "$FFMPEG_BASE/checksums.sha256" | awk -v f="$FFMPEG_ZIP" '$2==f{print $1}')"
  [[ -n "$want" && "$(sha "$FFMPEG_ZIP")" == "$want" ]] || { echo "$FFMPEG_ZIP: checksum mismatch (delete it and run again)" >&2; exit 1; }
  rm -rf _ffmpeg && mkdir _ffmpeg
  unzip -q -j -o "$FFMPEG_ZIP" '*/bin/ffmpeg.exe' '*/LICENSE.txt' -d _ffmpeg
  mv _ffmpeg/ffmpeg.exe ffmpeg.exe
  mv _ffmpeg/LICENSE.txt ffmpeg-LICENSE.txt
  rm -rf _ffmpeg "$FFMPEG_ZIP"
fi

# The Firefox add-on must be the copy Mozilla signed for the upstream project; it only exists inside the
# macOS release (Contents/Resources/FirefoxExtension.xpi) of the same extension version.
if [[ ! -f FirefoxExtension.xpi ]]; then
  EXT_VERSION="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$TOOLS/../../Extension/manifest.json" | head -1)"
  DMG_URL="${DMG_URL:-https://github.com/mstcvk/mc-download-manager/releases/download/v$EXT_VERSION/MC-Download-Manager-$EXT_VERSION.dmg}"
  if [[ "$(uname)" == "Darwin" ]]; then
    echo "→ taking FirefoxExtension.xpi out of the macOS release $EXT_VERSION…"
    curl -fL --retry 3 -o _release.dmg "$DMG_URL"
    rm -rf _dmg && mkdir _dmg
    hdiutil attach -nobrowse -readonly -mountpoint _dmg _release.dmg >/dev/null 2>&1
    cp "_dmg/MC Download Manager.app/Contents/Resources/FirefoxExtension.xpi" FirefoxExtension.xpi || true
    hdiutil detach _dmg >/dev/null 2>&1 || true
    rm -rf _dmg _release.dmg
  fi
  [[ -f FirefoxExtension.xpi ]] || echo "  (no FirefoxExtension.xpi: copy it from the macOS release $EXT_VERSION into $TOOLS, or the build has no Firefox add-on)"
fi

echo "✓ Tools ready:"
ls -lh yt-dlp.exe ffmpeg.exe
