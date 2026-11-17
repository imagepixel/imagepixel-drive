#!/usr/bin/env bash
# Builds the application for macOS and packs it into disk images, one for Macs with
# an Apple chip and one for Macs with an Intel chip:
#
#   artifacts/macos/ImagepixelDrive-<version>-beta-mac-arm64.dmg
#   artifacts/macos/ImagepixelDrive-<version>-beta-mac-x64.dmg
#
# The version for macOS is a beta, which the names of the disk images say.
#
# Must run on macOS. Requirements: the .NET 10 SDK.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
project="$root/src/ImagepixelDrive.App/ImagepixelDrive.App.csproj"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -n 1)"
if [ -z "$version" ]; then
    echo "No <Version> found in $project" >&2
    exit 1
fi

if [ ! -f "$root/bin/rclone-darwin-arm64" ] || [ ! -f "$root/bin/rclone-darwin-amd64" ]; then
    "$root/get-rclone.sh"
fi

out="$root/artifacts/macos"
rm -rf "$out"
mkdir -p "$out"

# the icon in every size macOS asks for, made from one picture
echo "Making the icon ..."
iconset="$out/app.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/installer/macos/app-icon-1024.png" \
        --out "$iconset/icon_${size}x${size}.png" > /dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$root/installer/macos/app-icon-1024.png" \
        --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil --convert icns "$iconset" --output "$out/app.icns"
rm -rf "$iconset"

for rid in osx-arm64 osx-x64; do
    arch="${rid#osx-}"
    publish="$root/artifacts/publish-$rid"
    rm -rf "$publish"

    echo "Publishing the application for $rid ..."
    dotnet publish "$project" --configuration Release --runtime "$rid" --self-contained true \
        -p:DebugType=none -p:DebugSymbols=false --output "$publish" --nologo --verbosity quiet

    # the layout macOS expects of an application
    image="$out/$arch"
    app="$image/Imagepixel Drive.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$publish/." "$app/Contents/MacOS/"
    cp "$out/app.icns" "$app/Contents/Resources/app.icns"
    cp "$root/LICENSE" "$app/Contents/Resources/LICENSE.txt"
    cp "$root/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/THIRD-PARTY-NOTICES.txt"
    cp "$root/NUTZUNGSHINWEISE.md" "$app/Contents/Resources/NUTZUNGSHINWEISE.md"
    sed "s/@VERSION@/$version/g" "$root/installer/macos/Info.plist" > "$app/Contents/Info.plist"
    chmod +x "$app/Contents/MacOS/ImagepixelDrive" "$app/Contents/MacOS/rclone"

    # Without a certificate of an Apple developer account the application gets an
    # ad-hoc signature. Macs with an Apple chip refuse to run programs without any.
    codesign --force --deep --sign - "$app"

    # the disk image shows the application next to a shortcut to the Applications folder
    ln -s /Applications "$image/Applications"
    dmg="$out/ImagepixelDrive-$version-beta-mac-$arch.dmg"
    hdiutil create -quiet -volname "Imagepixel Drive Beta" -srcfolder "$image" -ov -format UDZO "$dmg"
    rm -rf "$image"
    echo "Done: $dmg"
done

rm -f "$out/app.icns"
