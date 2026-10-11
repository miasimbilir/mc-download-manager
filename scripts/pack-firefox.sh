#!/bin/zsh
# Packs the WebExtension for Firefox: build/firefox/ (unpacked) + build/mc-download-manager-firefox-<version>.xpi
#
# Firefox shares the code in Extension/ with Chrome and Safari; only the manifest differs:
#   - background.scripts instead of background.service_worker (Firefox has no extension service workers)
#   - browser_specific_settings.gecko (add-on id, minimum version, data collection declaration)
#
# Release Firefox only installs add-ons signed by Mozilla. With AMO API credentials the xpi is signed as an
# unlisted (self-distributed) add-on, once per version (AMO rejects a version it has already signed).
# Credentials come from the environment (AMO_JWT_ISSUER / AMO_JWT_SECRET) or the login keychain:
#   security add-generic-password -s mc-download-manager-amo -a issuer -w 'user:…'
#   security add-generic-password -s mc-download-manager-amo -a secret -w '…'
# The signed file is written to build/mc-download-manager-firefox-<version>-signed.xpi and bundled into the
# app by scripts/build.sh. API keys: https://addons.mozilla.org/developers/addon/api/key/
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GECKO_ID="mc-download-manager@mstcvk.github.io"
VERSION="$(python3 -c "import json;print(json.load(open('$ROOT/Extension/manifest.json'))['version'])")"
STAGE="$ROOT/build/firefox"
XPI="$ROOT/build/mc-download-manager-firefox-$VERSION.xpi"
SIGNED="$ROOT/build/mc-download-manager-firefox-$VERSION-signed.xpi"

rm -rf "$STAGE"; mkdir -p "$STAGE"
rsync -a --exclude ".DS_Store" --exclude "manifest.json" "$ROOT/Extension/" "$STAGE/"
python3 - "$ROOT/Extension/manifest.json" "$STAGE/manifest.json" "$GECKO_ID" <<'PY'
import json, sys
src, dst, gecko_id = sys.argv[1:]
m = json.load(open(src))
m["background"] = {"scripts": ["background.js"]}
m["browser_specific_settings"] = {"gecko": {
    "id": gecko_id,
    "strict_min_version": "140.0",
    # Everything stays on this Mac: the extension only talks to the app on 127.0.0.1.
    "data_collection_permissions": {"required": ["none"]},
}}
json.dump(m, open(dst, "w"), indent=2, ensure_ascii=False)
PY

rm -f "$XPI"
(cd "$STAGE" && zip -qr -X "$XPI" .)
echo "✓ $XPI"

AMO_JWT_ISSUER="${AMO_JWT_ISSUER:-$(security find-generic-password -s mc-download-manager-amo -a issuer -w 2>/dev/null || true)}"
AMO_JWT_SECRET="${AMO_JWT_SECRET:-$(security find-generic-password -s mc-download-manager-amo -a secret -w 2>/dev/null || true)}"
if [[ -f "$SIGNED" ]]; then
  echo "✓ $SIGNED (already signed)"
elif [[ -n "$AMO_JWT_ISSUER" && -n "$AMO_JWT_SECRET" ]]; then
  echo "▶ Signing with addons.mozilla.org (unlisted)"
  OUTDIR="$ROOT/build/firefox-signed"
  rm -rf "$OUTDIR"; mkdir -p "$OUTDIR"
  # Passed through the environment (WEB_EXT_API_KEY/SECRET) so the secret never shows up in `ps`.
  WEB_EXT_API_KEY="$AMO_JWT_ISSUER" WEB_EXT_API_SECRET="$AMO_JWT_SECRET" \
    npx --yes web-ext@8 sign --source-dir "$STAGE" --artifacts-dir "$OUTDIR" --channel unlisted
  mv "$(ls "$OUTDIR"/*.xpi | head -1)" "$SIGNED"
  rm -rf "$OUTDIR"
  echo "✓ $SIGNED"
else
  echo "  (not signed: no AMO credentials; release Firefox can only load it temporarily)"
fi
