#!/usr/bin/env bash
# Takes the application out of its disk image, as a customer would, and checks that
# it runs: the signature is intact, the rclone inside starts, the application stays
# up, and a second start gives way to the one that is running.
#
#   installer/macos/smoke-test.sh [arm64|x64]
#
# Without an argument the disk image for the chip of this Mac is tested. Must run on
# macOS, after build-macos.sh. A picture of the screen is saved to artifacts/screenshots.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"

arch="${1:-$(uname -m)}"
if [ "$arch" = "x86_64" ]; then
    arch="x64"
fi

dmg="$(ls "$root"/artifacts/macos/ImagepixelDrive-*-mac-"$arch".dmg | head -n 1)"
echo "Testing $dmg"

work="$(mktemp -d)"
volume="$work/volume"
app="$work/Imagepixel Drive.app"
first=""

cleanup() {
    if [ -n "$first" ]; then
        kill "$first" 2> /dev/null || true
    fi
    hdiutil detach "$volume" -force -quiet 2> /dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT

# Right after a disk image was opened or read, macOS may still be looking at it and
# answers "Resource busy". That passes, so both steps are tried a few times; the last
# try shows what the system has to say.
attach() {
    for _ in 1 2 3 4; do
        if hdiutil attach "$dmg" -nobrowse -readonly -mountpoint "$volume" -quiet 2> /dev/null; then
            return 0
        fi
        sleep 3
    done
    hdiutil attach "$dmg" -nobrowse -readonly -mountpoint "$volume"
}

detach() {
    for _ in 1 2 3 4; do
        if hdiutil detach "$volume" -quiet 2> /dev/null; then
            return 0
        fi
        sleep 3
    done
    hdiutil detach "$volume" -force
}

mkdir -p "$volume"
echo "Opening the disk image ..."
attach
if [ ! -L "$volume/Applications" ]; then
    echo "The disk image has no link to the folder for programs." >&2
    exit 1
fi
echo "Copying the application out of it ..."
cp -R "$volume/Imagepixel Drive.app" "$work/"
detach

echo "Checking the signature ..."
codesign --verify --deep --strict --verbose=2 "$app"

echo "Starting the rclone inside ..."
"$app/Contents/MacOS/rclone" version

# the settings of this test go to a folder of their own
export IMAGEPIXEL_DRIVE_DATA="$work/settings"

echo "Starting the application ..."
"$app/Contents/MacOS/ImagepixelDrive" &
first=$!
sleep 20
if ! kill -0 "$first" 2> /dev/null; then
    echo "The application did not stay up." >&2
    exit 1
fi

mkdir -p "$root/artifacts/screenshots"
screencapture -x "$root/artifacts/screenshots/macos-$arch.png" \
    || echo "No picture of the screen could be taken."

echo "Starting it a second time ..."
"$app/Contents/MacOS/ImagepixelDrive" --minimized &
second=$!
sleep 15
if kill -0 "$second" 2> /dev/null; then
    kill "$second" 2> /dev/null || true
    echo "A second start kept running next to the first." >&2
    exit 1
fi
if ! kill -0 "$first" 2> /dev/null; then
    echo "The application ended when it was started a second time." >&2
    exit 1
fi

echo "The application for $arch runs."
