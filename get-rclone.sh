#!/usr/bin/env bash
# Fetches rclone for macOS and puts it into bin/:
#
#   bin/rclone-darwin-arm64   for Macs with an Apple chip
#   bin/rclone-darwin-amd64   for Macs with an Intel chip
#
# Imagepixel Drive uses rclone as it is released, without changes. This script
# downloads the pinned release from the rclone project and compares each file with
# its pinned SHA-256 checksum.
#
# To move to a newer rclone, change the version and take the checksums from the
# SHA256SUMS file of that release. get-rclone.ps1 pins the same version for Windows.
set -euo pipefail

version="v1.76.0"
sum_arm64="e72dc90b70f4947a7b72b61bde4a4fbba5777b2874fb448951ffdde67ebd0ab7"
sum_amd64="4f5835733d52b818340f97b1fc0348a70461534d123363a183ca2b660219595a"

root="$(cd "$(dirname "$0")" && pwd)"
folder="$root/.build/rclone-$version"
mkdir -p "$folder" "$root/bin"

checksum() {
    if command -v sha256sum > /dev/null; then
        sha256sum "$1" | cut -d ' ' -f 1
    else
        shasum -a 256 "$1" | cut -d ' ' -f 1
    fi
}

for arch in arm64 amd64; do
    name="rclone-$version-osx-$arch"
    archive="$folder/$name.zip"
    expected="sum_$arch"

    if [ ! -f "$archive" ]; then
        echo "Fetching rclone $version for $arch ..."
        curl --fail --location --silent --show-error --output "$archive" \
            "https://github.com/rclone/rclone/releases/download/$version/$name.zip"
    fi

    actual="$(checksum "$archive")"
    if [ "$actual" != "${!expected}" ]; then
        rm -f "$archive"
        echo "The checksum of $name.zip is $actual, expected was ${!expected}. The file was removed." >&2
        exit 1
    fi

    unzip -o -q "$archive" -d "$folder"
    cp "$folder/$name/rclone" "$root/bin/rclone-darwin-$arch"
    chmod +x "$root/bin/rclone-darwin-$arch"
    echo "Done: bin/rclone-darwin-$arch"
done
