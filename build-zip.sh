#!/bin/sh
# Rebuild pc-privacy-cleaner.zip at the repo root from the three tool files.
# The zip keeps a top-level pc-privacy-cleaner/ folder so "extract here" stays tidy.
set -eu
cd "$(dirname "$0")"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/pc-privacy-cleaner"
cp Clean-PersonalData.ps1 run.bat README.txt "$tmp/pc-privacy-cleaner/"
rm -f pc-privacy-cleaner.zip
# -X drops macOS extended attributes so no __MACOSX/._ files end up in the zip
(cd "$tmp" && zip -rX - pc-privacy-cleaner) > pc-privacy-cleaner.zip
unzip -l pc-privacy-cleaner.zip
