#!/bin/bash
# Builds PROCTOR-Teacher-iOS.ipa: the teacher app for iPhones and iPads.
#
# Runs on a Mac (Apple only lets Xcode's tools sign iOS apps) with:
#   * Xcode (open it once to accept the license), the .NET 10 SDK and: sudo dotnet workload install ios
#   * In Xcode > Settings > Accounts, the Apple developer account signed in, and a distribution
#     certificate + provisioning profile for the app's bundle id (see README-mobile.md).
#
# Usage:
#   PROCTOR/installer/Build-iOSApp.sh -p "PROCTOR Teacher Ad Hoc"
#   PROCTOR/installer/Build-iOSApp.sh -p "PROCTOR Teacher App Store" -k "Apple Distribution: My School (ABCDE12345)"
#
#   -p NAME  provisioning profile name (required; Ad Hoc, App Store or Enterprise)
#   -k NAME  signing identity (default: "Apple Distribution", the first such certificate in the keychain)
#   -b ID    bundle id (default com.proctor.teacher; must match the profile's App ID)
#   -m       include the multicast entitlement (only once Apple has granted it for the bundle id)
#   -o DIR   output folder (default PROCTOR/dist)
#
# The version is ProctorVersion in Directory.Build.props at the repository root.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
project="$root/PROCTORSystemMulti-ProjectApp/PROCTORSystemMulti-ProjectApp.iOS/PROCTORSystemMulti-ProjectApp.iOS.csproj"

profile=""
identity="Apple Distribution"
bundle=""
multicast="false"
out="$root/PROCTOR/dist"

while getopts "p:k:b:mo:h" opt; do
  case "$opt" in
    p) profile="$OPTARG" ;;
    k) identity="$OPTARG" ;;
    b) bundle="$OPTARG" ;;
    m) multicast="true" ;;
    o) out="$OPTARG" ;;
    *) sed -n '2,20p' "$0"; exit 1 ;;
  esac
done

[ "$(uname)" = "Darwin" ] || { echo "Run this on a Mac." >&2; exit 1; }
command -v dotnet >/dev/null || { echo "The .NET SDK isn't installed: https://dot.net" >&2; exit 1; }
xcode-select -p >/dev/null 2>&1 || { echo "Xcode isn't installed (App Store), or run: sudo xcode-select -s /Applications/Xcode.app" >&2; exit 1; }
[ -n "$profile" ] || { echo "Give the provisioning profile name: -p \"PROCTOR Teacher Ad Hoc\"" >&2; exit 1; }

version="$(sed -n 's:.*<ProctorVersion>\(.*\)</ProctorVersion>.*:\1:p' "$root/Directory.Build.props" | head -1)"

args=(-c Release -f net10.0-ios -r ios-arm64 -nologo -v m
      -p:ArchiveOnBuild=true
      "-p:CodesignKey=$identity"
      "-p:CodesignProvision=$profile"
      "-p:ProctorMulticast=$multicast")
[ -n "$bundle" ] && args+=("-p:ApplicationId=$bundle")

publish_dir="$root/PROCTORSystemMulti-ProjectApp/PROCTORSystemMulti-ProjectApp.iOS/bin/Release/net10.0-ios/ios-arm64/publish"
rm -rf "$publish_dir"

echo "Building the iOS teacher app $version (several minutes)..."
dotnet publish "$project" "${args[@]}"

ipa="$(ls "$publish_dir"/*.ipa 2>/dev/null | head -1 || true)"
[ -n "$ipa" ] || { echo "No .ipa in $publish_dir." >&2; exit 1; }

mkdir -p "$out"
target="$(cd "$out" && pwd)/PROCTOR-Teacher-iOS.ipa"
cp -f "$ipa" "$target"
hash="$(shasum -a 256 "$target" | cut -d' ' -f1)"
printf '%s  PROCTOR-Teacher-iOS.ipa\n' "$hash" > "$target.sha256"

echo
echo "App ready: $target"
echo "  Version : $version"
echo "  Size    : $(du -m "$target" | cut -f1) MB"
echo "  SHA-256 : $hash"
echo
echo "Install: App Store profile -> upload with Transporter (TestFlight / App Store)."
echo "         Ad Hoc profile    -> Apple Configurator, or Xcode > Devices and Simulators (+)."
