#!/bin/bash
# Structural validation and unsigned compilation only. Run on the borrowed Mac.
set -euo pipefail
apple_root="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$apple_root"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "This check requires macOS with Xcode and the iOS 26 SDK."
    exit 1
fi

xcodebuild -version
xcrun --sdk iphoneos --show-sdk-version
plutil -lint WanweiAppleLab/Info.plist WanweiAppleLab/WanweiAppleLab.entitlements WanweiAppleLab.xcodeproj/project.pbxproj
xcodebuild -list -project WanweiAppleLab.xcodeproj
xcodebuild \
    -project WanweiAppleLab.xcodeproj \
    -scheme WanweiAppleLab \
    -configuration Debug \
    -sdk iphoneos \
    -destination 'generic/platform=iOS' \
    -derivedDataPath "$apple_root/build" \
    CODE_SIGNING_ALLOWED=NO \
    build
echo "Unsigned build completed. Signing, installation and physical-device checks are still required."
