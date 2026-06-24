#!/usr/bin/env bash
# Download the LiveKit FFI native library (the Rust client core used by
# Livekit.Rtc.Dotnet) for a given platform into a target directory.
#
# The native library is NOT bundled in the Livekit.Rtc.Dotnet NuGet, so it must be
# provisioned alongside the published app for the 'record' and 'dispatch' voice
# modes. The Dockerfile fetches the linux build itself; CI uses this for win-x64.
#
# Usage:
#   scripts/download-livekit-ffi.sh <platform> <output-dir> [ffi-version]
#     platform:   linux-x64 | linux-arm64 | win-x64 | win-arm64 | osx-x64 | osx-arm64
#     ffi-version: defaults to the version validated against Livekit.Rtc.Dotnet 0.1.3
set -euo pipefail

PLATFORM="${1:?platform required (e.g. win-x64)}"
OUTDIR="${2:?output directory required}"
FFI_VERSION="${3:-livekit-ffi/v0.12.65}"

case "${PLATFORM}" in
  linux-x64)   ASSET="ffi-linux-x86_64";   LIB="liblivekit_ffi.so" ;;
  linux-arm64) ASSET="ffi-linux-arm64";    LIB="liblivekit_ffi.so" ;;
  win-x64)     ASSET="ffi-windows-x86_64"; LIB="livekit_ffi.dll" ;;
  win-arm64)   ASSET="ffi-windows-arm64";  LIB="livekit_ffi.dll" ;;
  osx-x64)     ASSET="ffi-macos-x86_64";   LIB="liblivekit_ffi.dylib" ;;
  osx-arm64)   ASSET="ffi-macos-arm64";    LIB="liblivekit_ffi.dylib" ;;
  *) echo "Unsupported platform: ${PLATFORM}" >&2; exit 1 ;;
esac

ENC="${FFI_VERSION//\//%2F}"
URL="https://github.com/livekit/rust-sdks/releases/download/${ENC}/${ASSET}.zip"

mkdir -p "${OUTDIR}"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

echo "Downloading ${FFI_VERSION} (${ASSET}) ..."
curl -sSL -o "${TMP}/ffi.zip" "${URL}"
unzip -q "${TMP}/ffi.zip" -d "${TMP}/ffi"

SRC="$(find "${TMP}/ffi" -name "${LIB}" | head -n1)"
[ -n "${SRC}" ] || { echo "ERROR: ${LIB} not found in archive" >&2; exit 1; }
install -m 755 "${SRC}" "${OUTDIR}/${LIB}"
echo "Installed ${OUTDIR}/${LIB}"
