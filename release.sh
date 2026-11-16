#!/usr/bin/env bash
# Build, sign and publish a release. Run from Git Bash:
#   ./release.sh "What changed"
# The version comes from <Version> in the csproj; the signing key from
# ~/.orv/update-signing.pem (or the path in ORV_SIGNING_KEY).
set -euo pipefail
cd "$(dirname "$0")"

NOTES="${1:?Usage: ./release.sh \"What changed\"}"
KEY="${ORV_SIGNING_KEY:-$HOME/.orv/update-signing.pem}"
[ -f "$KEY" ] || { echo "Signing key not found: $KEY" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Commit your changes first" >&2; exit 1; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' OpenRemoteViewer/OpenRemoteViewer.csproj)
TAG="v$VERSION"
EXE="OpenRemoteViewer/bin/Release/net8.0-windows/win-x64/publish/OpenRemoteViewer.exe"

(cd OpenRemoteViewer && dotnet publish -c Release)

openssl dgst -sha256 -sign "$KEY" -out "$EXE.sig" "$EXE"
# check the signature with the public half before publishing
PUB="$(mktemp)"
openssl ec -in "$KEY" -pubout -out "$PUB" 2>/dev/null
openssl dgst -sha256 -verify "$PUB" -signature "$EXE.sig" "$EXE"
rm -f "$PUB"

git push origin HEAD
git tag "$TAG"
git push origin "$TAG"
gh release create "$TAG" "$EXE" "$EXE.sig" --verify-tag --title "$TAG" --notes "$NOTES"
